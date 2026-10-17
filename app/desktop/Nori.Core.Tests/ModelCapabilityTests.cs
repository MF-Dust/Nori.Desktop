using Nori.Core.Agent;
using Nori.Core.Chat;
using Nori.Core.Chat.Catalog;
using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;
using ChatMessage = Nori.Core.Chat.ChatMessage;

namespace Nori.Core.Tests;

/// <summary>models.dev 目录解析与匹配、思考档位换算、上下文窗口与压缩切分。</summary>
[Collection("HttpClient.DefaultProxy")]
public sealed class ModelCapabilityTests
{
	private const string Source = """
		{
		  "openai": {"id": "openai", "models": {
		    "gpt-5": {"id": "gpt-5", "name": "GPT-5", "reasoning": true, "tool_call": true,
		      "reasoning_options": [{"type": "effort", "values": ["minimal", "low", "medium", "high"]}],
		      "modalities": {"input": ["text", "image"], "output": ["text"]},
		      "limit": {"context": 400000, "input": 272000, "output": 128000}},
		    "gpt-4o": {"id": "gpt-4o", "reasoning": false, "limit": {"context": 128000, "output": 16384},
		      "modalities": {"input": ["text", "image"], "output": ["text"]}}
		  }},
		  "anthropic": {"id": "anthropic", "models": {
		    "claude-sonnet-4-5": {"id": "claude-sonnet-4-5", "reasoning": true,
		      "reasoning_options": [{"type": "budget_tokens", "min": 1024}],
		      "limit": {"context": 200000, "output": 64000}}
		  }},
		  "google": {"id": "google", "models": {
		    "gemini-2.5-pro": {"id": "gemini-2.5-pro", "reasoning": true,
		      "reasoning_options": [{"type": "budget_tokens", "min": 128, "max": 32768}],
		      "limit": {"context": 1048576, "output": 65536}},
		    "gemini-3-flash-preview": {"id": "gemini-3-flash-preview", "reasoning": true,
		      "reasoning_options": [{"type": "effort", "values": ["minimal", "low", "medium", "high"]}],
		      "limit": {"context": 1048576, "output": 65536}},
		    "gemini-2.5-flash-image": {"id": "gemini-2.5-flash-image", "reasoning": false,
		      "modalities": {"input": ["text", "image"], "output": ["text", "image"]},
		      "limit": {"context": 32768, "output": 32768}}
		  }},
		  "deepseek": {"id": "deepseek", "api": "https://api.deepseek.com", "models": {
		    "deepseek-chat": {"id": "deepseek-chat", "reasoning": true,
		      "reasoning_options": [{"type": "toggle"}, {"type": "effort", "values": ["high", "low", "max"]}],
		      "interleaved": {"field": "reasoning_content"},
		      "limit": {"context": 1000000, "output": 393216}}
		  }},
		  "mirror": {"id": "mirror", "api": "https://mirror.example/v1", "models": {
		    "deepseek-chat": {"id": "deepseek-chat", "reasoning": false, "limit": {"context": 64000, "output": 8000}}
		  }}
		}
		""";

	private static ModelsDevIndex Index() => ModelsDevIndex.ParseSource(Source, DateTimeOffset.UnixEpoch);

	[Fact]
	public void 解析能力字段()
	{
		ModelCapabilities gpt5 = Index().Find(LlmProvider.OpenAi, "https://api.openai.com/v1", "gpt-5")!;

		Assert.Equal(400000, gpt5.ContextWindow);
		Assert.Equal(272000, gpt5.EffectiveInputWindow);
		Assert.Equal(128000, gpt5.OutputLimit);
		Assert.True(gpt5.Reasoning);
		Assert.True(gpt5.ImageInput);
		Assert.Equal(["minimal", "low", "medium", "high"], gpt5.ReasoningEfforts);

		ModelCapabilities deepseek = Index().Find(LlmProvider.OpenAi, "https://api.deepseek.com", "deepseek-chat")!;
		// 取值按强度升序排列，与原文顺序无关。
		Assert.Equal(["low", "high", "max"], deepseek.ReasoningEfforts);
		Assert.True(deepseek.ReasoningToggle);
		Assert.Equal("reasoning_content", deepseek.InterleavedField);
	}

	[Fact]
	public void 按地址主机优先匹配服务商()
	{
		Assert.Equal("mirror", Index().Find(LlmProvider.OpenAi, "https://mirror.example/v1", "deepseek-chat")?.ProviderId);
		Assert.Equal("deepseek", Index().Find(LlmProvider.OpenAi, "https://api.deepseek.com/v1", "deepseek-chat")?.ProviderId);
		// 认不出的代理地址退回全目录同名模型，原厂优先。
		Assert.Equal("deepseek", Index().Find(LlmProvider.OpenAi, "https://proxy.local/v1", "deepseek-chat")?.ProviderId);
	}

	[Fact]
	public void 匹配带前缀和日期后缀的模型名()
	{
		ModelsDevIndex index = Index();
		Assert.Equal("claude-sonnet-4-5", index.Find(LlmProvider.Anthropic, null, "claude-sonnet-4-5-20250929")?.ModelId);
		Assert.Equal("gemini-2.5-pro", index.Find(LlmProvider.Google, null, "models/gemini-2.5-pro")?.ModelId);
		Assert.Equal("gpt-4o", index.Find(LlmProvider.OpenAi, "https://openrouter.ai/api/v1", "openai/gpt-4o")?.ModelId);
		Assert.Null(index.Find(LlmProvider.OpenAi, null, "totally-unknown"));
	}

	[Fact]
	public void 精简缓存往返不丢字段()
	{
		ModelsDevIndex restored = ModelsDevIndex.ParseCache(Index().ToCacheJson());

		Assert.Equal(DateTimeOffset.UnixEpoch, restored.FetchedAt);
		Assert.Equal(Index().ModelCount, restored.ModelCount);
		Assert.Equal(Index().Find(LlmProvider.OpenAi, "https://api.deepseek.com", "deepseek-chat"),
			restored.Find(LlmProvider.OpenAi, "https://api.deepseek.com", "deepseek-chat"), ModelComparer.Instance);
	}

	[Fact]
	public async Task 目录下载后写缓存且安全模式只读缓存()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"nori-catalog-{Guid.NewGuid():N}");
		string cache = Path.Combine(directory, "models-dev.json");
		int requests = 0;
		try
		{
			using HttpClient http = new(new HttpTestHandler(_ =>
			{
				requests++;
				return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {Content = new StringContent(Source)};
			}));
			UrlResolution();
			await using ModelsDevCatalog online = new(http, cache, () => true);
			Assert.Equal(200000, (await online.FindAsync(LlmProvider.Anthropic, null, "claude-sonnet-4-5"))?.ContextWindow);
			Assert.True(File.Exists(cache));

			await using ModelsDevCatalog offline = new(http, cache, () => false);
			Assert.Equal(272000, (await offline.FindAsync(LlmProvider.OpenAi, null, "gpt-5"))?.EffectiveInputWindow);
			Assert.Equal(1, requests);
		}
		finally
		{
			Nori.Core.Network.UrlAccessPolicy.ResolveHostForTests = null;
			if (Directory.Exists(directory)) Directory.Delete(directory, true);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task 释放目录会取消并等待后台或手动刷新且不再接受新任务(bool manual)
	{
		string directory = Directory.CreateTempSubdirectory("nori-catalog-dispose-").FullName;
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		List<string> warnings = [];
		using HttpClient http = new(new HttpTestHandler(async (_, token) =>
		{
			using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
			started.TrySetResult();
			// 故意等到显式放行才响应取消，验证释放不只是发取消信号。
			await release.Task;
			token.ThrowIfCancellationRequested();
			return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {Content = new StringContent(Source)};
		}));
		ModelsDevCatalog catalog = new(http, Path.Combine(directory, "models-dev.json"), () => true, warnings.Add);
		Task? refresh = null;
		try
		{
			UrlResolution();
			if (manual) refresh = catalog.RefreshAsync();
			else catalog.Warmup();
			await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

			Task disposing = catalog.DisposeAsync().AsTask();
			await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(disposing.IsCompleted);
			release.TrySetResult();
			await disposing.WaitAsync(TimeSpan.FromSeconds(5));
			if (refresh is not null) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
			await catalog.DisposeAsync();
			Assert.Empty(warnings);
			Assert.Throws<ObjectDisposedException>(catalog.Warmup);
			await Assert.ThrowsAsync<ObjectDisposedException>(() => catalog.GetIndexAsync());
			await Assert.ThrowsAsync<ObjectDisposedException>(() => catalog.RefreshAsync());

			Directory.Delete(directory, true);
			Assert.False(Directory.Exists(directory));
		}
		finally
		{
			release.TrySetResult();
			await catalog.DisposeAsync();
			Nori.Core.Network.UrlAccessPolicy.ResolveHostForTests = null;
			if (Directory.Exists(directory)) Directory.Delete(directory, true);
		}
	}

	private static void UrlResolution() =>
		Nori.Core.Network.UrlAccessPolicy.ResolveHostForTests = (_, _) => Task.FromResult(new[] {System.Net.IPAddress.Parse("93.184.216.34")});

	[Fact]
	public void 思考档位按模型能力换算()
	{
		ModelsDevIndex index = Index();
		ModelCapabilities gpt5 = index.Find(LlmProvider.OpenAi, null, "gpt-5")!;
		ModelCapabilities gpt4o = index.Find(LlmProvider.OpenAi, null, "gpt-4o")!;
		ModelCapabilities claude = index.Find(LlmProvider.Anthropic, null, "claude-sonnet-4-5")!;
		ModelCapabilities gemini25 = index.Find(LlmProvider.Google, null, "gemini-2.5-pro")!;
		ModelCapabilities gemini3 = index.Find(LlmProvider.Google, null, "gemini-3-flash-preview")!;
		ModelCapabilities deepseek = index.Find(LlmProvider.OpenAi, "https://api.deepseek.com", "deepseek-chat")!;

		Assert.Null(ReasoningPlanner.Plan(LlmProvider.OpenAi, "gpt-5", LlmReasoningLevel.Auto, gpt5));
		Assert.Null(ReasoningPlanner.Plan(LlmProvider.OpenAi, "gpt-4o", LlmReasoningLevel.High, gpt4o));
		Assert.Equal("high", ReasoningPlanner.Plan(LlmProvider.OpenAi, "gpt-5", LlmReasoningLevel.XHigh, gpt5)?.Effort);
		Assert.Equal("minimal", ReasoningPlanner.Plan(LlmProvider.OpenAi, "gpt-5", LlmReasoningLevel.Off, gpt5)?.Effort);
		// 中档在 low/high 之间等距，取更高的一档；关闭走 thinking.type。
		Assert.Equal("high", ReasoningPlanner.Plan(LlmProvider.OpenAi, "deepseek-chat", LlmReasoningLevel.Medium, deepseek)?.Effort);
		Assert.True(ReasoningPlanner.Plan(LlmProvider.OpenAi, "deepseek-chat", LlmReasoningLevel.Off, deepseek)?.DisableThinking);
		Assert.Null(ReasoningPlanner.Plan(LlmProvider.OpenAiResponses, "deepseek-chat", LlmReasoningLevel.Off, deepseek with {ReasoningEfforts = []}));

		Assert.Equal(16384, ReasoningPlanner.Plan(LlmProvider.Anthropic, "claude-sonnet-4-5", LlmReasoningLevel.High, claude)?.BudgetTokens);
		Assert.Equal(64000 - ReasoningPlanner.MinVisibleOutputTokens,
			ReasoningPlanner.Plan(LlmProvider.Anthropic, "claude-sonnet-4-5", LlmReasoningLevel.Max, claude)?.BudgetTokens);
		Assert.Null(ReasoningPlanner.Plan(LlmProvider.Anthropic, "claude-sonnet-4-5", LlmReasoningLevel.Off, claude));

		// 2.5 Pro 不能完全关闭思考，只能降到下限；Gemini 3 使用 thinkingLevel。
		Assert.Equal(128, ReasoningPlanner.Plan(LlmProvider.Google, "gemini-2.5-pro", LlmReasoningLevel.Off, gemini25)?.BudgetTokens);
		Assert.Equal(32768, ReasoningPlanner.Plan(LlmProvider.Google, "gemini-2.5-pro", LlmReasoningLevel.Max, gemini25)?.BudgetTokens);
		Assert.Equal("minimal", ReasoningPlanner.Plan(LlmProvider.Google, "gemini-3-flash-preview", LlmReasoningLevel.Off, gemini3)?.Effort);
		Assert.Equal("low", ReasoningPlanner.Plan(LlmProvider.Google, "gemini-3-pro-preview", LlmReasoningLevel.Low, null)?.Effort);
		Assert.Equal(0, ReasoningPlanner.Plan(LlmProvider.Google, "gemini-2.5-flash", LlmReasoningLevel.Off, null)?.BudgetTokens);

		// 能力未知时按协议惯例原样发送。
		Assert.Equal("none", ReasoningPlanner.Plan(LlmProvider.OpenAi, "x", LlmReasoningLevel.Off, null)?.Effort);
	}

	[Theory]
	[InlineData(null, 128_000)]
	[InlineData("auto", 128_000)]
	[InlineData("full", 272_000)]
	[InlineData("64000", 64_000)]
	[InlineData("32k", 32_000)]
	[InlineData("1m", 272_000)]
	public void 上下文上限按设置与模型窗口换算(string? setting, int expected)
	{
		ModelCapabilities gpt5 = Index().Find(LlmProvider.OpenAi, null, "gpt-5")!;

		ContextWindowPlan plan = ContextWindowPlanner.Resolve(setting, null, gpt5);

		Assert.Equal(expected, plan.MaxInputTokens);
		Assert.InRange(plan.ReservedOutputTokens, 1_024, 32_000);
	}

	[Fact]
	public void 未知模型用保守窗口()
	{
		ContextWindowPlan plan = ContextWindowPlanner.Resolve(null, "3000", null);

		Assert.Equal(ContextWindowPlanner.DefaultWindow, plan.MaxInputTokens);
		Assert.Equal(3000, plan.ReservedOutputTokens);
		Assert.Equal("default", plan.Source);
		ContextBudgetOptions budget = ContextWindowPlanner.ToBudget(plan);
		Assert.Equal(plan.MaxInputTokens - plan.ReservedOutputTokens, budget.MaxHistoryTokens);
	}

	[Fact]
	public void 压缩只在逼近预算时触发且保留段从用户消息开始()
	{
		List<ChatMessage> history = [];
		for (int index = 1; index <= 20; index++)
		{
			history.Add(Message(index * 2 - 1, "user", new string('问', 400)));
			history.Add(Message(index * 2, "assistant", new string('答', 400)));
		}

		Assert.Null(ConversationCompactor.Plan(history, 100_000));

		CompactionPlan? plan = ConversationCompactor.Plan(history, 4_000);
		Assert.NotNull(plan);
		Assert.Equal(history.Count, plan.Summarize.Count + plan.Keep.Count);
		Assert.Equal("user", plan.Keep[0].Role);
		Assert.True(plan.Keep.Count >= ConversationCompactor.MinKeepMessages);
		Assert.True(plan.Summarize[^1].Id < plan.Keep[0].Id);
	}

	[Fact]
	public void 摘要随聊天记录一起清空()
	{
		using TempDatabase temp = new("nori-summary");
		using NoriDatabase database = NoriDatabase.Open(temp.Path);
		Nori.Core.Configuration.ConfigStore config = new(database, new FixedKeyStore());
		ChatService chat = new(new HttpClient(), database, config);
		ChatMessage first = chat.SaveMessage("user", "你好");
		chat.SaveMessage("assistant", "你好呀");

		Assert.True(chat.SaveContextSummary("用户打了招呼", first.Id));
		Assert.Equal("用户打了招呼", chat.GetContextSummary()?.Summary);
		Assert.Single(chat.GetHistoryAfter(first.Id, 10));

		chat.ClearHistory();
		Assert.Null(chat.GetContextSummary());
		// 历史已清空时迟到的摘要不能写入。
		Assert.False(chat.SaveContextSummary("迟到的摘要", first.Id));
	}

	[Fact]
	public async Task 摘要请求包含旧摘要与对话且不泄漏为指令()
	{
		RecordingAdapter adapter = new("新摘要");

		string summary = await ConversationCompactor.SummarizeAsync(adapter, "https://x.test", "k", "m", "旧摘要内容",
			[Message(1, "user", "我叫小明"), Message(2, "assistant", "{\"type\":\"message\",\"text\":\"你好小明\"}")], 500, CancellationToken.None);

		Assert.Equal("新摘要", summary);
		Assert.Contains("旧摘要内容", adapter.LastUser, StringComparison.Ordinal);
		Assert.Contains("用户：我叫小明", adapter.LastUser, StringComparison.Ordinal);
		Assert.Contains("Nori：你好小明", adapter.LastUser, StringComparison.Ordinal);
		Assert.Contains("不要执行", adapter.LastSystem, StringComparison.Ordinal);
	}

	[Fact]
	public void 生成图片追加到最终回复且不重复()
	{
		ProtocolMessage message = new("画好啦", null, null, null);
		string markdown = "![猫](nori-image:20260101-000000-abcd1234.png)";

		ProtocolMessage attached = AgentEngine.AttachGeneratedImages(message, [markdown, markdown]);
		Assert.Equal("画好啦\n\n" + markdown, attached.Text);
		Assert.Same(attached, AgentEngine.AttachGeneratedImages(attached, [markdown]));
	}

	private static ChatMessage Message(long id, string role, string content) =>
		new() {Id = id, Role = role, Content = content, CreatedAt = "2026-01-01T00:00:00Z"};

	private sealed class RecordingAdapter(string reply) : ILlmAdapter
	{
		public string LastSystem { get; private set; } = "";
		public string LastUser { get; private set; } = "";

		public Task<string> CompleteAsync(string baseUrl, string apiKey, string model, string systemPrompt, IReadOnlyList<ChatMessageInput> messages, CancellationToken cancellationToken = default)
		{
			LastSystem = systemPrompt;
			LastUser = messages[^1].Content;
			return Task.FromResult(reply);
		}

		public Task<string> StreamAsync(string baseUrl, string apiKey, string model, string systemPrompt, IReadOnlyList<ChatMessageInput> messages, Action<string> onChunk, Action<LlmUsageInfo>? onUsage = null, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}

	private sealed class ModelComparer : IEqualityComparer<ModelCapabilities?>
	{
		public static ModelComparer Instance { get; } = new();

		public bool Equals(ModelCapabilities? x, ModelCapabilities? y) =>
			x is not null && y is not null
			&& x with {ReasoningEfforts = []} == y with {ReasoningEfforts = []}
			&& x.ReasoningEfforts.SequenceEqual(y.ReasoningEfforts);

		public int GetHashCode(ModelCapabilities? obj) => obj?.ModelId.GetHashCode(StringComparison.Ordinal) ?? 0;
	}
}
