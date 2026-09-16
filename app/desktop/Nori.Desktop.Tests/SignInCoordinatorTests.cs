using System.Net;
using System.Text;
using System.Text.Json;
using Nori.Core.Cloud;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Security;
using Nori.Desktop.Account;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>
/// 登录协调器。
///
/// 这一族的重点是**同意那个回合**：服务端对缺同意回 403 并且**不消耗验证码**，就是为了让
/// 客户端取得同意后拿同一个码重提。这个来回一旦写错，症状不是报错 —— 是用户输对了验证码
/// 却怎么也登不进去，而且第二次提交时码已经被烧掉了。
/// </summary>
public sealed class SignInCoordinatorTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-test-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	private sealed class FixedKeyStore : ISecretKeyStore
	{
		private readonly byte[] _key = [.. Enumerable.Range(0, SecretKeyStore.KeySize).Select(index => (byte)index)];
		public byte[] LoadOrCreate() => _key;
		public bool IsFileFallback => true;
	}

	private sealed class FakeHandler(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
	{
		private int _index;

		public List<string> Bodies { get; } = [];
		public List<string> Paths { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Paths.Add(request.RequestUri?.AbsolutePath ?? "");
			Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
			(HttpStatusCode status, string body) = replies[Math.Min(_index++, replies.Length - 1)];
			return new HttpResponseMessage(status)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			};
		}
	}

	public SignInCoordinatorTests()
	{
		_database = NoriDatabase.Open(_path);
		_config = new ConfigStore(_database, new FixedKeyStore());
		_config.InitDefaults("0.1.0");
		_config.EnsureSchemaVersion();
	}

	public void Dispose()
	{
		_database.Dispose();
		try { File.Delete(_path); } catch (IOException) { }
		GC.SuppressFinalize(this);
	}

	private const string LegalJson =
		"""[{"key":"tos","title":"服务条款","version":"1.1","sha256":"aaa"},{"key":"cloud-privacy","title":"隐私政策","version":"1.1","sha256":"bbb"}]""";

	private const string SuccessJson =
		"""{"ok":true,"user":{"email":"a@b.com","name":"甲"},"session":{"token":"tok-123","expiresAt":"2027-01-01T00:00:00.000Z"}}""";

	private (SignInCoordinator Coordinator, AccountSession Session) Build(
		FakeHandler handler, Func<ConsentRequest, Task<bool>> consent)
	{
		AccountSession session = new(_config);
		NoriCloudClient client = new(new HttpClient(handler), "https://example.test");
		return (new SignInCoordinator(client, session, consent), session);
	}

	private static SignInState State(SignInMethod method, bool codeSent, string code = "123456") => new()
	{
		Method = method,
		Phase = SignInPhase.Idle,
		Email = "a@b.com",
		Password = "password1234",
		Code = code,
		CodeSent = codeSent,
		ResendSeconds = 0,
		CanSubmit = true,
		SubmitLabel = "登录",
		Error = "",
		Status = "",
	};

	// ── 发码 ───────────────────────────────────────────────────────────────

	/// <summary>验证码方式的第一步是发码，不是登录 —— 判据是表单的 CodeSent。</summary>
	[Fact]
	public async Task 尚未发码时只发码()
	{
		FakeHandler handler = new((HttpStatusCode.OK, """{"ok":true}"""));
		(SignInCoordinator coordinator, _) = Build(handler, _ => Task.FromResult(true));

		SignInOutcome outcome = await coordinator.SubmitAsync(State(SignInMethod.Code, codeSent: false));

		Assert.True(outcome.Ok);
		Assert.True(outcome.CodeSent);
		Assert.Single(handler.Paths);
		Assert.Equal("/api/nori-auth/code", handler.Paths[0]);
	}

	// ── 同意 ───────────────────────────────────────────────────────────────

	/// <summary>
	/// 缺同意 → 弹确认 → 用**同一个码**带 accept 重提。
	///
	/// 重提的码必须还是那一个：服务端在缺同意那一次刻意没有消耗它，换一个码等于让
	/// 用户白输一遍。
	/// </summary>
	[Fact]
	public async Task 缺同意时取得同意后用同一个码重提()
	{
		FakeHandler handler = new(
			(HttpStatusCode.Forbidden, $$"""{"error":"consent_required","legal":{{LegalJson}}}"""),
			(HttpStatusCode.OK, SuccessJson));

		int asked = 0;
		(SignInCoordinator coordinator, AccountSession session) = Build(handler, _ =>
		{
			asked++;
			return Task.FromResult(true);
		});

		SignInOutcome outcome = await coordinator.SubmitAsync(State(SignInMethod.Code, codeSent: true));

		Assert.True(outcome.Ok);
		Assert.Equal(1, asked);
		Assert.Equal(2, handler.Bodies.Count);

		using JsonDocument first = JsonDocument.Parse(handler.Bodies[0]);
		using JsonDocument second = JsonDocument.Parse(handler.Bodies[1]);
		Assert.False(first.RootElement.TryGetProperty("accept", out _));
		Assert.Equal("123456", first.RootElement.GetProperty("code").GetString());
		Assert.Equal("123456", second.RootElement.GetProperty("code").GetString());

		JsonElement accept = second.RootElement.GetProperty("accept");
		Assert.Equal("aaa", accept.GetProperty("tos").GetString());
		Assert.Equal("bbb", accept.GetProperty("cloud-privacy").GetString());

		// 登录成功要落盘，否则重启之后又得登一次。
		Assert.Equal("tok-123", session.Current?.Token);
	}

	/// <summary>不同意就是一次普通失败：登录本来就是可选项，不该变成关不掉的窗口。</summary>
	[Fact]
	public async Task 不同意则失败且不再请求()
	{
		FakeHandler handler = new((HttpStatusCode.Forbidden, $$"""{"error":"consent_required","legal":{{LegalJson}}}"""));
		(SignInCoordinator coordinator, AccountSession session) = Build(handler, _ => Task.FromResult(false));

		SignInOutcome outcome = await coordinator.SubmitAsync(State(SignInMethod.Code, codeSent: true));

		Assert.False(outcome.Ok);
		Assert.NotEmpty(outcome.Error);
		Assert.Single(handler.Bodies);
		Assert.Null(session.Current);
	}

	/// <summary>文档改版与首次注册要能分开 —— 传给对话框的标志决定它说哪一句话。</summary>
	[Fact]
	public async Task 版本落后时标记为重新确认()
	{
		FakeHandler handler = new(
			(HttpStatusCode.Forbidden, $$"""{"error":"consent_stale","renewal":true,"legal":{{LegalJson}}}"""),
			(HttpStatusCode.OK, SuccessJson));

		bool? renewal = null;
		(SignInCoordinator coordinator, _) = Build(handler, request =>
		{
			renewal = request.IsRenewal;
			return Task.FromResult(true);
		});

		await coordinator.SubmitAsync(State(SignInMethod.Code, codeSent: true));

		Assert.True(renewal);
	}

	/// <summary>
	/// 服务端说缺同意却没给清单时，不能带着空的 accept 去重试。
	///
	/// 那样只会再被拒一次，用户看到的是一个点了没反应的按钮。
	/// </summary>
	[Fact]
	public async Task 没有文档清单时不重试()
	{
		FakeHandler handler = new((HttpStatusCode.Forbidden, """{"error":"consent_required"}"""));
		(SignInCoordinator coordinator, _) = Build(handler, _ => Task.FromResult(true));

		SignInOutcome outcome = await coordinator.SubmitAsync(State(SignInMethod.Code, codeSent: true));

		Assert.False(outcome.Ok);
		Assert.NotEmpty(outcome.Error);
		Assert.Single(handler.Bodies);
	}

	// ── 停用 ───────────────────────────────────────────────────────────────

	/// <summary>停用的说明里要能看到申诉链接，否则那个 URL 无处可去。</summary>
	[Fact]
	public async Task 停用时把申诉链接带进说明()
	{
		FakeHandler handler = new((HttpStatusCode.Forbidden, """{"error":"account_banned","reason":"批量注册","appeal":"https://example.test/appeal?t=xyz"}"""));
		(SignInCoordinator coordinator, AccountSession session) = Build(handler, _ => Task.FromResult(true));

		SignInOutcome outcome = await coordinator.SubmitAsync(State(SignInMethod.Code, codeSent: true));

		Assert.False(outcome.Ok);
		Assert.Contains("批量注册", outcome.Error, StringComparison.Ordinal);
		Assert.Contains("https://example.test/appeal?t=xyz", outcome.Error, StringComparison.Ordinal);
		Assert.Null(session.Current);
	}

	// ── 密码 ───────────────────────────────────────────────────────────────

	[Fact]
	public async Task 密码方式直接走密码接口()
	{
		FakeHandler handler = new((HttpStatusCode.OK, SuccessJson));
		(SignInCoordinator coordinator, AccountSession session) = Build(handler, _ => Task.FromResult(true));

		SignInOutcome outcome = await coordinator.SubmitAsync(State(SignInMethod.Password, codeSent: false));

		Assert.True(outcome.Ok);
		Assert.Equal("/api/nori-auth/password", handler.Paths[0]);
		// 下次打开窗口应当停在密码那一档。
		Assert.Equal(SignInMethod.Password, session.LastMethod);
	}

	// ── 退出 ───────────────────────────────────────────────────────────────

	[Fact]
	public async Task 退出登录清掉本机记录()
	{
		FakeHandler handler = new((HttpStatusCode.OK, SuccessJson));
		(SignInCoordinator coordinator, AccountSession session) = Build(handler, _ => Task.FromResult(true));
		await coordinator.SubmitAsync(State(SignInMethod.Code, codeSent: true));
		Assert.NotNull(session.Current);

		await coordinator.SignOutAsync();

		Assert.Null(session.Current);
		Assert.Equal("/api/nori-auth/sign-out", handler.Paths[^1]);
	}

	/// <summary>没登录时点退出不该发请求，也不该抛。</summary>
	[Fact]
	public async Task 未登录时退出是空操作()
	{
		FakeHandler handler = new((HttpStatusCode.OK, "{}"));
		(SignInCoordinator coordinator, AccountSession session) = Build(handler, _ => Task.FromResult(true));

		await coordinator.SignOutAsync();

		Assert.Null(session.Current);
		Assert.Empty(handler.Paths);
	}
}
