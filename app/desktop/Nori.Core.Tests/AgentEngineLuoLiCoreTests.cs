using System.Net;
using System.Text;
using Nori.Core.Agent;
using Nori.Core.Chat;
using Nori.Core.Chat.LuoLiCore;
using Nori.Core.Emotion;
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
	private readonly List<IDisposable> _disposables = [];

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
		foreach (IDisposable item in _disposables) item.Dispose();
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
	private AgentEngine BuildEngine(
		LuoLiCoreConversation? conversation,
		ReplyReactionService? reaction = null,
		IReadOnlyList<string>? motions = null,
		IReadOnlyList<string>? expressions = null,
		bool motionsThrow = false)
	{
		ChatService chat = new(new HttpClient(), _database, _config);
		// 补表情那条路会读情绪，给它一个真的；其余四项仍然是 null 断言。
		EmotionManager? emotion = reaction is null ? null : Track(new EmotionManager(_config));
		return new AgentEngine(
			new HttpClient(),
			_config,
			chat,
			tools: null!,
			skills: null!,
			emotion: emotion!,
			memory: null!,
			pet: null,
			motionNames: motionsThrow
				? () => throw new InvalidOperationException("模型还没加载好")
				: () => motions ?? [],
			expressionNames: () => expressions ?? [],
			adapterFactory: ExplodingAdapter,
			luoLiCore: conversation,
			replyReaction: reaction);
	}

	private EmotionManager Track(EmotionManager emotion)
	{
		_disposables.Add(emotion);
		return emotion;
	}

	[Fact]
	public async Task 启用时绕开本机模型与工具循环()
	{
		EnableLuoLiCore();
		AgentEngine engine = BuildEngine(Conversation(_ => Sse("event: done\ndata: {\"text\":\"我在\"}\n\n")));

		List<AgentRunState> states = [];
		ProtocolMessage message = await engine.RunAsync(
			"在吗",
			"session-1",
			new AgentCallbacks { OnState = states.Add },
			CancellationToken.None);

		// 走到本机适配器会抛，能返回就说明没走。
		Assert.Equal("我在", message.Text);
		Assert.Equal(AgentRunState.Idle, states[^1]);
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

	// ---- 表情动作：远端只给文本，这几项由本地补 ----

	private void ConfigureLocalLlm()
	{
		_config.Set(AiSettingsStore.KeyLlmBaseUrl, new ConfigValue.Text("https://llm.example/v1"));
		_config.Set(AiSettingsStore.KeyLlmApiKey, new ConfigValue.Text("sk-local"));
		_config.Set(AiSettingsStore.KeyLlmModel, new ConfigValue.Text("m"));
	}

	private sealed class FixedReplyAdapter(string reply) : ILlmAdapter
	{
		public Task<string> CompleteAsync(
			string baseUrl, string apiKey, string model, string systemPrompt,
			IReadOnlyList<ChatMessageInput> messages, CancellationToken cancellationToken = default) =>
			Task.FromResult(reply);

		public Task<string> StreamAsync(
			string baseUrl, string apiKey, string model, string systemPrompt,
			IReadOnlyList<ChatMessageInput> messages, Action<string> onChunk,
			Action<LlmUsageInfo>? onUsage = null, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<string>> FetchModelsAsync(
			string baseUrl, string apiKey, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}

	private ReplyReactionService Reaction(string json) =>
		new(new HttpClient(), _config, (_, _) => new FixedReplyAdapter(json));

	[Fact]
	public async Task 远端只给文本_表情动作在本地补上()
	{
		EnableLuoLiCore();
		ConfigureLocalLlm();
		AgentEngine engine = BuildEngine(
			Conversation(_ => Sse("event: done\ndata: {\"text\":\"我在\"}\n\n")),
			reaction: Reaction("{\"emotion\":\"happy\",\"expression\":\"smile\",\"action\":\"Tap\"}"),
			motions: ["Tap"],
			expressions: ["smile"]);

		ProtocolMessage message = await engine.RunAsync("在吗", "session-8", new AgentCallbacks(), CancellationToken.None);

		Assert.Equal("我在", message.Text);
		Assert.Equal("happy", message.Emotion);
		Assert.Equal("smile", message.Expression);
		Assert.Equal("Tap", message.Action);
	}

	/// <summary>
	/// 取候选名单要读当前模型，模型还没加载好时会抛。
	///
	/// 「挑表情失败」和「她没话说」在用户那边看起来一模一样，所以这条必须退化成没有表情，
	/// 而不是让整轮对话失败。
	/// </summary>
	[Fact]
	public async Task 挑表情时出错不影响这一轮()
	{
		EnableLuoLiCore();
		ConfigureLocalLlm();
		AgentEngine engine = BuildEngine(
			Conversation(_ => Sse("event: done\ndata: {\"text\":\"我在\"}\n\n")),
			reaction: Reaction("{\"expression\":\"smile\"}"),
			motionsThrow: true);

		ProtocolMessage message = await engine.RunAsync("在吗", "session-9", new AgentCallbacks(), CancellationToken.None);

		Assert.Equal("我在", message.Text);
		Assert.Null(message.Expression);
	}

	/// <summary>没有候选名单时不该凭空冒出一个动作名。</summary>
	[Fact]
	public async Task 没有候选名单时保持空值()
	{
		EnableLuoLiCore();
		ConfigureLocalLlm();
		AgentEngine engine = BuildEngine(
			Conversation(_ => Sse("event: done\ndata: {\"text\":\"我在\"}\n\n")),
			reaction: Reaction("{\"expression\":\"smile\",\"action\":\"Tap\"}"));

		ProtocolMessage message = await engine.RunAsync("在吗", "session-10", new AgentCallbacks(), CancellationToken.None);

		Assert.Null(message.Expression);
		Assert.Null(message.Action);
	}

	/// <summary>
	/// 排队期间不能显示成「正在说」。
	///
	/// 对端的排队闸可能让轮次等上数秒，而那条流在轮次执行期间没有任何保活帧。见到第一个
	/// 增量才算开始说话，跟本机那条路一致。
	/// </summary>
	[Fact]
	public async Task 排队时停在Thinking_见到增量才转Streaming()
	{
		EnableLuoLiCore();
		AgentEngine engine = BuildEngine(Conversation(_ => Sse(
			"event: queued\ndata: {}\n\n"
			+ "event: delta\ndata: {\"text\":\"在\"}\n\n"
			+ "event: done\ndata: {\"text\":\"在的\"}\n\n")));

		List<AgentRunState> states = [];
		await engine.RunAsync("在吗", "session-11", new AgentCallbacks { OnState = states.Add }, CancellationToken.None);

		Assert.Equal(
			[AgentRunState.Thinking, AgentRunState.Thinking, AgentRunState.Streaming, AgentRunState.Idle],
			states);
	}

	/// <summary>一次性给完整文本的轮次没有「正在说」这一段，直接从想到说完。</summary>
	[Fact]
	public async Task 没有增量时不进入说话态()
	{
		EnableLuoLiCore();
		AgentEngine engine = BuildEngine(Conversation(_ => Sse("event: done\ndata: {\"text\":\"在的\"}\n\n")));

		List<AgentRunState> states = [];
		await engine.RunAsync("在吗", "session-12", new AgentCallbacks { OnState = states.Add }, CancellationToken.None);

		Assert.DoesNotContain(AgentRunState.Streaming, states);
		Assert.Equal(AgentRunState.Idle, states[^1]);
	}

	[Fact]
	public async Task 清空聊天记录时一并重置远端()
	{
		EnableLuoLiCore();
		List<string> paths = [];
		AgentEngine engine = BuildEngine(Conversation(request =>
		{
			paths.Add(request.RequestUri!.AbsolutePath);
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("{\"ok\":true,\"commitId\":\"c1\"}", Encoding.UTF8, "application/json"),
			};
		}));

		Assert.True(await engine.ResetRemoteContextAsync(CancellationToken.None));
		Assert.Equal(["/sdk/v1/sessions/sess_fixed/reset"], paths);
	}

	/// <summary>没接这条路的实例照常能清本地，不该因此报错。</summary>
	[Fact]
	public async Task 没有注入这条路时重置是空操作()
	{
		AgentEngine engine = BuildEngine(null);

		Assert.False(await engine.ResetRemoteContextAsync(CancellationToken.None));
	}


	// ---- codex review 的两条 P1：落库顺序与取消窗口 ----

	/// <summary>
	/// 用一个真实的 ChatService 建引擎，并把「取候选名单」这一步当作挑表情的时机探针。
	///
	/// 那一步在 <c>AttachReactionAsync</c> 的 try 里、紧跟在落库之后，是观察「落库有没有先
	/// 发生」最靠近的一个点。
	/// </summary>
	private AgentEngine BuildWithProbe(
		ChatService chat,
		LuoLiCoreConversation conversation,
		ReplyReactionService reaction,
		Func<IReadOnlyList<string>> motionNames) =>
		new(
			new HttpClient(),
			_config,
			chat,
			tools: null!,
			skills: null!,
			emotion: Track(new EmotionManager(_config)),
			memory: null!,
			pet: null,
			motionNames: motionNames,
			expressionNames: () => ["smile"],
			adapterFactory: ExplodingAdapter,
			luoLiCore: conversation,
			replyReaction: reaction);

	/// <summary>
	/// done 之后按停止，这一轮必须仍然落进本地历史。
	///
	/// 对端那一轮已经提交、界面上也已经有文本了，此刻若让取消把整轮掀掉，本地就永远没有这条
	/// 记录 —— 远端记着、用户看见过、本地没有，下一轮的远端上下文与用户所见对不上。
	/// </summary>
	[Fact]
	public async Task 完成之后取消仍然落库()
	{
		EnableLuoLiCore();
		ConfigureLocalLlm();
		using CancellationTokenSource stop = new();
		ChatService chat = new(new HttpClient(), _database, _config);

		AgentEngine engine = BuildWithProbe(
			chat,
			Conversation(_ => Sse("event: done\ndata: {\"text\":\"我在\"}\n\n")),
			Reaction("{\"expression\":\"smile\"}"),
			() =>
			{
				stop.Cancel();
				stop.Token.ThrowIfCancellationRequested();
				return [];
			});

		ProtocolMessage message = await engine.RunAsync(
			"在吗", "session-cancel", new AgentCallbacks(), stop.Token);

		Assert.Equal("我在", message.Text);
		// 用户那条 + 她那条，两条都在。
		Assert.Equal(2, chat.GetHistory().Count);
	}

	[Fact]
	public async Task 挑表情之前就已经落库()
	{
		EnableLuoLiCore();
		ConfigureLocalLlm();
		ChatService chat = new(new HttpClient(), _database, _config);
		int atReactionTime = -1;

		AgentEngine engine = BuildWithProbe(
			chat,
			Conversation(_ => Sse("event: done\ndata: {\"text\":\"我在\"}\n\n")),
			Reaction("{\"expression\":\"smile\",\"action\":\"Tap\"}"),
			() =>
			{
				atReactionTime = chat.GetHistory().Count;
				return ["Tap"];
			});

		ProtocolMessage message = await engine.RunAsync(
			"在吗", "session-order", new AgentCallbacks(), CancellationToken.None);

		Assert.Equal(2, atReactionTime);
		// 顺序调整不能把表情弄丢。
		Assert.Equal("smile", message.Expression);
	}

	[Fact]
	public void 有会话时报告远端仍记着东西()
	{
		EnableLuoLiCore();
		AgentEngine engine = BuildEngine(Conversation(_ => Sse("event: done\ndata: {}\n\n")));

		Assert.True(engine.HasRemoteContext);
	}

	[Fact]
	public void 没接这条路时不报告远端上下文()
	{
		Assert.False(BuildEngine(null).HasRemoteContext);
	}

	private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(responder(request));
	}
}
