using System.Net;
using System.Text;
using Nori.Core.Agent;
using Nori.Core.Chat;
using Nori.Core.Chat.LuoLiCore;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Security;

namespace Nori.Core.Tests;

/// <summary>
/// AgentEngine 把一轮交给 LuoLiCore 的分支。
///
/// 这条分支动的是桌宠的核心对话路径，所以两个方向都要钉住：启用时确实绕开本机的
/// 模型与工具循环；未启用时这条路完全不存在。
/// </summary>
public sealed class AgentEngineLuoLiCoreTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-engine-luoli-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	private sealed class FixedKeyStore : ISecretKeyStore
	{
		private readonly byte[] _key = Enumerable.Range(0, SecretKeyStore.KeySize).Select(index => (byte)index).ToArray();

		public byte[] LoadOrCreate() => _key;

		public bool IsFileFallback => true;
	}

	public AgentEngineLuoLiCoreTests()
	{
		_database = NoriDatabase.Open(_path);
		_config = new ConfigStore(_database, new FixedKeyStore());
		_config.InitDefaults("test");
	}

	public void Dispose()
	{
		_database.Dispose();
		try { File.Delete(_path); } catch (IOException) { /* 临时库删不掉不影响断言 */ }
	}

	private void EnableLuoLiCore(bool enabled = true)
	{
		_config.Set(LuoLiCoreSettingsStore.KeyEnabled, new ConfigValue.Boolean(enabled));
		_config.Set(LuoLiCoreSettingsStore.KeyBaseUrl, new ConfigValue.Text("http://127.0.0.1:3000"));
		_config.Set(LuoLiCoreSettingsStore.KeyApiKey, new ConfigValue.Text("sk-test"));
		_config.Set(LuoLiCoreSettingsStore.KeySessionId, new ConfigValue.Text("sess_fixed"));
	}

	private LuoLiCoreConversation Conversation(Func<HttpRequestMessage, HttpResponseMessage> responder)
	{
		HttpClient http = new(new StubHandler(responder));
		return new LuoLiCoreConversation(new LuoLiCoreSettingsStore(_config), options => new LuoLiCoreSdkClient(http, options));
	}

	private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
	};

	/// <summary>
	/// 本机那条路一旦被走到就会立刻炸。用它证明「确实没走本机」，而不是靠断言输出相等
	/// —— 输出相等只说明结果一样，说不清是谁产出的。
	/// </summary>
	private static ILlmAdapter ExplodingAdapter(LlmProvider provider, HttpClient http) =>
		throw new InvalidOperationException("不应该走到本机模型这条路");

	/// <summary>
	/// 工具、技能、情绪、记忆、伴侣动作五项传 null。
	///
	/// 这不是图省事 —— **这本身就是断言**：LuoLiCore 那条分支在读 LLM 配置之前就返回，
	/// 一次都不该碰到它们。真的碰了就是 NullReference，测试立刻炸；换成构造出真实协作者，
	/// 反而看不出「有没有被碰过」。
	///
	/// 构造函数只做字段赋值，不解引用这几项，所以这样构造是安全的。
	/// </summary>
	private AgentEngine BuildEngine(LuoLiCoreConversation? conversation)
	{
		ChatService chat = new(new HttpClient(), _database, _config);
		return new AgentEngine(
			new HttpClient(),
			_config,
			chat,
			tools: null!,
			skills: null!,
			emotion: null!,
			memory: null!,
			pet: null,
			motionNames: () => [],
			expressionNames: () => [],
			adapterFactory: ExplodingAdapter,
			luoLiCore: conversation);
	}

	[Fact]
	public async Task 启用时绕开本机模型与工具循环()
	{
		EnableLuoLiCore();
		AgentEngine engine = BuildEngine(Conversation(_ => Sse("event: done\ndata: {\"text\":\"我在\"}\n\n")));

		List<AgentRunState> states = [];
		List<string> chunks = [];
		ProtocolMessage message = await engine.RunAsync(
			"在吗",
			"session-1",
			new AgentCallbacks { OnState = states.Add, OnTextChunk = chunks.Add },
			CancellationToken.None);

		// 走到本机适配器会抛，能返回就说明没走。
		Assert.Equal("我在", message.Text);
		Assert.Contains(AgentRunState.Streaming, states);
		Assert.Equal(AgentRunState.Idle, states[^1]);
	}

	/// <summary>只配了 LuoLiCore 的实例本来就没有 BaseUrl / ApiKey / Model 三项。</summary>
	[Fact]
	public async Task 没有配置本机LLM也能跑()
	{
		EnableLuoLiCore();
		// 刻意不写 llm_api_base / llm_api_key / llm_model
		AgentEngine engine = BuildEngine(Conversation(_ => Sse("event: done\ndata: {\"text\":\"好\"}\n\n")));

		ProtocolMessage message = await engine.RunAsync("嗨", "session-2", new AgentCallbacks(), CancellationToken.None);

		Assert.Equal("好", message.Text);
	}

	[Fact]
	public async Task 增量逐块回调()
	{
		EnableLuoLiCore();
		AgentEngine engine = BuildEngine(Conversation(_ => Sse(
			"event: delta\ndata: {\"text\":\"你\"}\n\n"
			+ "event: delta\ndata: {\"text\":\"好\"}\n\n"
			+ "event: done\ndata: {\"text\":\"你好\"}\n\n")));

		List<string> chunks = [];
		await engine.RunAsync("嗨", "session-3", new AgentCallbacks { OnTextChunk = chunks.Add }, CancellationToken.None);

		Assert.Equal(["你", "好"], chunks);
	}

	[Fact]
	public async Task 完成回调照常触发()
	{
		EnableLuoLiCore();
		AgentEngine engine = BuildEngine(Conversation(_ => Sse("event: done\ndata: {\"text\":\"嗯\"}\n\n")));

		ProtocolMessage? completed = null;
		await engine.RunAsync("在吗", "session-4", new AgentCallbacks { OnComplete = value => completed = value }, CancellationToken.None);

		Assert.NotNull(completed);
		Assert.Equal("嗯", completed!.Text);
	}

	[Fact]
	public async Task 失败时状态落到Error而不是Idle()
	{
		EnableLuoLiCore();
		AgentEngine engine = BuildEngine(Conversation(_ => Sse(
			"event: error\ndata: {\"code\":\"provider_error\",\"message\":\"上游挂了\",\"retryable\":false}\n\n")));

		List<AgentRunState> states = [];
		await Assert.ThrowsAsync<ChatException>(
			() => engine.RunAsync("在吗", "session-5", new AgentCallbacks { OnState = states.Add }, CancellationToken.None));

		Assert.Equal(AgentRunState.Error, states[^1]);
	}

	[Fact]
	public async Task 未启用时不走这条路()
	{
		EnableLuoLiCore(enabled: false);
		AgentEngine engine = BuildEngine(Conversation(_ => Sse("event: done\ndata: {\"text\":\"不该出现\"}\n\n")));

		// 没配本机 LLM，因此走本机那条会在配置检查处失败 —— 证明分支没有被走到。
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => engine.RunAsync("在吗", "session-6", new AgentCallbacks(), CancellationToken.None));
	}

	[Fact]
	public async Task 没有注入这条路时行为不变()
	{
		AgentEngine engine = BuildEngine(null);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => engine.RunAsync("在吗", "session-7", new AgentCallbacks(), CancellationToken.None));
	}

	private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(responder(request));
	}
}
