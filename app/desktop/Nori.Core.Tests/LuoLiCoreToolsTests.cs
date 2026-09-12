using System.Net;
using System.Text;
using Nori.Core.Agent;
using Nori.Core.Chat.LuoLiCore;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Security;

namespace Nori.Core.Tests;

/// <summary>
/// 把对端的工具能力接进来的两件事：问「我能用什么」，以及跑起来时知道「她正在跑什么」。
/// </summary>
public sealed class LuoLiCoreToolsTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-luoli-tools-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly LuoLiCoreSettingsStore _settings;

	private sealed class FixedKeyStore : ISecretKeyStore
	{
		private readonly byte[] _key = Enumerable.Range(0, SecretKeyStore.KeySize).Select(index => (byte)index).ToArray();

		public byte[] LoadOrCreate() => _key;

		public bool IsFileFallback => true;
	}

	public LuoLiCoreToolsTests()
	{
		_database = NoriDatabase.Open(_path);
		_config = new ConfigStore(_database, new FixedKeyStore());
		_config.InitDefaults("test");
		_settings = new LuoLiCoreSettingsStore(_config);
	}

	public void Dispose()
	{
		_database.Dispose();
		try { File.Delete(_path); } catch (IOException) { /* 临时库删不掉不影响断言 */ }
	}

	private void Configure(bool enabled = true, string sessionId = "s")
	{
		_config.Set(LuoLiCoreSettingsStore.KeyEnabled, new ConfigValue.Boolean(enabled));
		_config.Set(LuoLiCoreSettingsStore.KeyBaseUrl, new ConfigValue.Text("http://127.0.0.1:3000"));
		_config.Set(LuoLiCoreSettingsStore.KeyApiKey, new ConfigValue.Text("sk-test"));
		if (sessionId.Length > 0) _config.Set(LuoLiCoreSettingsStore.KeySessionId, new ConfigValue.Text(sessionId));
	}

	private LuoLiCoreConversation Build(Func<HttpRequestMessage, HttpResponseMessage> responder)
	{
		HttpClient http = new(new StubHandler(responder));
		return new LuoLiCoreConversation(_settings, options => new LuoLiCoreSdkClient(http, options));
	}

	private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
	};

	private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code)
	{
		Content = new StringContent(body, Encoding.UTF8, "application/json"),
	};

	// ---- GET /sdk/v1/tools ----

	[Fact]
	public async Task 列出对端可用的工具()
	{
		Configure();
		List<string> paths = [];
		LuoLiCoreConversation conversation = Build(request =>
		{
			paths.Add(request.RequestUri!.AbsolutePath);
			return Json(
				HttpStatusCode.OK,
				"{\"ok\":true,\"tools\":["
					+ "{\"name\":\"grep\",\"description\":\"按内容搜文件\",\"approval\":\"never\"},"
					+ "{\"name\":\"delete_file\",\"description\":\"删文件\",\"approval\":\"always\"}"
					+ "]}");
		});

		IReadOnlyList<LuoLiCoreTool> tools = await conversation.ListToolsAsync(CancellationToken.None);

		Assert.Equal(["/sdk/v1/tools"], paths);
		Assert.Equal(["grep", "delete_file"], tools.Select(tool => tool.Name));
		Assert.False(tools[0].NeedsApproval);
		// SDK 会话没有人工审批通道，需要审批的那件调了就是被拒 —— 界面得能提前标出来。
		Assert.True(tools[1].NeedsApproval);
	}

	/// <summary>对端版本旧、还没有这个端点时，不该让设置页或启动流程失败。</summary>
	[Fact]
	public async Task 对端没有这个端点时返回空列表而不是抛()
	{
		Configure();
		LuoLiCoreConversation conversation = Build(_ => Json(HttpStatusCode.NotFound, "{}"));

		Assert.Empty(await conversation.ListToolsAsync(CancellationToken.None));
	}

	[Fact]
	public async Task 未启用时不去碰远端()
	{
		Configure(enabled: false);
		List<string> paths = [];
		LuoLiCoreConversation conversation = Build(request =>
		{
			paths.Add(request.RequestUri!.AbsolutePath);
			return Json(HttpStatusCode.OK, "{\"ok\":true,\"tools\":[]}");
		});

		Assert.Empty(await conversation.ListToolsAsync(CancellationToken.None));
		Assert.Empty(paths);
	}

	// ---- SSE tool 事件 ----

	[Fact]
	public async Task 请求里显式要了工具事件()
	{
		Configure();
		string body = "";
		LuoLiCoreConversation conversation = Build(request =>
		{
			body = request.Content!.ReadAsStringAsync(CancellationToken.None).Result;
			return Sse("event: done\ndata: {\"text\":\"好\"}\n\n");
		});

		await conversation.RunAsync("在吗", null, CancellationToken.None);

		// 对端缺省不发工具事件，所以这一项必须真的发出去，否则整条特性静默失效。
		Assert.Contains("\"events\":[\"tool\"]", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 工具事件逐条回调()
	{
		Configure();
		List<string> tools = [];
		List<string> chunks = [];
		LuoLiCoreConversation conversation = Build(_ => Sse(
			"event: tool\ndata: {\"name\":\"grep\"}\n\n"
			+ "event: delta\ndata: {\"text\":\"找到了\"}\n\n"
			+ "event: tool\ndata: {\"name\":\"read_file\"}\n\n"
			+ "event: done\ndata: {\"text\":\"找到了\"}\n\n"));

		await conversation.RunAsync("搜一下", chunks.Add, null, tools.Add, CancellationToken.None);

		Assert.Equal(["grep", "read_file"], tools);
		Assert.Equal(["找到了"], chunks);
	}

	/// <summary>它不参与顺序不变量：夹在 delta 之间、或出现在没有 delta 的轮次里都合法。</summary>
	[Fact]
	public async Task 没有任何文本增量的一轮也能有工具事件()
	{
		Configure();
		List<string> tools = [];
		LuoLiCoreConversation conversation = Build(_ => Sse(
			"event: tool\ndata: {\"name\":\"bash\"}\n\n"
			+ "event: done\ndata: {\"text\":\"跑完了\"}\n\n"));

		ProtocolMessage message = await conversation.RunAsync("跑一下", null, null, tools.Add, CancellationToken.None);

		Assert.Equal(["bash"], tools);
		Assert.Equal("跑完了", message.Text);
	}

	[Fact]
	public async Task 名字为空的工具事件被丢掉()
	{
		Configure();
		List<string> tools = [];
		LuoLiCoreConversation conversation = Build(_ => Sse(
			"event: tool\ndata: {\"name\":\"\"}\n\n"
			+ "event: done\ndata: {\"text\":\"好\"}\n\n"));

		await conversation.RunAsync("在吗", null, null, tools.Add, CancellationToken.None);

		Assert.Empty(tools);
	}

	// ---- 建议清单 ----

	[Fact]
	public void 建议清单覆盖选定的六组()
	{
		Assert.Contains("read_file", LuoLiCoreToolPreset.Recommended);
		Assert.Contains("bash", LuoLiCoreToolPreset.Recommended);
		Assert.Contains("web_fetch", LuoLiCoreToolPreset.Recommended);
		Assert.Contains("search_messages", LuoLiCoreToolPreset.Recommended);
		Assert.Contains("memory_write", LuoLiCoreToolPreset.Recommended);
		Assert.Contains("install_mcp", LuoLiCoreToolPreset.Recommended);
	}

	/// <summary>
	/// 这两件的 minRole 是 owner，而 SDK 会话恒 trusted —— 配了也调不到。
	/// 列进建议清单只会让人以为自己开了。
	/// </summary>
	[Fact]
	public void 建议清单不含角色轴挡住的两件()
	{
		foreach (string name in LuoLiCoreToolPreset.BlockedByRole)
			Assert.DoesNotContain(name, LuoLiCoreToolPreset.Recommended);
	}

	[Fact]
	public void 建议清单不含结构性禁止的那两族()
	{
		// 派发与 Telegram 输出在对端是配了也不生效的，出现在这里等于在教人配一份无效清单。
		foreach (string name in new[] { "spawn_worker", "read_worker_result", "send_message", "stay_silent" })
			Assert.DoesNotContain(name, LuoLiCoreToolPreset.Recommended);
	}

	[Fact]
	public void 建议清单去重且有序()
	{
		Assert.Equal(LuoLiCoreToolPreset.Recommended.Distinct(StringComparer.Ordinal), LuoLiCoreToolPreset.Recommended);
		Assert.Equal(
			LuoLiCoreToolPreset.Recommended.OrderBy(name => name, StringComparer.Ordinal),
			LuoLiCoreToolPreset.Recommended);
	}

	/// <summary>「你以为开了、其实没开」是这个集成里最难查的一类不一致。</summary>
	[Fact]
	public void 缺口算得出来()
	{
		LuoLiCoreTool[] available =
		[
			new("read_file", "读文件", false),
			new("grep", "搜文件", false),
		];

		IReadOnlyList<string> missing = LuoLiCoreToolPreset.Missing(available);

		Assert.DoesNotContain("read_file", missing);
		Assert.Contains("bash", missing);
		Assert.Equal(LuoLiCoreToolPreset.Recommended.Count - 2, missing.Count);
	}

	[Fact]
	public void 全都给齐时缺口为空()
	{
		LuoLiCoreTool[] available =
			[.. LuoLiCoreToolPreset.Recommended.Select(name => new LuoLiCoreTool(name, "", false))];

		Assert.Empty(LuoLiCoreToolPreset.Missing(available));
	}

	private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(responder(request));
	}
}
