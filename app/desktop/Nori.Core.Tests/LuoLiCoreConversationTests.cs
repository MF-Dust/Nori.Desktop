using System.Net;
using System.Text;
using Nori.Core.Agent;
using Nori.Core.Chat;
using Nori.Core.Chat.LuoLiCore;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Security;

namespace Nori.Core.Tests;

/// <summary>把一轮对话交给 LuoLiCore 这条路。用假的 HTTP 处理器，不需要真实服务端。</summary>
public sealed class LuoLiCoreConversationTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-luoli-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly LuoLiCoreSettingsStore _settings;

	/// <summary>
	/// 测试用固定主密钥。
	///
	/// `luolicore_api_key` 以 `_api_key` 结尾，因此和 `llm_api_key` 一样按敏感字段加密存储；
	/// 不给固定密钥的话，测试环境里密钥库不可用，写进去的密钥读回来是空，表现成「配置不完整」。
	/// </summary>
	private sealed class FixedKeyStore : ISecretKeyStore
	{
		private readonly byte[] _key = Enumerable.Range(0, SecretKeyStore.KeySize).Select(index => (byte)index).ToArray();

		public byte[] LoadOrCreate() => _key;

		public bool IsFileFallback => true;
	}

	public LuoLiCoreConversationTests()
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

	private void Configure(bool enabled = true, string sessionId = "")
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

	[Fact]
	public void 未启用时不接管对话()
	{
		Configure(enabled: false);
		LuoLiCoreConversation conversation = Build(_ => Json(HttpStatusCode.OK, "{}"));

		Assert.False(conversation.IsActive);
	}

	[Fact]
	public void 配置不全时即便开了开关也不接管()
	{
		_config.Set(LuoLiCoreSettingsStore.KeyEnabled, new ConfigValue.Boolean(true));
		// 只填地址不填密钥
		_config.Set(LuoLiCoreSettingsStore.KeyBaseUrl, new ConfigValue.Text("http://127.0.0.1:3000"));

		Assert.False(_settings.Read().IsActive);
	}

	[Fact]
	public async Task 首轮建会话并把id写回配置()
	{
		Configure();
		List<string> paths = [];
		LuoLiCoreConversation conversation = Build(request =>
		{
			paths.Add(request.RequestUri!.AbsolutePath);
			return request.RequestUri!.AbsolutePath.EndsWith("/sessions", StringComparison.Ordinal)
				? Json(HttpStatusCode.OK, "{\"id\":\"sess_1\",\"label\":null,\"modelAlias\":null,\"outputFormat\":\"plain\",\"createdAt\":0,\"deletedAt\":null}")
				: Sse("event: done\ndata: {\"text\":\"在的\"}\n\n");
		});

		ProtocolMessage message = await conversation.RunAsync("你在吗", null, CancellationToken.None);

		Assert.Equal("在的", message.Text);
		Assert.Equal("sess_1", _settings.Read().SessionId);
		Assert.Equal(["/sdk/v1/sessions", "/sdk/v1/sessions/sess_1/messages/stream"], paths);
	}

	[Fact]
	public async Task 已有会话就复用_不再新建()
	{
		Configure(sessionId: "sess_old");
		List<string> paths = [];
		LuoLiCoreConversation conversation = Build(request =>
		{
			paths.Add(request.RequestUri!.AbsolutePath);
			return Sse("event: done\ndata: {\"text\":\"记得\"}\n\n");
		});

		await conversation.RunAsync("还记得吗", null, CancellationToken.None);

		// 每轮新建会话等价于每轮失忆，这一条防的就是那个。
		Assert.DoesNotContain("/sdk/v1/sessions", paths);
		Assert.Equal(["/sdk/v1/sessions/sess_old/messages/stream"], paths);
	}

	[Fact]
	public async Task 增量逐块回调_最终文本以done为准()
	{
		Configure(sessionId: "s");
		List<string> chunks = [];
		LuoLiCoreConversation conversation = Build(_ => Sse(
			"event: delta\ndata: {\"text\":\"你\"}\n\n"
			+ "event: delta\ndata: {\"text\":\"好\"}\n\n"
			+ "event: done\ndata: {\"text\":\"你好\"}\n\n"));

		ProtocolMessage message = await conversation.RunAsync("嗨", chunks.Add, CancellationToken.None);

		Assert.Equal(["你", "好"], chunks);
		Assert.Equal("你好", message.Text);
	}

	/// <summary>动作与表情不从这条流来 —— 合法名随当前模型变化，只有宿主知道。</summary>
	[Fact]
	public async Task 协议消息只带文本_表情动作留给宿主决定()
	{
		Configure(sessionId: "s");
		LuoLiCoreConversation conversation = Build(_ => Sse("event: done\ndata: {\"text\":\"嗯\"}\n\n"));

		ProtocolMessage message = await conversation.RunAsync("在吗", null, CancellationToken.None);

		Assert.Null(message.Emotion);
		Assert.Null(message.Expression);
		Assert.Null(message.Action);
	}

	[Fact]
	public async Task 错误事件变成可读的失败而不是半截文本()
	{
		Configure(sessionId: "s");
		LuoLiCoreConversation conversation = Build(_ => Sse(
			"event: delta\ndata: {\"text\":\"说到一半\"}\n\n"
			+ "event: error\ndata: {\"code\":\"provider_error\",\"message\":\"上游挂了\",\"retryable\":false}\n\n"));

		ChatException failure = await Assert.ThrowsAsync<ChatException>(
			() => conversation.RunAsync("在吗", null, CancellationToken.None));

		Assert.Contains("provider_error", failure.Message, StringComparison.Ordinal);
		Assert.Contains("上游挂了", failure.Message, StringComparison.Ordinal);
	}

	/// <summary>配额拒绝发生在响应头写出之前，所以它仍是普通 HTTP 错误，不是 SSE 事件。</summary>
	[Fact]
	public async Task 配额拒绝按http错误报出()
	{
		Configure(sessionId: "s");
		LuoLiCoreConversation conversation = Build(_ => Json(
			HttpStatusCode.TooManyRequests,
			"{\"code\":\"quota_exceeded\",\"message\":\"额度用完了\",\"retryable\":false}"));

		ChatException failure = await Assert.ThrowsAsync<ChatException>(
			() => conversation.RunAsync("在吗", null, CancellationToken.None));

		Assert.Contains("quota_exceeded", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 连不上时报连接失败而不是抛原始异常()
	{
		Configure(sessionId: "s");
		LuoLiCoreConversation conversation = Build(_ => throw new HttpRequestException("connection refused"));

		ChatException failure = await Assert.ThrowsAsync<ChatException>(
			() => conversation.RunAsync("在吗", null, CancellationToken.None));

		Assert.Contains("连不上", failure.Message, StringComparison.Ordinal);
	}

	private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(responder(request));
	}
}
