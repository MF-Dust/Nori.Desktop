using System.Diagnostics;
using System.Text.Json.Nodes;
using Nori.Core.Agent;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Emotion;
using Nori.Core.Memory;
using Nori.Core.Skills;
using Nori.Core.Tools;

namespace Nori.Core.Tests;

/// <summary>
/// 同一轮里的多个工具调用怎么执行。
///
/// 原先是 foreach 里挨个 await：模型一轮要三次互不相关的查询时，用户等的是三次往返之和。
/// 现在按权限级别分组 —— safe 的并发跑，其余顺序跑。
///
/// 这一族要钉住三件事，缺哪一件都会留下不报错的缺陷：
/// 1. safe 的确实是并发的（不然改了等于没改）；
/// 2. 要确认的绝不与别的重叠（两个确认框同时弹出来，用户无从分辨哪个对应哪一条）；
/// 3. 写回模型的那段记录按**调用顺序**，与完成顺序无关（否则同一次提问会因为网络快慢
///    产生不同的上下文，问题复现不了）。
/// </summary>
public sealed class AgentParallelToolsTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-parallel-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public AgentParallelToolsTests()
	{
		_database = NoriDatabase.Open(_path);
		_config = new ConfigStore(_database);
		_config.InitDefaults("test");
		_config.Set("llm_provider", new ConfigValue.Text("openai"));
		_config.Set("llm_api_base", new ConfigValue.Text("https://example.test/v1"));
		_config.Set("llm_api_key", new ConfigValue.Text("test-key"));
		_config.Set("llm_model", new ConfigValue.Text("deterministic-model"));
		_config.Set("memory_enabled", new ConfigValue.Boolean(false));
	}

	public void Dispose()
	{
		_database.Dispose();
		try { File.Delete(_path); } catch (IOException) { /* 临时库删不掉不影响断言 */ }
		GC.SuppressFinalize(this);
	}

	// ── 夹具 ───────────────────────────────────────────────────────────────

	/// <summary>按脚本逐轮返回的适配器，并留下每一轮收到的消息。</summary>
	private sealed class ScriptedAdapter(params string[] rounds) : ILlmAdapter
	{
		private int _round;

		/// <summary>每一轮发出去的消息。判断「写回顺序」要看下一轮收到了什么。</summary>
		public List<IReadOnlyList<ChatMessageInput>> Sent { get; } = [];

		public Task<string> CompleteAsync(
			string baseUrl, string apiKey, string model, string systemPrompt,
			IReadOnlyList<ChatMessageInput> messages, CancellationToken cancellationToken = default) =>
			Task.FromResult(Next(messages));

		public Task<string> StreamAsync(
			string baseUrl, string apiKey, string model, string systemPrompt,
			IReadOnlyList<ChatMessageInput> messages, Action<string> onChunk,
			Action<LlmUsageInfo>? onUsage = null, CancellationToken cancellationToken = default)
		{
			string response = Next(messages);
			onChunk(response);
			return Task.FromResult(response);
		}

		private string Next(IReadOnlyList<ChatMessageInput> messages)
		{
			Sent.Add([.. messages]);
			return rounds[Math.Min(_round++, rounds.Length - 1)];
		}
	}

	/// <summary>记录每个工具的进出时刻，用来判断有没有重叠。</summary>
	private sealed class Windows
	{
		private readonly object _gate = new();
		private readonly Dictionary<string, (long Start, long End)> _spans = [];
		private int _running;

		public int Peak { get; private set; }

		public IDisposable Enter(string name)
		{
			lock (_gate)
			{
				_running++;
				if (_running > Peak) Peak = _running;
				_spans[name] = (Stopwatch.GetTimestamp(), 0);
			}
			return new Exit(this, name);
		}

		public bool Overlaps(string left, string right)
		{
			lock (_gate)
			{
				(long aStart, long aEnd) = _spans[left];
				(long bStart, long bEnd) = _spans[right];
				return aStart < bEnd && bStart < aEnd;
			}
		}

		private sealed class Exit(Windows owner, string name) : IDisposable
		{
			public void Dispose()
			{
				lock (owner._gate)
				{
					owner._running--;
					owner._spans[name] = (owner._spans[name].Start, Stopwatch.GetTimestamp());
				}
			}
		}
	}

	private static RegisteredTool Tool(string name, string level, Func<Task<object?>> body) => new()
	{
		Name = name,
		Description = name,
		Parameters = new JsonObject(),
		PermissionLevel = level,
		Execute = async (_, _) => await body(),
	};

	private static string ToolCall(string name) =>
		"{\"type\":\"tool_call\",\"id\":\"call-" + name + "\",\"name\":\"" + name + "\",\"arguments\":{}}";

	private static string Final(string text) => "{\"type\":\"message\",\"text\":\"" + text + "\"}";

	private AgentEngine Build(ToolRegistry tools, ScriptedAdapter adapter, EmotionManager emotion, MemoryService memory)
	{
		HttpClient http = new();
		ChatService chat = new(http, _database, _config);
		return new AgentEngine(
			http, _config, chat, tools, new SkillService(_config, http), emotion, memory,
			pet: null, motionNames: static () => [], expressionNames: static () => [],
			adapterFactory: (_, _) => adapter);
	}

	private async Task<(ScriptedAdapter Adapter, Windows Spans)> RunAsync(
		IEnumerable<RegisteredTool> tools, string firstRound,
		Func<ToolApprovalRequest, Task<bool>>? approve = null)
	{
		ToolRegistry registry = new();
		foreach (RegisteredTool tool in tools) registry.Register(tool);

		ScriptedAdapter adapter = new(firstRound, Final("完成"));
		using EmotionManager emotion = new(_config);
		emotion.Initialize();
		await using MemoryService memory = new(new MemoryStore(_database), new StubEmbedding(), _config);

		AgentEngine engine = Build(registry, adapter, emotion, memory);
		await engine.RunAsync("跑一轮", "parallel-session", new AgentCallbacks
		{
			RequestApproval = approve is null ? null : request => approve(request),
		}, CancellationToken.None);
		return (adapter, _spans);
	}

	private Windows _spans = new();

	private sealed class StubEmbedding : Nori.Core.Embedding.IEmbeddingAdapter
	{
		public Task<float[]> GetEmbeddingAsync(
			string baseUrl, string apiKey, string model, string input,
			int? dimensions = null, CancellationToken cancellationToken = default) =>
			Task.FromResult<float[]>([1f, 0f]);

		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(
			string baseUrl, string apiKey, string model, IReadOnlyList<string> inputs,
			int? dimensions = null, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<float[]>>([[1f, 0f]]);
	}

	// ── 断言 ───────────────────────────────────────────────────────────────

	/// <summary>
	/// 三个 safe 工具同一轮出现时并发执行。
	///
	/// 判据不是耗时（机器快慢会让它不稳），而是**三个都进来之后才放行** —— 顺序执行时
	/// 第一个会一直等下去，测试超时失败。
	/// </summary>
	[Fact]
	public async Task 同一轮的安全工具并发执行()
	{
		_spans = new Windows();
		using CountdownEvent gate = new(3);
		Task<(ScriptedAdapter, Windows)> run = RunAsync(
			[
				Tool("a", "safe", () => Arrive(gate)),
				Tool("b", "safe", () => Arrive(gate)),
				Tool("c", "safe", () => Arrive(gate)),
			],
			ToolCall("a") + "\n" + ToolCall("b") + "\n" + ToolCall("c"));

		(_, Windows spans) = await run.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(3, spans.Peak);
	}

	/// <summary>并发上限：一轮里给 6 个 safe 工具，同时在跑的不超过 MaxParallelTools。</summary>
	[Fact]
	public async Task 并发数不超过上限()
	{
		_spans = new Windows();
		List<RegisteredTool> tools = [];
		List<string> calls = [];
		for (int index = 0; index < 6; index++)
		{
			string name = $"t{index}";
			tools.Add(Tool(name, "safe", async () =>
			{
				using IDisposable _ = _spans.Enter(name);
				await Task.Delay(30);
				return name;
			}));
			calls.Add(ToolCall(name));
		}

		(_, Windows spans) = await RunAsync(tools, string.Join("\n", calls));

		Assert.True(spans.Peak <= AgentEngine.MaxParallelTools,
			$"同时在跑 {spans.Peak} 个，超过上限 {AgentEngine.MaxParallelTools}");
	}

	/// <summary>
	/// 要确认的工具不与任何别的工具重叠。
	///
	/// 两个确认框同时弹出来，用户无从分辨哪个对应哪一条；而且这一级的工具通常有副作用，
	/// 顺序不能乱。
	/// </summary>
	[Fact]
	public async Task 需要确认的工具不与别的并发()
	{
		_spans = new Windows();
		(_, Windows spans) = await RunAsync(
			[
				Tool("safe1", "safe", () => Span("safe1")),
				Tool("ask", "confirm", () => Span("ask")),
				Tool("safe2", "safe", () => Span("safe2")),
			],
			ToolCall("safe1") + "\n" + ToolCall("ask") + "\n" + ToolCall("safe2"),
			approve: _ => Task.FromResult(true));

		Assert.False(spans.Overlaps("ask", "safe1"));
		Assert.False(spans.Overlaps("ask", "safe2"));
	}

	/// <summary>
	/// 写回模型的顺序是调用顺序，不是完成顺序。
	///
	/// 让第一个慢、第二个快，再看下一轮发出去的消息里两段反馈谁在前。乱序的后果是同一次
	/// 提问会因为网络快慢产生不同的上下文 —— 问题因此复现不了。
	/// </summary>
	[Fact]
	public async Task 结果按调用顺序写回()
	{
		_spans = new Windows();
		(ScriptedAdapter adapter, _) = await RunAsync(
			[
				Tool("slow", "safe", async () => { await Task.Delay(120); return "慢"; }),
				Tool("fast", "safe", () => Task.FromResult<object?>("快")),
			],
			ToolCall("slow") + "\n" + ToolCall("fast"));

		// 第二轮收到的消息里，两段工具反馈按调用顺序排列。
		string transcript = string.Join("\n", adapter.Sent[^1].Select(message => message.Content));
		int slowAt = transcript.IndexOf("slow", StringComparison.Ordinal);
		int fastAt = transcript.IndexOf("fast", StringComparison.Ordinal);
		Assert.True(slowAt >= 0 && fastAt > slowAt, $"顺序不对：slow@{slowAt} fast@{fastAt}");
	}

	private async Task<object?> Arrive(CountdownEvent gate)
	{
		using IDisposable _ = _spans.Enter(Guid.NewGuid().ToString("N"));
		gate.Signal();
		// 三个都到齐才放行。顺序执行时这里等不到，测试由 WaitAsync 超时失败。
		await Task.Run(() => gate.Wait(TimeSpan.FromSeconds(8)));
		return "ok";
	}

	private async Task<object?> Span(string name)
	{
		using IDisposable _ = _spans.Enter(name);
		await Task.Delay(40);
		return name;
	}
}
