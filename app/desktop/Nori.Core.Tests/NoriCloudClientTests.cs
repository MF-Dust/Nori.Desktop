using System.Net;
using System.Text;
using System.Text.Json;
using Nori.Core.Cloud;

namespace Nori.Core.Tests;

/// <summary>
/// 认证客户端。
///
/// 这一族测的是**协议契约与错误文案**，不是网络。服务端对每种失败回的是 <c>bad_code</c>
/// 这类机器码，把它原样显示等于什么都没说；而同意与停用两条分支是「登录压根走不完」的
/// 两个最常见原因 —— 它们如果被当成普通失败吞掉，用户看到的是一个反复失败、没有下一步
/// 的按钮。
/// </summary>
public sealed class NoriCloudClientTests
{
	/// <summary>按序回放预设响应，并留下收到的请求供断言。</summary>
	private sealed class FakeHandler(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
	{
		private int _index;

		public List<string> Bodies { get; } = [];
		public List<HttpRequestMessage> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
			(HttpStatusCode status, string body) = replies[Math.Min(_index++, replies.Length - 1)];
			return new HttpResponseMessage(status)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			};
		}
	}

	private sealed class ThrowingHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
			throw new HttpRequestException("网络不可用");
	}

	private static NoriCloudClient Client(HttpMessageHandler handler) =>
		new(new HttpClient(handler), "https://example.test");

	private const string LegalJson =
		"""[{"key":"tos","title":"服务条款","version":"1.1","sha256":"aaa"},{"key":"cloud-privacy","title":"隐私政策","version":"1.1","sha256":"bbb"}]""";

	// ── 发码 ───────────────────────────────────────────────────────────────

	[Fact]
	public async Task 发码成功()
	{
		FakeHandler handler = new((HttpStatusCode.OK, $$"""{"ok":true,"legal":{{LegalJson}},"expiresIn":2700}"""));
		CloudAuthResult result = await Client(handler).SendCodeAsync("a@b.com");

		Assert.True(result.CodeSent);
		Assert.Empty(result.Error);
		Assert.Equal(2, result.Legal.Count);
		Assert.Equal("服务条款", result.Legal[0].Title);
		Assert.Equal("aaa", result.Legal[0].Sha256);
	}

	[Fact]
	public async Task 发码时邮箱不合法()
	{
		FakeHandler handler = new((HttpStatusCode.BadRequest, """{"error":"bad_email"}"""));
		CloudAuthResult result = await Client(handler).SendCodeAsync("nope");

		Assert.False(result.CodeSent);
		Assert.Equal("邮箱地址格式不正确", result.Error);
	}

	/// <summary>限流要说清楚多久之后能再试，否则用户只能反复点同一个按钮。</summary>
	[Fact]
	public async Task 限流带上可重试时间()
	{
		FakeHandler handler = new((HttpStatusCode.TooManyRequests, """{"error":"too_many_requests","retryAfter":600}"""));
		CloudAuthResult result = await Client(handler).SendCodeAsync("a@b.com");

		Assert.Contains("10 分钟", result.Error, StringComparison.Ordinal);
	}

	// ── 验码 ───────────────────────────────────────────────────────────────

	[Fact]
	public async Task 验码成功后带回账户与令牌()
	{
		FakeHandler handler = new((HttpStatusCode.OK, """{"ok":true,"user":{"email":"a@b.com","name":"甲"},"session":{"token":"tok-123","expiresAt":"2027-01-01T00:00:00.000Z"}}"""));
		CloudAuthResult result = await Client(handler).VerifyCodeAsync("a@b.com", "123456");

		Assert.True(result.Ok);
		Assert.NotNull(result.Account);
		Assert.Equal("a@b.com", result.Account.Email);
		Assert.Equal("甲", result.Account.Name);
		Assert.Equal("tok-123", result.Account.Token);
		Assert.Equal("2027-01-01T00:00:00.000Z", result.Account.ExpiresAt);
	}

	/// <summary>
	/// 200 但没有令牌，必须当失败。
	///
	/// 放过去的话会存下一份没有令牌的登录态：之后每个请求都 401，而界面显示「已登录」。
	/// </summary>
	[Fact]
	public async Task 成功响应缺令牌时判为失败()
	{
		FakeHandler handler = new((HttpStatusCode.OK, """{"ok":true,"user":{"email":"a@b.com"}}"""));
		CloudAuthResult result = await Client(handler).VerifyCodeAsync("a@b.com", "123456");

		Assert.False(result.Ok);
		Assert.NotEmpty(result.Error);
	}

	[Fact]
	public async Task 验证码错误()
	{
		FakeHandler handler = new((HttpStatusCode.BadRequest, """{"error":"bad_code"}"""));
		CloudAuthResult result = await Client(handler).VerifyCodeAsync("a@b.com", "000000");

		Assert.False(result.Ok);
		Assert.Contains("验证码", result.Error, StringComparison.Ordinal);
	}

	// ── 同意 ───────────────────────────────────────────────────────────────

	[Fact]
	public async Task 缺同意时带回文档清单()
	{
		FakeHandler handler = new((HttpStatusCode.Forbidden, $$"""{"error":"consent_required","legal":{{LegalJson}}}"""));
		CloudAuthResult result = await Client(handler).VerifyCodeAsync("a@b.com", "123456");

		Assert.True(result.ConsentRequired);
		Assert.False(result.Ok);
		Assert.Equal(2, result.Legal.Count);
		Assert.False(result.ConsentIsRenewal);
	}

	/// <summary>
	/// 「没同意过」与「同意的是旧版」要分得开。
	///
	/// 对一个早就注册、只因文档改版被拦下的人说「请先阅读并同意以下条款」，
	/// 他不知道自己为什么又被拦下来。
	/// </summary>
	[Fact]
	public async Task 版本落后标记为重新确认()
	{
		FakeHandler handler = new((HttpStatusCode.Forbidden, $$"""{"error":"consent_stale","renewal":true,"changed":["tos"],"legal":{{LegalJson}}}"""));
		CloudAuthResult result = await Client(handler).VerifyCodeAsync("a@b.com", "123456");

		Assert.True(result.ConsentRequired);
		Assert.True(result.ConsentIsRenewal);
	}

	[Fact]
	public async Task 同意会作为accept字段发出去()
	{
		FakeHandler handler = new((HttpStatusCode.OK, """{"ok":true,"session":{"token":"t"}}"""));
		Dictionary<string, string> accept = new(StringComparer.Ordinal) {["tos"] = "aaa"};
		await Client(handler).VerifyCodeAsync("a@b.com", "123456", accept);

		using JsonDocument sent = JsonDocument.Parse(handler.Bodies[0]);
		Assert.Equal("aaa", sent.RootElement.GetProperty("accept").GetProperty("tos").GetString());
		Assert.Equal("123456", sent.RootElement.GetProperty("code").GetString());
	}

	/// <summary>不带同意时不该凭空多出一个空的 accept —— 服务端对它的判定与「没传」不同。</summary>
	[Fact]
	public async Task 没有同意时不发accept字段()
	{
		FakeHandler handler = new((HttpStatusCode.OK, """{"ok":true,"session":{"token":"t"}}"""));
		await Client(handler).VerifyCodeAsync("a@b.com", "123456");

		using JsonDocument sent = JsonDocument.Parse(handler.Bodies[0]);
		Assert.False(sent.RootElement.TryGetProperty("accept", out _));
	}

	// ── 停用 ───────────────────────────────────────────────────────────────

	[Fact]
	public async Task 账户停用时带回理由与申诉链接()
	{
		FakeHandler handler = new((HttpStatusCode.Forbidden, """{"error":"account_banned","reason":"批量注册","at":"2026-09-14T03:11:02.118Z","appeal":"https://example.test/appeal?t=xyz"}"""));
		CloudAuthResult result = await Client(handler).VerifyCodeAsync("a@b.com", "123456");

		Assert.True(result.Banned);
		Assert.False(result.Ok);
		Assert.Contains("批量注册", result.Error, StringComparison.Ordinal);
		Assert.Equal("https://example.test/appeal?t=xyz", result.AppealUrl);
	}

	[Fact]
	public async Task 停用但未记录理由()
	{
		FakeHandler handler = new((HttpStatusCode.Forbidden, """{"error":"account_banned","reason":"","appeal":"https://example.test/appeal?t=xyz"}"""));
		CloudAuthResult result = await Client(handler).VerifyCodeAsync("a@b.com", "123456");

		Assert.True(result.Banned);
		Assert.NotEmpty(result.Error);
	}

	// ── 密码 ───────────────────────────────────────────────────────────────

	/// <summary>
	/// 服务端把「没这个账户」「没设过密码」「密码不对」合成同一个回应。
	///
	/// 客户端的文案不能把它们再拆开 —— 那等于把服务端为防账号枚举付的代价退回去。
	/// </summary>
	[Fact]
	public async Task 密码错误的文案不区分账户是否存在()
	{
		FakeHandler handler = new((HttpStatusCode.BadRequest, """{"error":"bad_credentials"}"""));
		CloudAuthResult result = await Client(handler).SignInWithPasswordAsync("a@b.com", "xxxxxxxxxxxx");

		Assert.Equal("邮箱或密码不正确", result.Error);
	}

	// ── 异常路径 ───────────────────────────────────────────────────────────

	/// <summary>服务端出错时 nginx 会回一张 HTML 错误页。解析不了不能变成崩溃。</summary>
	[Fact]
	public async Task 非JSON响应不会抛异常()
	{
		FakeHandler handler = new((HttpStatusCode.BadGateway, "<html><body>502 Bad Gateway</body></html>"));
		CloudAuthResult result = await Client(handler).VerifyCodeAsync("a@b.com", "123456");

		Assert.False(result.Ok);
		Assert.Contains("502", result.Error, StringComparison.Ordinal);
	}

	/// <summary>服务端新增了这里还没跟上的错误码。带上原始码 —— 「未知错误」没法报障。</summary>
	[Fact]
	public async Task 未知错误码保留原文()
	{
		FakeHandler handler = new((HttpStatusCode.BadRequest, """{"error":"some_new_code"}"""));
		CloudAuthResult result = await Client(handler).VerifyCodeAsync("a@b.com", "123456");

		Assert.Contains("some_new_code", result.Error, StringComparison.Ordinal);
	}

	// ── 退出 ───────────────────────────────────────────────────────────────

	[Fact]
	public async Task 退出登录带上Bearer头()
	{
		FakeHandler handler = new((HttpStatusCode.OK, "{}"));
		await Client(handler).SignOutAsync("tok-123");

		Assert.Equal("Bearer tok-123", handler.Requests[0].Headers.Authorization?.ToString());
	}

	/// <summary>网络不通也不能抛：本机那份记录无论如何都要清掉。</summary>
	[Fact]
	public async Task 退出登录在网络失败时不抛()
	{
		await Client(new ThrowingHandler()).SignOutAsync("tok-123");
	}

	// ── 地址 ───────────────────────────────────────────────────────────────

	[Fact]
	public void 法务文档地址按基址拼()
	{
		Assert.Equal("https://example.test/legal/cloud-tos",
			Client(new FakeHandler((HttpStatusCode.OK, "{}"))).LegalUrl("cloud-tos"));
	}

	/// <summary>基址带尾斜杠时不该拼出双斜杠 —— 服务端的路由是精确匹配的。</summary>
	[Fact]
	public void 基址尾斜杠被裁掉()
	{
		NoriCloudClient client = new(new HttpClient(new FakeHandler((HttpStatusCode.OK, "{}"))), "https://example.test/");
		Assert.Equal("https://example.test/legal/tos", client.LegalUrl("tos"));
	}
}
