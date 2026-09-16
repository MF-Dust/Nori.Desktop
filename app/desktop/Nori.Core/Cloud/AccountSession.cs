using Nori.Core.Configuration;

namespace Nori.Core.Cloud;

/// <summary>已登录账户的一份快照。纯数据。</summary>
public sealed record CloudAccount
{
	/// <summary>账户邮箱。界面上显示的就是它 —— 显示名可空，邮箱不会。</summary>
	public required string Email { get; init; }

	/// <summary>显示名。服务端允许为空。</summary>
	public string Name { get; init; } = "";

	/// <summary>会话令牌。请求时放进 <c>Authorization: Bearer</c>。</summary>
	public required string Token { get; init; }

	/// <summary>会话到期时刻（ISO 8601 UTC）。服务端给什么就存什么，不自己解析。</summary>
	public string ExpiresAt { get; init; } = "";
}

/// <summary>
/// 登录状态的落盘。
///
/// ── 为什么键名都以 <c>_token</c> 结尾的那个必须叫这个名字 ──────────────────
/// <see cref="ConfigStore.IsSensitiveKey"/> 按后缀判断要不要加密，<c>_token</c> 是其中
/// 一条。所以 <c>cloud_session_token</c> 这个名字不是随便起的：改成 <c>cloud_session</c>
/// 之类，令牌就会以明文落进 SQLite，而且**不会有任何报错**。
///
/// ── 为什么这些键一个都不进云存档 ──────────────────────────────────────────
/// <see cref="CloudSaveScope"/> 是白名单，不加就不同步，所以这里本来就是安全的。
/// 但理由仍然写进了 <c>ExcludedWithReason</c>：会话是「这台机器登录过」，把它同步到
/// 另一台等于把登录态一起搬过去，而用户只是想同步偏好。
/// </summary>
public sealed class AccountSession(ConfigStore config)
{
	internal const string TokenKey = "cloud_session_token";
	internal const string EmailKey = "cloud_account_email";
	internal const string NameKey = "cloud_account_name";
	internal const string ExpiresKey = "cloud_session_expires_at";
	internal const string MethodKey = "cloud_sign_in_method";

	/// <summary>
	/// 存取时加在值前面的一个字符，取出时去掉。
	///
	/// <see cref="ConfigValue.FromStorage"/> **读取时重新推断类型**：<c>"0001"</c> 会被读成
	/// 整数 1，再格式化回来就成了 <c>"1"</c>；<c>"true"</c> 会读成布尔。会话令牌少一个前导零
	/// 不会报任何错 —— 它只会让之后每一次请求都 401，而本机界面显示的是「已登录」。
	///
	/// 加一个非数字前缀，推断就永远落到字符串那一档。代价是库里的值多两个字节。
	/// </summary>
	private const string Guard = "s:";

	/// <summary>当前账户；未登录时为 null。</summary>
	public CloudAccount? Current
	{
		get
		{
			string token = Read(TokenKey);
			string email = Read(EmailKey);
			// 两者缺一都视为未登录：只有邮箱没有令牌发不出请求，只有令牌没有邮箱
			// 界面上显示不出登录的是谁。半份记录比没有记录更难排查。
			if (token.Length == 0 || email.Length == 0) return null;
			return new CloudAccount
			{
				Email = email,
				Name = Read(NameKey),
				Token = token,
				ExpiresAt = Read(ExpiresKey),
			};
		}
	}

	/// <summary>是否已登录。</summary>
	public bool IsSignedIn => Current is not null;

	/// <summary>
	/// 本机上次使用的登录方式。
	///
	/// 记的是**本机历史**，不是服务端状态 —— 因此不构成账号枚举（见
	/// <see cref="SignInForm"/> 的类型注释）。默认验证码方式：当前线上账户多数未设置
	/// 密码，默认展示密码输入会让他们先经历一次失败提交。
	/// </summary>
	public SignInMethod LastMethod =>
		Read(MethodKey) == "password" ? SignInMethod.Password : SignInMethod.Code;

	/// <summary>
	/// 登录成功后落盘。
	///
	/// 换了账户要把本机记的云端版本号清零。<see cref="CloudSyncService"/> 那个版本号是
	/// **对某一个账户的存档**而言的：拿 A 账户的第 7 版去 B 账户上传，服务端按 B 的当前
	/// 版本（新账户是 0）比对，回 409。客户端把 409 解释成「云端有更新的存档」，
	/// 而 B 根本没有存档 —— 这句话不成立，而用户只能被迫选覆盖。
	///
	/// 退出再用同一个账户登录不清零：那个版本号仍然成立，清掉只会让下一次上传多走一次
	/// 覆盖确认。
	/// </summary>
	public void Save(CloudAccount account, SignInMethod method)
	{
		if (Read(EmailKey) is {Length: > 0} previous
			&& !previous.Equals(account.Email, StringComparison.OrdinalIgnoreCase))
		{
			config.Set(CloudSyncService.RevisionKey, new ConfigValue.Text("r0"));
		}

		Write(TokenKey, account.Token);
		Write(EmailKey, account.Email);
		Write(NameKey, account.Name);
		Write(ExpiresKey, account.ExpiresAt);
		Write(MethodKey, method == SignInMethod.Password ? "password" : "code");
	}

	/// <summary>
	/// 退出登录。
	///
	/// 令牌、邮箱、显示名、到期时刻全部清掉；**登录方式保留** —— 它是本机偏好，
	/// 下次打开登录窗仍应停在上次用的那一档，退出登录不是要把这个偏好也忘掉。
	/// </summary>
	public void Clear()
	{
		Write(TokenKey, "");
		Write(EmailKey, "");
		Write(NameKey, "");
		Write(ExpiresKey, "");
	}

	private void Write(string key, string value) => config.Set(key, new ConfigValue.Text(Guard + value));

	private string Read(string key)
	{
		string raw = config.GetStringOr(key, "");
		// 前缀缺失说明这个键不是本类写的（或来自更早的版本）。原样返回而不是当成错误：
		// 这条路径上唯一的后果是可能被类型推断改写过，而那比读不出来要好。
		return raw.StartsWith(Guard, StringComparison.Ordinal) ? raw[Guard.Length..] : raw;
	}
}
