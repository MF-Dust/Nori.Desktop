using System.Text.Json;
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

/// <summary>引擎把已有执行路径写进记录，不改授权和去重。</summary>
public sealed class AgentExecutionEngineTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), "nori-exec-" + Guid.NewGuid().ToString("N") + ".db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public AgentExecutionEngineTests()
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
		try { File.Delete(_path); } catch (IOException) { }
		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task 拒绝不会执行工具且记录为拒绝()
	{
		int ran = 0;
		using AgentExecutionJournal journal = new();
		await RunAsync(journal,
			[Tool("writeFile", "confirm", () => { Interlocked.Increment(ref ran); return Task.FromResult<object?>("written"); }, readOnly: false)],
			ToolCall("writeFile"),
			_ => Task.FromResult(false));

		Assert.Equal(0, ran);
		AgentExecutionSnapshot snapshot = journal.Snapshot();
		Assert.Contains(snapshot.Events, ev => ev.Kind == AgentExecutionKind.Request && ev.Status == AgentExecutionStatus.Running);
		Assert.Contains(snapshot.Events, ev => ev.Kind == AgentExecutionKind.Permission && ev.FailureCategory == "requested");
		Assert.Contains(snapshot.Events, ev => ev.Kind == AgentExecutionKind.Permission && ev.Status == AgentExecutionStatus.Denied && !ev.Late);
		AgentExecutionEvent tool = Assert.Single(snapshot.Events, ev => ev.Kind == AgentExecutionKind.Tool && ev.Status == AgentExecutionStatus.Denied && !ev.Late);
		Assert.False(tool.SideEffectsCommitted);
		Assert.Contains(snapshot.Events, ev => ev.Kind == AgentExecutionKind.Session && ev.Status == AgentExecutionStatus.Success);
		Assert.Null(snapshot.Summary.PromptTokens);
		Assert.DoesNotContain("written", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
	}

	[Fact]
	public async Task 同一轮并发工具按调用标识分开且迟到完成不归到另一条()
	{
		using AgentExecutionJournal journal = new();
		TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int arrived = 0;
		Task run = RunAsync(journal,
			[
				Tool("readA", "safe", async () =>
				{
					if (Interlocked.Increment(ref arrived) == 2)
					{
						both.TrySetResult();
					}
					await first.Task;
					return "a";
				}),
				Tool("readB", "safe", async () =>
				{
					if (Interlocked.Increment(ref arrived) == 2)
					{
						both.TrySetResult();
					}
					await second.Task;
					return "b";
				}),
			],
			ToolCall("readA") + "\n" + ToolCall("readB"));
		await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
		first.TrySetResult();
		second.TrySetResult();
		await run.WaitAsync(TimeSpan.FromSeconds(5));

		AgentExecutionSnapshot snapshot = journal.Snapshot();
		AgentExecutionEvent left = Assert.Single(snapshot.Events, ev => ev.ToolName == "readA" && ev.Status == AgentExecutionStatus.Success && !ev.Late);
		AgentExecutionEvent right = Assert.Single(snapshot.Events, ev => ev.ToolName == "readB" && ev.Status == AgentExecutionStatus.Success && !ev.Late);
		Assert.Equal(left.RequestId, right.RequestId);
		Assert.Equal(left.Round, right.Round);
		Assert.NotEqual(left.ToolCallId, right.ToolCallId);
		// 传入引擎登记时的原始键，避免对已 HMAC 的 ToolCallId 再哈希一次而测空槽。
		Assert.False(journal.TryFinish(left.RequestId, left.Round!.Value,
			ToolExecutionTracker.Key("call-readB", "readB", null), "readB", 1, AgentExecutionStatus.Failed, "tool_error", false));
		Assert.Equal(AgentExecutionStatus.Success, journal.Snapshot().Events.Single(ev => ev.Sequence == right.Sequence).Status);
		Assert.Contains(journal.Snapshot().Events, ev =>
			ev.Late && ev.ToolCallId == right.ToolCallId && ev.Status == AgentExecutionStatus.Failed);
		Assert.Equal(2, snapshot.Summary.RealExecutions);
	}

	[Fact]
	public async Task 取消不是成功且迟到的完成不能改写()
	{
		using AgentExecutionJournal journal = new();
		using CancellationTokenSource cancellation = new();
		Task run = RunAsync(journal,
			[Tool("wait", "safe", async (_, ctx) => { await Task.Delay(Timeout.Infinite, ctx.CancellationToken); return "done"; })],
			ToolCall("wait"),
			cancellationToken: cancellation.Token);
		await WaitUntilAsync(() => journal.Snapshot().Events.Any(ev => ev.Kind == AgentExecutionKind.Tool && ev.Status == AgentExecutionStatus.Running));
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

		AgentExecutionSnapshot snapshot = journal.Snapshot();
		AgentExecutionEvent tool = Assert.Single(snapshot.Events, ev => ev.Kind == AgentExecutionKind.Tool && ev.Status == AgentExecutionStatus.Cancelled && !ev.Late);
		Assert.False(tool.SideEffectsCommitted);
		Assert.Contains(snapshot.Events, ev => ev.Kind == AgentExecutionKind.Session && ev.Status == AgentExecutionStatus.Cancelled);
		Assert.False(journal.TryFinish(tool.RequestId, tool.Round!.Value,
			ToolExecutionTracker.Key("call-wait", "wait", null), "wait", 3, AgentExecutionStatus.Success, null, true));
		Assert.Contains(journal.Snapshot().Events, ev =>
			ev.Late && ev.ToolCallId == tool.ToolCallId && ev.Status == AgentExecutionStatus.Success);
		Assert.DoesNotContain(journal.Snapshot().Events, ev => ev.ToolCallId == tool.ToolCallId && ev.Status == AgentExecutionStatus.Success && !ev.Late);
	}

	[Fact]
	public async Task 相同调用标识的第二次不会再执行()
	{
		int ran = 0;
		using AgentExecutionJournal journal = new();
		await RunAsync(journal,
			[Tool("readA", "safe", () => { Interlocked.Increment(ref ran); return Task.FromResult<object?>("value"); })],
			ToolCall("readA") + "\n" + ToolCall("readA"));

		Assert.Equal(1, ran);
		AgentExecutionSnapshot snapshot = journal.Snapshot();
		Assert.Equal(1, snapshot.Summary.RealExecutions);
		Assert.Equal(1, snapshot.Summary.Deduplicated);
	}

	[Fact]
	public async Task 上报的用量保留命中率且未上报时保持空()
	{
		using AgentExecutionJournal journal = new();
		await RunAsync(journal, [], "{\"type\":\"message\",\"text\":\"好\"}", usage: new LlmUsageInfo
		{
			PromptTokens = 10, CompletionTokens = 4, TotalTokens = 14, CachedTokens = 2, Model = "deterministic-model",
		});
		AgentExecutionUsage usage = Assert.IsType<AgentExecutionUsage>(
			journal.Snapshot().Events.Single(ev => ev.Kind == AgentExecutionKind.Model && ev.Usage is not null).Usage);
		Assert.Equal(14, usage.TotalTokens);
		Assert.Equal(20, usage.CacheHitRate);

		await RunAsync(journal, [], "{\"type\":\"message\",\"text\":\"再好\"}");
		Assert.Contains(journal.Snapshot().Events, ev => ev.Kind == AgentExecutionKind.Model && ev.Usage is null && ev.DurationMs is not null);
	}

	private async Task RunAsync(
		AgentExecutionJournal journal,
		IEnumerable<RegisteredTool> tools,
		string firstRound,
		Func<ToolApprovalRequest, Task<bool>>? approve = null,
		CancellationToken cancellationToken = default,
		LlmUsageInfo? usage = null)
	{
		ToolRegistry registry = new();
		foreach (RegisteredTool tool in tools) registry.Register(tool);
		using HttpClient http = new();
		ChatService chat = new(http, _database, _config);
		using EmotionManager emotion = new(_config);
		emotion.Initialize();
		await using MemoryService memory = new(new MemoryStore(_database), new StubEmbedding(), _config);
		ScriptedAdapter adapter = new(usage, firstRound, "{\"type\":\"message\",\"text\":\"完成\"}");
		AgentEngine engine = new(
			http, _config, chat, registry, new SkillService(_config, http), emotion, memory,
			pet: null, motionNames: static () => [], expressionNames: static () => [],
			executionJournal: journal,
			adapterFactory: (_, _) => adapter);
		await engine.RunAsync("跑一轮", "exec-session", new AgentCallbacks
		{
			RequestApproval = approve is null ? null : request => approve(request),
		}, cancellationToken);
	}

	private static async Task WaitUntilAsync(Func<bool> ready)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (!ready())
		{
			if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("执行记录没有出现预期事件");
			await Task.Delay(20);
		}
	}

	private static RegisteredTool Tool(string name, string level, Func<Task<object?>> body, bool readOnly = true) =>
		Tool(name, level, (_, _) => body(), readOnly);

	private static RegisteredTool Tool(string name, string level, Func<JsonNode?, ToolContext, Task<object?>> body, bool readOnly = true) => new()
	{
		Name = name,
		Description = name,
		Parameters = new JsonObject(),
		PermissionLevel = level,
		IsReadOnly = readOnly,
		Execute = body,
	};

	private static string ToolCall(string name) =>
		"{\"type\":\"tool_call\",\"id\":\"call-" + name + "\",\"name\":\"" + name + "\",\"arguments\":{}}";

	private sealed class ScriptedAdapter(LlmUsageInfo? usage, params string[] rounds) : ILlmAdapter
	{
		private int _round;

		public Task<string> CompleteAsync(
			string baseUrl, string apiKey, string model, string systemPrompt,
			IReadOnlyList<ChatMessageInput> messages, CancellationToken cancellationToken = default) =>
			Task.FromResult(Next());

		public Task<string> StreamAsync(
			string baseUrl, string apiKey, string model, string systemPrompt,
			IReadOnlyList<ChatMessageInput> messages, Action<string> onChunk,
			Action<LlmUsageInfo>? onUsage = null, CancellationToken cancellationToken = default)
		{
			if (usage is not null && _round == 0) onUsage?.Invoke(usage);
			string response = Next();
			onChunk(response);
			return Task.FromResult(response);
		}

		private string Next() => rounds[Math.Min(_round++, rounds.Length - 1)];
	}

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
}
