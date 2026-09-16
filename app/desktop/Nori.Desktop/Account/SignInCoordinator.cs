using Nori.Core.Cloud;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Security;

namespace Nori.Desktop.Account;

/// <summary>
/// 把一次表单提交变成一次真实请求。
///
/// ── 为什么单独一层 ────────────────────────────────────────────────────────
/// <see cref="Windows.AccountWindow"/> 的约定是「窗口不认识网络」：它把表单快照递出来，
/// 拿回一个结果推进状态机。判断逻辑在 <see cref="SignInForm"/>，传输在
/// <see cref="NoriCloudClient"/>，落盘在 <see cref="AccountSession"/> —— 这一层是把三者
/// 接起来的地方，也是**同意流程**唯一的落点。
///
/// ── 同意为什么在这里而不在窗口里 ──────────────────────────────────────────
/// 服务端对缺同意的回应是 403 + 文档清单，而且**这一次不消耗验证码**，就是为了让客户端
/// 取得同意后拿同一个码重提。这个「失败 → 补一个条件 → 重提」的回合对表单状态机是透明的：
/// 它只需要知道最终成没成。把它放进窗口会让窗口多出一个只在这条路径上存在的状态。
/// </summary>
public sealed class SignInCoordinator(
	NoriCloudClient cloud,
	AccountSession session,
	Func<ConsentRequest, Task<bool>> askConsent,
	FileLogger? logger = null)
{
	/// <summary>登录成功后要通知谁。窗口自己也会收到结果，这个是给应用其余部分的。</summary>
	public event Action<CloudAccount>? SignedIn;

	/// <summary>测试用：模拟一次登录成功，用来验证订阅方确实接上了。</summary>
	internal void RaiseSignedInForTests(CloudAccount account) => SignedIn?.Invoke(account);

	/// <summary>本机的登录态。调用方要据此决定菜单里显示「登录」还是「退出登录」。</summary>
	public AccountSession Session => session;

	/// <summary>
	/// 提交一次。
	///
	/// 返回的 <see cref="Windows.SignInOutcome"/> 只有「成了 / 只是发了码 / 失败带原因」
	/// 三种 —— 同意与停用都在这里消化掉，转成其中之一。
	/// </summary>
	public async Task<Windows.SignInOutcome> SubmitAsync(SignInState state, CancellationToken cancel = default)
	{
		// 验证码方式的第一步是发码，不是登录。判据用表单自己的 CodeSent，
		// 不要在这里重新推断 —— 两处推断迟早会分叉。
		if (state.Method == SignInMethod.Code && !state.CodeSent)
		{
			CloudAuthResult sent = await cloud.SendCodeAsync(state.Email, cancel);
			return sent.CodeSent
				? new Windows.SignInOutcome {Ok = true, CodeSent = true}
				: new Windows.SignInOutcome {Ok = false, Error = sent.Error};
		}

		CloudAuthResult result = await AttemptAsync(state, null, cancel);

		if (result.ConsentRequired)
		{
			/*
			 * 服务端把码留着了，所以这里可以取得同意后原样重提。
			 *
			 * 不同意就是一次普通的失败：登录本来就是可选项，不该把它变成一个
			 * 「不同意就关不掉」的窗口。
			 */
			bool agreed = await askConsent(new ConsentRequest
			{
				Documents = result.Legal,
				IsRenewal = result.ConsentIsRenewal,
				UrlOf = cloud.LegalUrl,
			});
			if (!agreed)
			{
				return new Windows.SignInOutcome {Ok = false, Error = "需要同意相关条款后才能登录"};
			}

			Dictionary<string, string> accept = new(StringComparer.Ordinal);
			foreach (LegalDocument document in result.Legal) accept[document.Key] = document.Sha256;
			if (accept.Count == 0)
			{
				// 服务端说缺同意却没给清单。带着空的 accept 重提只会再被拒一次，
				// 变成一个点不动的按钮，所以在这里就说清楚。
				return new Windows.SignInOutcome {Ok = false, Error = "服务端未提供条款清单，请稍后重试"};
			}
			result = await AttemptAsync(state, accept, cancel);
		}

		if (!result.Ok)
		{
			// 停用的说明里已经含理由；申诉链接另外给一句，否则那个 URL 无处可去。
			string message = result.Banned && result.AppealUrl.Length > 0
				? result.Error + "\n可经此链接申诉：" + result.AppealUrl
				: result.Error;
			return new Windows.SignInOutcome {Ok = false, Error = message};
		}

		CloudAccount account = result.Account!;
		try
		{
			session.Save(account, state.Method);
		}
		catch (SecretKeyStoreException error)
		{
			/*
			 * 网络上登录成功了，但令牌存不下来（平台密钥库不可用）。
			 *
			 * 不能报成功：窗口会关掉，而重启之后登录态是空的，用户不知道发生了什么。
			 * 也不能沉默 —— 这是本机环境问题，只有说出来他才可能去处理。
			 */
			logger?.Write(LogSource.Backend, "warn", "登录成功但会话无法落盘: " + error.GetType().Name);
			return new Windows.SignInOutcome
			{
				Ok = false,
				Error = "登录成功，但本机密钥库不可用，凭据无法保存。请检查系统凭据服务后重试",
			};
		}

		logger?.Write(LogSource.Backend, "info", "云端账户已登录");
		SignedIn?.Invoke(account);
		return new Windows.SignInOutcome {Ok = true};
	}

	/// <summary>退出登录。先告诉服务端，再清本机 —— 清本机这一步不会因为前一步失败而跳过。</summary>
	public async Task SignOutAsync(CancellationToken cancel = default)
	{
		CloudAccount? account = session.Current;
		if (account is not null) await cloud.SignOutAsync(account.Token, cancel);
		session.Clear();
		logger?.Write(LogSource.Backend, "info", "云端账户已退出");
	}

	/// <summary>按当前方式发一次登录请求。</summary>
	private Task<CloudAuthResult> AttemptAsync(
		SignInState state, IReadOnlyDictionary<string, string>? accept, CancellationToken cancel) =>
		state.Method == SignInMethod.Password
			? cloud.SignInWithPasswordAsync(state.Email, state.Password, accept, cancel)
			: cloud.VerifyCodeAsync(state.Email, state.Code, accept, cancel);

	/// <summary>按配置装配一个协调器。</summary>
	public static SignInCoordinator Create(
		HttpClient http, ConfigStore config,
		Func<ConsentRequest, Task<bool>> askConsent,
		FileLogger? logger = null)
	{
		string baseUrl = config.GetStringOr(NoriCloudClient.BaseUrlKey, NoriCloudClient.DefaultBaseUrl);
		return new SignInCoordinator(
			new NoriCloudClient(http, baseUrl), new AccountSession(config), askConsent, logger);
	}
}

/// <summary>要用户确认的一组文档。</summary>
public sealed record ConsentRequest
{
	/// <summary>文档清单，顺序即展示顺序。</summary>
	public required IReadOnlyList<LegalDocument> Documents { get; init; }

	/// <summary>
	/// 此前同意过，只是版本变了。
	///
	/// 文案必须据此分叉：对一个早就注册过、只因文档改版被拦下的人说「请先阅读并同意
	/// 以下条款」，他不知道自己为什么又被拦下来。
	/// </summary>
	public bool IsRenewal { get; init; }

	/// <summary>取某份文档的公开地址。条款近 80 KB，在应用窗口里读不了，交给系统浏览器。</summary>
	public required Func<string, string> UrlOf { get; init; }
}
