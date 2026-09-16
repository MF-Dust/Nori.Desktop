using System.Net;
using System.Text;
using System.Text.Json;
using Nori.Core.Cloud;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Memory;
using Nori.Core.Proactive;

namespace Nori.Core.Tests;

/// <summary>
/// 云存档的同步。
///
/// 这一族守的是**版本号那道闸**。它存在的理由是：一个账户可以在多台机器上登录，不带
/// 版本号上传就是无条件覆盖，另一台刚存的东西会没掉且两边都没有迹象。
///
/// 闸失效有两种方式，都不会报错：
///   · 版本号读不回来 → 每次都拿 0 去撞 → 用户看到「云端有更新的存档」，而那份正是他
///     自己刚存的；
///   · 冲突被当成普通失败 → 客户端重试或放弃，用户失去在两份之间选择的机会。
/// </summary>
public sealed class CloudSyncServiceTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-sync-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly MemoryStore _memoryStore;
	private readonly MemoryTransferService _transfer;
	private readonly ReminderStore _reminders;
	private readonly CloudSaveService _saves;
	private readonly AccountSession _session;

	public CloudSyncServiceTests()
	{
		_database = NoriDatabase.Open(_path);
		_config = new ConfigStore(_database);
		_memoryStore = new MemoryStore(_database);
		_transfer = new MemoryTransferService(_memoryStore);
		_reminders = new ReminderStore(_database);
		_saves = new CloudSaveService(_config, _transfer, _reminders);
		_session = new AccountSession(_config);
	}

	public void Dispose()
	{
		_transfer.Dispose();
		_database.Dispose();
		try { File.Delete(_path); } catch (IOException) { /* 临时库删不掉不影响断言 */ }
	}

	private sealed class FakeHandler(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
	{
		private int _index;

		public List<string> Bodies { get; } = [];
		public List<string> Targets { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Targets.Add($"{request.Method} {request.RequestUri?.PathAndQuery}");
			Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
			(HttpStatusCode status, string body) = replies[Math.Min(_index++, replies.Length - 1)];
			return new HttpResponseMessage(status)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			};
		}
	}

	private CloudSyncService Build(FakeHandler handler)
	{
		NoriCloudClient cloud = new(new HttpClient(handler), "https://example.test");
		return new CloudSyncService(cloud, _session, _saves, _config);
	}

	private void SignIn() => _session.Save(new CloudAccount
	{
		Email = "a@b.com",
		Token = "tok-123",
		ExpiresAt = "2027-01-01T00:00:00.000Z",
	}, SignInMethod.Code);

	// ── 未登录 ──────────────────────────────────────────────────────────────

	/// <summary>未登录时三个动作都不该发出请求 —— 发了也只会拿到 401。</summary>
	[Fact]
	public async Task 未登录时不发请求()
	{
		FakeHandler handler = new((HttpStatusCode.OK, "{}"));
		CloudSyncService sync = Build(handler);

		Assert.False((await sync.BackupAsync()).Ok);
		Assert.False((await sync.RestoreAsync()).Ok);
		Assert.False((await sync.ForgetAsync()).Ok);
		Assert.False((await sync.StatusAsync()).SignedIn);
		Assert.Empty(handler.Targets);
	}

	// ── 上传 ────────────────────────────────────────────────────────────────

	[Fact]
	public async Task 首次上传带0号版本并记住新版本号()
	{
		SignIn();
		FakeHandler handler = new((HttpStatusCode.OK, """{"ok":true,"revision":1,"savedAt":"2026-09-15T07:00:00.000Z"}"""));
		CloudSyncService sync = Build(handler);

		CloudSyncResult result = await sync.BackupAsync();

		Assert.True(result.Ok);
		using JsonDocument sent = JsonDocument.Parse(handler.Bodies[0]);
		Assert.Equal(0, sent.RootElement.GetProperty("ifRevision").GetInt32());
		// 文档要原样嵌在信封里，而且带着格式标识 —— 服务端按它判收不收。
		Assert.Equal(CloudSaveDocument.FormatName,
			sent.RootElement.GetProperty("document").GetProperty("format").GetString());
	}

	/// <summary>
	/// 第一次上传之后版本号是 1，而 1 正是配置类型推断的雷区。
	///
	/// 读不回来的话，第二次上传会带着 0 去撞，被判成冲突 —— 而那份「更新的存档」
	/// 正是他自己刚存的。
	/// </summary>
	[Fact]
	public async Task 第二次上传带上真实的版本号()
	{
		SignIn();
		FakeHandler handler = new(
			(HttpStatusCode.OK, """{"ok":true,"revision":1,"savedAt":"2026-09-15T07:00:00.000Z"}"""),
			(HttpStatusCode.OK, """{"ok":true,"revision":2,"savedAt":"2026-09-15T08:00:00.000Z"}"""));
		CloudSyncService sync = Build(handler);

		await sync.BackupAsync();
		await sync.BackupAsync();

		using JsonDocument second = JsonDocument.Parse(handler.Bodies[1]);
		Assert.Equal(1, second.RootElement.GetProperty("ifRevision").GetInt32());
	}

	[Fact]
	public async Task 冲突单独报告并带上云端时刻()
	{
		SignIn();
		FakeHandler handler = new((HttpStatusCode.Conflict,
			"""{"error":"revision_conflict","revision":7,"savedAt":"2026-09-15T06:00:00.000Z"}"""));
		CloudSyncService sync = Build(handler);

		CloudSyncResult result = await sync.BackupAsync();

		Assert.False(result.Ok);
		Assert.True(result.Conflict, "冲突要能被调用方分辨出来，否则它只能当成普通失败去重试");
		Assert.Equal("2026-09-15T06:00:00.000Z", result.RemoteSavedAt);
	}

	/// <summary>冲突之后本机记的版本号不能被改掉 —— 改了下次就会变成无条件覆盖。</summary>
	[Fact]
	public async Task 冲突不会改动本机记的版本号()
	{
		SignIn();
		FakeHandler handler = new(
			(HttpStatusCode.OK, """{"ok":true,"revision":1}"""),
			(HttpStatusCode.Conflict, """{"error":"revision_conflict","revision":9}"""),
			(HttpStatusCode.OK, """{"ok":true,"revision":2}"""));
		CloudSyncService sync = Build(handler);

		await sync.BackupAsync();
		await sync.BackupAsync();
		await sync.BackupAsync();

		using JsonDocument third = JsonDocument.Parse(handler.Bodies[2]);
		Assert.Equal(1, third.RootElement.GetProperty("ifRevision").GetInt32());
	}

	/// <summary>用户明确选了「以本机为准」时才不带版本号。这条路必须存在，也必须是显式的。</summary>
	[Fact]
	public async Task 覆盖模式不带版本号()
	{
		SignIn();
		FakeHandler handler = new((HttpStatusCode.OK, """{"ok":true,"revision":9}"""));
		CloudSyncService sync = Build(handler);

		await sync.BackupAsync(overwrite: true);

		using JsonDocument sent = JsonDocument.Parse(handler.Bodies[0]);
		Assert.False(sent.RootElement.TryGetProperty("ifRevision", out _));
	}

	// ── 恢复 ────────────────────────────────────────────────────────────────

	[Fact]
	public async Task 云端没有存档时明说()
	{
		SignIn();
		FakeHandler handler = new((HttpStatusCode.OK, """{"ok":true,"present":false}"""));
		CloudSyncService sync = Build(handler);

		CloudSyncResult result = await sync.RestoreAsync();

		Assert.False(result.Ok);
		Assert.Contains("还没有", result.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 恢复把范围内的配置写回本机()
	{
		SignIn();
		string document = """{"format":"nori-cloud-v1","saved_at":"2026-09-15T07:00:00.000Z","config":{"language":"en-US"},"reminders":[]}""";
		FakeHandler handler = new((HttpStatusCode.OK,
			$$"""{"ok":true,"present":true,"revision":4,"savedAt":"2026-09-15T07:00:00.000Z","document":{{document}}}"""));
		CloudSyncService sync = Build(handler);

		CloudSyncResult result = await sync.RestoreAsync();

		Assert.True(result.Ok);
		Assert.Equal("en-US", _config.GetStringOr("language", ""));
	}

	/// <summary>
	/// 恢复之后本机这份就等同于云端那一版，版本号要跟上。
	///
	/// 不跟的话，恢复完紧接着上传会拿旧号去撞，得到一个假的冲突。
	/// </summary>
	[Fact]
	public async Task 恢复之后版本号跟上云端()
	{
		SignIn();
		string document = """{"format":"nori-cloud-v1","config":{},"reminders":[]}""";
		FakeHandler handler = new(
			(HttpStatusCode.OK, $$"""{"ok":true,"present":true,"revision":4,"document":{{document}}}"""),
			(HttpStatusCode.OK, """{"ok":true,"revision":5}"""));
		CloudSyncService sync = Build(handler);

		await sync.RestoreAsync();
		await sync.BackupAsync();

		using JsonDocument sent = JsonDocument.Parse(handler.Bodies[1]);
		Assert.Equal(4, sent.RootElement.GetProperty("ifRevision").GetInt32());
	}

	/// <summary>服务端说有、拿回来却解析不了时，要说清楚唯一的出路是覆盖。</summary>
	[Fact]
	public async Task 云端存档解析不了时给出出路()
	{
		SignIn();
		FakeHandler handler = new((HttpStatusCode.OK,
			"""{"ok":true,"present":true,"revision":4,"document":"这不是一个对象"}"""));
		CloudSyncService sync = Build(handler);

		CloudSyncResult result = await sync.RestoreAsync();

		Assert.False(result.Ok);
		Assert.Contains("覆盖", result.Message, StringComparison.Ordinal);
	}

	// ── 状态 ────────────────────────────────────────────────────────────────

	[Fact]
	public async Task 查状态只要元信息()
	{
		SignIn();
		FakeHandler handler = new((HttpStatusCode.OK,
			"""{"ok":true,"present":true,"revision":3,"savedAt":"2026-09-15T07:00:00.000Z","appVersion":"1.2.3","bytes":2048}"""));
		CloudSyncService sync = Build(handler);

		CloudSyncStatus status = await sync.StatusAsync();

		Assert.True(status.Ok);
		Assert.True(status.Present);
		Assert.Equal(3, status.Revision);
		Assert.Equal("1.2.3", status.AppVersion);
		Assert.Equal(2048, status.Bytes);
		// 为一行界面文字拉一份 4MB 的文档在移动网络下会很慢。
		Assert.Contains("meta=1", handler.Targets[0], StringComparison.Ordinal);
	}

	// ── 删除 ────────────────────────────────────────────────────────────────

	[Fact]
	public async Task 删除云端后版本号清零()
	{
		SignIn();
		FakeHandler handler = new(
			(HttpStatusCode.OK, """{"ok":true,"revision":1}"""),
			(HttpStatusCode.OK, """{"ok":true,"removed":true}"""),
			(HttpStatusCode.OK, """{"ok":true,"revision":1}"""));
		CloudSyncService sync = Build(handler);

		await sync.BackupAsync();
		CloudSyncResult gone = await sync.ForgetAsync();
		await sync.BackupAsync();

		Assert.True(gone.Ok);
		// 不清零的话，下一次上传会带着一个云端已经不存在的版本号，被判成冲突。
		using JsonDocument again = JsonDocument.Parse(handler.Bodies[2]);
		Assert.Equal(0, again.RootElement.GetProperty("ifRevision").GetInt32());
	}

	[Fact]
	public async Task 删除要带明确的确认字段()
	{
		SignIn();
		FakeHandler handler = new((HttpStatusCode.OK, """{"ok":true}"""));
		CloudSyncService sync = Build(handler);

		await sync.ForgetAsync();

		using JsonDocument sent = JsonDocument.Parse(handler.Bodies[0]);
		Assert.Equal("delete", sent.RootElement.GetProperty("action").GetString());
		Assert.Equal("DELETE", sent.RootElement.GetProperty("confirm").GetString());
	}

	// ── 服务端错误 ──────────────────────────────────────────────────────────

	[Fact]
	public async Task 会话过期时给出可操作的说明()
	{
		SignIn();
		FakeHandler handler = new((HttpStatusCode.Unauthorized, """{"error":"not_signed_in"}"""));
		CloudSyncService sync = Build(handler);

		CloudSyncResult result = await sync.BackupAsync();

		Assert.False(result.Ok);
		Assert.Contains("重新登录", result.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 服务端未启用同步时明说()
	{
		SignIn();
		FakeHandler handler = new((HttpStatusCode.ServiceUnavailable, """{"error":"not_configured"}"""));
		CloudSyncService sync = Build(handler);

		Assert.Contains("未启用", (await sync.StatusAsync()).Error, StringComparison.Ordinal);
	}

	/// <summary>超限要说清楚超了多少，否则用户不知道该删掉多少东西。</summary>
	[Fact]
	public async Task 服务端超限提示带上数值()
	{
		SignIn();
		FakeHandler handler = new((HttpStatusCode.RequestEntityTooLarge,
			"""{"error":"too_large","bytes":5242880,"limit":4194304}"""));
		CloudSyncService sync = Build(handler);

		CloudSyncResult result = await sync.BackupAsync();

		Assert.Contains("5120 KB", result.Message, StringComparison.Ordinal);
		Assert.Contains("4096 KB", result.Message, StringComparison.Ordinal);
	}

	// ── 会话过期 ────────────────────────────────────────────────────────────

	/// <summary>
	/// 服务端说令牌不作数了，本机那份登录记录要跟着清掉。
	///
	/// 守的是一处实际存在过的缺陷：错误码在 <c>Describe()</c> 那一层就被翻成人话，调用方
	/// 只拿到一句「登录已过期，请重新登录」，本机会话原样留着。症状是托盘与设置页一直
	/// 显示已登录、同步按钮一直可点，而每一次都失败在同一句话上。
	/// </summary>
	[Theory]
	[InlineData("status")]
	[InlineData("backup")]
	[InlineData("restore")]
	[InlineData("forget")]
	public async Task 令牌失效时清掉本机登录态(string action)
	{
		SignIn();
		CloudSyncService sync = Build(new FakeHandler((HttpStatusCode.Unauthorized, """{"error":"not_signed_in"}""")));

		string message = action switch
		{
			"status" => (await sync.StatusAsync()).Error,
			"backup" => (await sync.BackupAsync()).Message,
			"restore" => (await sync.RestoreAsync()).Message,
			_ => (await sync.ForgetAsync()).Message,
		};

		Assert.Contains("登录已过期", message, StringComparison.Ordinal);
		Assert.False(_session.IsSignedIn);
	}

	/// <summary>
	/// 别的失败不能把人登出。
	///
	/// 维护中、写入失败都是暂时的。把它们也当成过期的话，服务端抖一下用户就得重新登录，
	/// 而他的令牌其实一直有效。
	/// </summary>
	[Theory]
	[InlineData(HttpStatusCode.ServiceUnavailable, """{"error":"maintenance"}""")]
	[InlineData(HttpStatusCode.InternalServerError, """{"error":"write_failed"}""")]
	public async Task 其它失败不清登录态(HttpStatusCode status, string body)
	{
		SignIn();
		CloudSyncService sync = Build(new FakeHandler((status, body)));

		await sync.BackupAsync();

		Assert.True(_session.IsSignedIn);
	}

	/// <summary>过期清掉会话之后，这一轮返回的状态里也要说未登录 —— 否则界面刷完还是已登录。</summary>
	[Fact]
	public async Task 过期时返回的状态也是未登录()
	{
		SignIn();
		CloudSyncService sync = Build(new FakeHandler((HttpStatusCode.Unauthorized, """{"error":"not_signed_in"}""")));

		CloudSyncStatus status = await sync.StatusAsync();

		Assert.False(status.SignedIn);
		Assert.False(_session.IsSignedIn);
	}

	// ── 与服务端的约定 ──────────────────────────────────────────────────────

	/// <summary>
	/// 上限两边取同一个数。
	///
	/// 客户端更大的话，它会放过一份服务端注定拒收的存档；更小的话，用户被本机拦住却
	/// 找不到服务端的依据。cloud-saves.mjs 里有一条对称的断言。
	/// </summary>
	[Fact]
	public void 上限与服务端一致()
	{
		Assert.Equal(4 * 1024 * 1024, CloudSaveService.MaxBytes);
	}

	/// <summary>格式标识两边逐字一致。对不上时症状是每一次上传都被拒，而两边各自看都没问题。</summary>
	[Fact]
	public void 格式标识与服务端一致()
	{
		Assert.Equal("nori-cloud-v1", CloudSaveDocument.FormatName);
	}
}
