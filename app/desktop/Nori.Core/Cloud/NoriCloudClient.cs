using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nori.Core.Cloud;

/// <summary>一份待确认的法务文档。</summary>
public sealed record LegalDocument
{
	/// <summary>文档标识（<c>tos</c> / <c>aup</c> / <c>disclaimer</c> / <c>cloud-tos</c> / <c>cloud-privacy</c>）。</summary>
	[JsonPropertyName("key")]
	public string Key { get; init; } = "";

	/// <summary>标题。展示用。</summary>
	[JsonPropertyName("title")]
	public string Title { get; init; } = "";

	/// <summary>版本号。展示用。</summary>
	[JsonPropertyName("version")]
	public string Version { get; init; } = "";

	/// <summary>
	/// 内容哈希。**同意就是提交这个值** —— 服务端逐字节比对当前文档的哈希，
	/// 对不上即视为同意的是旧版本。所以它不能由客户端凑，只能原样回传。
	/// </summary>
	[JsonPropertyName("sha256")]
	public string Sha256 { get; init; } = "";
}

/// <summary>云端存档的一份快照，或者「云端没有」。</summary>
public sealed record CloudSaveSnapshot
{
	/// <summary>云端有没有存档。请求本身失败时为 false，用 <see cref="Error"/> 区分。</summary>
	public bool Present { get; init; }

	/// <summary>云端当前的版本号。上传时要带上它，否则会覆盖掉别的机器刚存的。</summary>
	public int Revision { get; init; }

	/// <summary>生成这份存档的机器的时钟。给人认的，不参与判断。</summary>
	public string SavedAt { get; init; } = "";

	/// <summary>服务端收到的时刻。排查时以它为准。</summary>
	public string ReceivedAt { get; init; } = "";

	/// <summary>生成它的客户端版本。</summary>
	public string AppVersion { get; init; } = "";

	/// <summary>序列化后的字节数。</summary>
	public int Bytes { get; init; }

	/// <summary>文档本身。只取元信息时为 null。</summary>
	public CloudSaveDocument? Document { get; init; }

	/// <summary>可直接展示的失败说明；成功时为空串。</summary>
	public string Error { get; init; } = "";

	/// <summary>
	/// 服务端判定这个令牌不再作数（会话过期，或已在别处注销）。
	///
	/// 单独给出来而不是只留一句错误说明：调用方要据此清掉本机登录态。缺这一条的症状是
	/// 界面一直显示已登录，同步按钮一直可点，而每一次都失败在同一句话上。
	/// </summary>
	public bool Expired { get; init; }

	/// <summary>请求本身成不成。与「云端有没有存档」是两件事。</summary>
	public bool Ok => Error.Length == 0;
}

/// <summary>一次上传的结果。</summary>
public sealed record CloudSaveUploadResult
{
	public bool Ok { get; init; }

	/// <summary>同 <see cref="CloudSaveSnapshot.Expired"/>。</summary>
	public bool Expired { get; init; }

	/// <summary>成功时是新版本号；冲突时是**云端当前的**版本号。</summary>
	public int Revision { get; init; }

	public string SavedAt { get; init; } = "";

	/// <summary>
	/// 云端比本机手上那份新。
	///
	/// 这不是错误而是一次正常的并发结果 —— 另一台机器先存了。调用方要把选择权交给
	/// 用户（覆盖还是先取回来），不能默默重试，那等于把版本号这道闸拆掉。
	/// </summary>
	public bool Conflict { get; init; }

	public string Error { get; init; } = "";
}

/// <summary>一次认证请求的结果。</summary>
public sealed record CloudAuthResult
{
	/// <summary>登录完成。</summary>
	public bool Ok { get; init; }

	/// <summary>本次只是把验证码发出去了，尚未登录。</summary>
	public bool CodeSent { get; init; }

	/// <summary>可直接展示的失败说明；成功时为空串。</summary>
	public string Error { get; init; } = "";

	/// <summary>
	/// 需要先同意法务文档。调用方应展示 <see cref="Legal"/>，取得明确同意后带
	/// <c>accept</c> 重新提交同一个验证码（服务端在这一步不消耗验证码）。
	/// </summary>
	public bool ConsentRequired { get; init; }

	/// <summary>要确认的文档清单。</summary>
	public IReadOnlyList<LegalDocument> Legal { get; init; } = [];

	/// <summary>
	/// 此前同意过、只是版本落后。
	///
	/// 界面文案要据此分叉：对一个早就注册过、只因文档改版被拦下的人说「请先阅读并
	/// 同意以下条款」，他不知道自己为什么又被拦。
	/// </summary>
	public bool ConsentIsRenewal { get; init; }

	/// <summary>账户已停用。</summary>
	public bool Banned { get; init; }

	/// <summary>申诉链接；仅在 <see cref="Banned"/> 为真时有值。</summary>
	public string AppealUrl { get; init; } = "";

	/// <summary>登录成功时的账户。</summary>
	public CloudAccount? Account { get; init; }

	internal static CloudAuthResult Fail(string message) => new() {Error = message};
}

/// <summary>
/// Nori 云端的认证客户端。
///
/// ── 只负责一次请求 ────────────────────────────────────────────────────────
/// 不持有登录态（那是 <see cref="AccountSession"/>），不决定界面怎么走（那是调用方）。
/// 这样整条登录流程可以在没有服务端的条件下测试 —— 传一个 <see cref="HttpMessageHandler"/>
/// 进来即可。
///
/// ── 为什么用 Bearer 而不是 cookie ─────────────────────────────────────────
/// 服务端 <c>accountOf()</c> 先读 cookie、再读 <c>Authorization: Bearer</c>。桌面端没有
/// cookie 容器可依赖（<see cref="HttpClient"/> 的容器不跨进程重启），令牌本来就要自己
/// 存，所以直接走 Bearer 这条 —— 它是服务端为跨域交接留的正式入口，不是绕路。
///
/// ── 错误文案 ──────────────────────────────────────────────────────────────
/// 服务端回的是 <c>bad_code</c> 这类机器码。把它原样显示等于什么都没说，所以在这里
/// 逐条翻成**能指出该改哪一项**的话。翻不了的（服务端新增而这里还没跟上）退回一句
/// 带原始码的说明，而不是「未知错误」—— 后者让人没法报障。
/// </summary>
public sealed class NoriCloudClient
{
	/// <summary>默认服务地址。</summary>
	public const string DefaultBaseUrl = "https://inori.nyco.cloud";

	/// <summary>
	/// 覆盖服务地址的配置键。留给自建部署与联调，不在界面上暴露。
	///
	/// 不进云存档：它指向某个具体部署，跟着账户跑会让另一台机器连到错误的服务端。
	/// </summary>
	public const string BaseUrlKey = "cloud_base_url";

	private static readonly JsonSerializerOptions Json = new()
	{
		PropertyNameCaseInsensitive = true,
	};

	private readonly HttpClient _http;
	private readonly string _baseUrl;

	public NoriCloudClient(HttpClient http, string baseUrl = DefaultBaseUrl)
	{
		_http = http;
		_baseUrl = baseUrl.TrimEnd('/');
	}

	/// <summary>
	/// 发送登录验证码。
	///
	/// 这个接口对任何邮箱的回应完全一致 —— 服务端刻意不查账户是否存在（见
	/// <see cref="SignInForm"/>）。所以这里也不要根据回应去推断什么。
	/// </summary>
	public async Task<CloudAuthResult> SendCodeAsync(string email, CancellationToken cancel = default)
	{
		using HttpResponseMessage response = await PostAsync("/api/nori-auth/code",
			new() {["email"] = email}, null, cancel);
		JsonElement body = await ReadAsync(response, cancel);

		if (response.IsSuccessStatusCode)
		{
			return new CloudAuthResult {CodeSent = true, Legal = ReadLegal(body)};
		}
		return CloudAuthResult.Fail(Describe(response.StatusCode, ErrorCode(body), body));
	}

	/// <summary>
	/// 用验证码登录。
	///
	/// <paramref name="accept"/> 为「文档标识 → 哈希」。首次提交传 null；服务端回
	/// <see cref="CloudAuthResult.ConsentRequired"/> 之后，取得同意再带着它用**同一个
	/// 验证码**重提 —— 缺同意那一次服务端不消耗验证码，正是为了这个。
	/// </summary>
	public async Task<CloudAuthResult> VerifyCodeAsync(
		string email, string code,
		IReadOnlyDictionary<string, string>? accept = null,
		CancellationToken cancel = default)
	{
		using HttpResponseMessage response = await PostAsync("/api/nori-auth/verify",
			new() {["email"] = email, ["code"] = code}, accept, cancel);
		return await ReadAuthAsync(response, email, cancel);
	}

	/// <summary>用密码登录。</summary>
	public async Task<CloudAuthResult> SignInWithPasswordAsync(
		string email, string password,
		IReadOnlyDictionary<string, string>? accept = null,
		CancellationToken cancel = default)
	{
		using HttpResponseMessage response = await PostAsync("/api/nori-auth/password",
			new() {["email"] = email, ["password"] = password}, accept, cancel);
		return await ReadAuthAsync(response, email, cancel);
	}

	/// <summary>
	/// 退出登录，让服务端作废这个会话。
	///
	/// 失败不抛：本机那份记录**无论如何都要清掉**。服务端没收到的后果是一个令牌留到
	/// 自然过期，而本机清不掉的后果是用户点了退出却还显示已登录。
	/// </summary>
	public async Task SignOutAsync(string token, CancellationToken cancel = default)
	{
		try
		{
			using HttpRequestMessage request = new(HttpMethod.Post, _baseUrl + "/api/nori-auth/sign-out");
			request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
			using HttpResponseMessage response = await _http.SendAsync(request, cancel);
		}
		catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
		{
			// 见方法注释：这里没有可做的补救，也不该把它变成用户看得见的失败。
		}
	}

	/// <summary>每个文档的公开地址。条款近 80 KB，在 460px 的窗口里读不了，交给系统浏览器。</summary>
	public string LegalUrl(string key) => $"{_baseUrl}/legal/{key}";

	/* ── 云存档 ───────────────────────────────────────────────────────────────
	 * 与认证共用基址、JSON 设置与错误文案表。拆成第二个类型的话，那三样各有一份，
	 * 而"服务端换了个错误码"这种变化要改两处才生效，漏一处不会报错。
	 */

	private const string SavePath = "/api/nori-cloud-save";

	/// <summary>
	/// 取云端存档。
	///
	/// <paramref name="metaOnly"/> 为真时只要元信息 —— 界面上那一行「云端存档：某时」
	/// 要在用户决定是否恢复**之前**显示，为它拉一份 4MB 的文档在移动网络下会很慢。
	/// </summary>
	public async Task<CloudSaveSnapshot> FetchSaveAsync(
		string token, bool metaOnly = false, CancellationToken cancel = default)
	{
		using HttpRequestMessage request = new(HttpMethod.Get,
			_baseUrl + SavePath + (metaOnly ? "?meta=1" : ""));
		request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

		HttpResponseMessage response;
		try
		{
			response = await _http.SendAsync(request, cancel);
		}
		catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
		{
			return new CloudSaveSnapshot {Error = Transport(error)};
		}

		using (response)
		{
			JsonElement body = await ReadAsync(response, cancel);
			if (!response.IsSuccessStatusCode)
			{
				string code = ErrorCode(body);
				return new CloudSaveSnapshot
				{
					Error = Describe(response.StatusCode, code, body),
					Expired = IsExpired(code),
				};
			}
			if (!body.TryGetProperty("present", out JsonElement present)
				|| present.ValueKind != JsonValueKind.True)
			{
				return new CloudSaveSnapshot {Present = false};
			}
			return new CloudSaveSnapshot
			{
				Present = true,
				Revision = Int(body, "revision"),
				SavedAt = Str(body, "savedAt"),
				ReceivedAt = Str(body, "receivedAt"),
				AppVersion = Str(body, "appVersion"),
				Bytes = Int(body, "bytes"),
				Document = ReadDocument(body),
			};
		}
	}

	/// <summary>
	/// 上传一份存档。
	///
	/// <paramref name="documentJson"/> 是 <c>CloudSaveService.Serialize</c> 的原样输出，
	/// 不是一个对象 —— 这里必须发**一模一样的字节**：客户端是拿那串字节算的大小并据此
	/// 判断有没有超限，重新序列化一次（哪怕只是空值处理不同）就会让那个判断对不上。
	///
	/// <paramref name="ifRevision"/> 传本机手上那份的版本号；云端更新时返回冲突。
	/// 传 null 表示明确覆盖，只应在用户选了「以本机为准」时使用。
	/// </summary>
	public async Task<CloudSaveUploadResult> UploadSaveAsync(
		string token, string documentJson, int? ifRevision, CancellationToken cancel = default)
	{
		string envelope = ifRevision is {} revision
			? $$"""{"document":{{documentJson}},"ifRevision":{{revision}}}"""
			: $$"""{"document":{{documentJson}}}""";

		using HttpRequestMessage request = new(HttpMethod.Post, _baseUrl + SavePath)
		{
			Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
		};
		request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

		HttpResponseMessage response;
		try
		{
			response = await _http.SendAsync(request, cancel);
		}
		catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
		{
			return new CloudSaveUploadResult {Error = Transport(error)};
		}

		using (response)
		{
			JsonElement body = await ReadAsync(response, cancel);
			if (response.StatusCode == HttpStatusCode.Conflict)
			{
				return new CloudSaveUploadResult
				{
					Conflict = true,
					Revision = Int(body, "revision"),
					SavedAt = Str(body, "savedAt"),
					Error = "云端已有更新的存档",
				};
			}
			if (!response.IsSuccessStatusCode)
			{
				string code = ErrorCode(body);
				return new CloudSaveUploadResult
				{
					Error = Describe(response.StatusCode, code, body),
					Expired = IsExpired(code),
				};
			}
			return new CloudSaveUploadResult
			{
				Ok = true,
				Revision = Int(body, "revision"),
				SavedAt = Str(body, "savedAt"),
			};
		}
	}

	/// <summary>
	/// 删除云端存档。
	///
	/// 走 POST 而不是 DELETE：服务端只对 POST 读请求体，而删除需要一个明确的确认字段。
	/// </summary>
	public async Task<CloudSaveUploadResult> DeleteSaveAsync(string token, CancellationToken cancel = default)
	{
		using HttpRequestMessage request = new(HttpMethod.Post, _baseUrl + SavePath)
		{
			Content = new StringContent("""{"action":"delete","confirm":"DELETE"}""",
				Encoding.UTF8, "application/json"),
		};
		request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

		HttpResponseMessage response;
		try
		{
			response = await _http.SendAsync(request, cancel);
		}
		catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
		{
			return new CloudSaveUploadResult {Error = Transport(error)};
		}

		using (response)
		{
			JsonElement body = await ReadAsync(response, cancel);
			return response.IsSuccessStatusCode
				? new CloudSaveUploadResult {Ok = true}
				: Failed(response.StatusCode, ErrorCode(body), body);
		}
	}

	private static CloudSaveDocument? ReadDocument(JsonElement body)
	{
		if (!body.TryGetProperty("document", out JsonElement document)
			|| document.ValueKind != JsonValueKind.Object) return null;
		try
		{
			return document.Deserialize<CloudSaveDocument>(Json);
		}
		catch (JsonException)
		{
			// 云端那份解析不了。当成「没有」比抛出去好：调用方能做的只有覆盖它，
			// 而那条路在「云端没有」时本来就通。
			return null;
		}
	}

	private static string Transport(Exception error) => error switch
	{
		TaskCanceledException or TimeoutException => "服务器无响应，请稍后重试",
		_ => "网络不可用，请检查网络连接后重试",
	};

	private static int Int(JsonElement body, string name) =>
		body.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number) ? number : 0;

	/// <summary>
	/// 发一次 POST。
	///
	/// 收字典而不是匿名对象：匿名对象的属性名由变量名决定，重命名一个局部变量就会
	/// 悄悄改掉线上的字段名，而编译器不会有任何意见。
	/// </summary>
	private async Task<HttpResponseMessage> PostAsync(
		string path, Dictionary<string, object?> body,
		IReadOnlyDictionary<string, string>? accept,
		CancellationToken cancel)
	{
		// accept 是可选字段，拼在这里而不是每个调用点各拼一遍。
		if (accept is {Count: > 0}) body["accept"] = accept;
		return await _http.PostAsJsonAsync(_baseUrl + path, body, cancel);
	}

	private async Task<CloudAuthResult> ReadAuthAsync(
		HttpResponseMessage response, string email, CancellationToken cancel)
	{
		JsonElement body = await ReadAsync(response, cancel);
		string code = ErrorCode(body);

		if (response.IsSuccessStatusCode)
		{
			string token = Str(body, "session", "token");
			if (token.Length == 0)
			{
				// 服务端回了 200 却没有令牌。继续往下走会存下一份没有令牌的登录态，
				// 之后每一次请求都是 401，而人看到的是「已登录」。
				return CloudAuthResult.Fail("服务器未返回会话凭据，请稍后重试");
			}
			return new CloudAuthResult
			{
				Ok = true,
				Account = new CloudAccount
				{
					Email = Str(body, "user", "email") is {Length: > 0} served ? served : email,
					Name = Str(body, "user", "name"),
					Token = token,
					ExpiresAt = Str(body, "session", "expiresAt"),
				},
			};
		}

		if (response.StatusCode == HttpStatusCode.Forbidden
			&& code is "consent_required" or "consent_stale")
		{
			return new CloudAuthResult
			{
				ConsentRequired = true,
				Legal = ReadLegal(body),
				ConsentIsRenewal = body.TryGetProperty("renewal", out JsonElement renewal)
					&& renewal.ValueKind == JsonValueKind.True,
			};
		}

		if (response.StatusCode == HttpStatusCode.Forbidden && code == "account_banned")
		{
			string reason = Str(body, "reason");
			return new CloudAuthResult
			{
				Banned = true,
				AppealUrl = Str(body, "appeal"),
				Error = reason.Length > 0 ? $"该账户已被停用。停用理由：{reason}" : "该账户已被停用。",
			};
		}

		return CloudAuthResult.Fail(Describe(response.StatusCode, code, body));
	}

	/// <summary>
	/// 机器码 → 可展示的说明。
	///
	/// 每一条都要指出**该改哪一项**。「验证失败」这种说法会让人重复提交同样的输入。
	/// </summary>
	/// <summary>
	/// 这个码是不是「令牌不作数了」。
	///
	/// 只认 <c>not_signed_in</c> 这一个：它是服务端对无效或过期令牌的固定回应。网络不通、
	/// 维护中、写入失败都不算 —— 把那些也当成过期的话，服务端抖一下就会把人登出。
	/// </summary>
	private static bool IsExpired(string code) => code == "not_signed_in";

	private static CloudSaveUploadResult Failed(HttpStatusCode status, string code, JsonElement body) =>
		new() {Error = Describe(status, code, body), Expired = IsExpired(code)};

	internal static string Describe(HttpStatusCode status, string code, JsonElement body) => code switch
	{
		"bad_email" => "邮箱地址格式不正确",
		"bad_code" or "code_expired" => "验证码不正确或已过期，请重新获取",
		"too_many_tries" => "验证码尝试次数过多，请重新获取",
		"bad_credentials" => "邮箱或密码不正确",
		"registration_closed" => "该服务当前不开放注册",
		"too_many_requests" => RetryHint(body),
		"legal_unavailable" => "服务端暂时无法提供条款文本，请稍后重试",
		"maintenance" => "服务正在维护中，请稍后重试",
		"bad_json" => "请求格式不正确，请更新客户端后重试",
		// ── 云存档 ────────────────────────────────────────────────────────
		"not_signed_in" => "登录已过期，请重新登录",
		"not_configured" => "服务端未启用云端同步",
		"too_large" => TooLargeHint(body),
		"bad_format" or "bad_document" => "存档格式不受服务端支持，请更新客户端后重试",
		"write_failed" => "服务端写入失败，请稍后重试",
		"" => $"服务器返回 {(int)status}，请稍后重试",
		// 服务端新增了这里还没跟上的错误码。带上原始码 —— 「未知错误」没法报障。
		_ => $"登录失败（{code}）",
	};

	/// <summary>超限要说清楚超了多少 —— 只说「太大了」的话，用户不知道该删掉多少东西。</summary>
	private static string TooLargeHint(JsonElement body)
	{
		if (body.TryGetProperty("bytes", out JsonElement sent) && sent.TryGetInt32(out int bytes)
			&& body.TryGetProperty("limit", out JsonElement cap) && cap.TryGetInt32(out int limit)
			&& limit > 0)
		{
			return $"存档 {bytes / 1024} KB，超出上限 {limit / 1024} KB。请减少记忆或提醒后重试";
		}
		return "存档超出服务端上限";
	}

	/// <summary>限流的提示要带上大概多久之后能再试，否则用户只能反复点。</summary>
	private static string RetryHint(JsonElement body)
	{
		if (body.TryGetProperty("retryAfter", out JsonElement after)
			&& after.TryGetInt32(out int seconds) && seconds > 0)
		{
			int minutes = Math.Max(1, (int)Math.Ceiling(seconds / 60.0));
			return $"请求过于频繁，请在约 {minutes} 分钟后重试";
		}
		return "请求过于频繁，请稍后重试";
	}

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken cancel)
	{
		try
		{
			string text = await response.Content.ReadAsStringAsync(cancel);
			if (text.Length == 0) return default;
			using JsonDocument document = JsonDocument.Parse(text);
			return document.RootElement.Clone();
		}
		catch (Exception error) when (error is JsonException or InvalidOperationException)
		{
			// 服务端出错时 nginx 可能回一张 HTML 错误页。解析不了不该变成崩溃 ——
			// 交给 Describe，它会按状态码给一句能看的说明。
			return default;
		}
	}

	private static string ErrorCode(JsonElement body) => Str(body, "error");

	private static IReadOnlyList<LegalDocument> ReadLegal(JsonElement body)
	{
		if (!body.TryGetProperty("legal", out JsonElement legal) || legal.ValueKind != JsonValueKind.Array)
		{
			return [];
		}
		try
		{
			return legal.Deserialize<List<LegalDocument>>(Json) ?? [];
		}
		catch (JsonException)
		{
			return [];
		}
	}

	private static string Str(JsonElement body, params string[] path)
	{
		JsonElement cursor = body;
		foreach (string segment in path)
		{
			if (cursor.ValueKind != JsonValueKind.Object
				|| !cursor.TryGetProperty(segment, out JsonElement next)) return "";
			cursor = next;
		}
		return cursor.ValueKind == JsonValueKind.String ? cursor.GetString() ?? "" : "";
	}
}
