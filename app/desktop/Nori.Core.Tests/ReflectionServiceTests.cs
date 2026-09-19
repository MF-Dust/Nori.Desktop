using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Embedding;
using Nori.Core.Memory;

namespace Nori.Core.Tests;

public sealed class ReflectionServiceTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nori-reflection-" + Guid.NewGuid().ToString("N"));
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly MemoryService _memory;
	private readonly ChatService _chat;
	private readonly HttpClient _http = new();

	public ReflectionServiceTests()
	{
		AppStoragePaths paths = new(_root);
		Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);
		_database = NoriDatabase.Open(paths.DatabasePath, paths);
		_config = new ConfigStore(_database, new Security.SecretKeyStore(paths));
		_config.Set(AiSettingsStore.KeyLlmProvider, new ConfigValue.Text("openai"));
		_config.Set(AiSettingsStore.KeyLlmBaseUrl, new ConfigValue.Text("https://llm.example/v1"));
		_config.Set(AiSettingsStore.KeyLlmApiKey, new ConfigValue.Text("test-key"));
		_config.Set(AiSettingsStore.KeyLlmModel, new ConfigValue.Text("test-model"));
		_config.Set("memory_reflection_enabled", new ConfigValue.Boolean(true));
		_config.Set("memory_reflection_rounds", new ConfigValue.Integer(2));
		_config.Set("memory_reflection_min_chars", new ConfigValue.Integer(100));
		_memory = new MemoryService(new MemoryStore(_database), new NoEmbedding(), _config, startBackgroundWorker: false);
		_chat = new ChatService(_http, _database, _config);
	}

	[Fact]
	public async Task 相同失败窗口达到阈值后暂停且新消息只重新尝试一次()
	{
		SequenceAdapter adapter = new(call => call <= 4
			? "{\"shouldStore\":"
			: """{"shouldStore":false,"summary":"","personaSummary":"","topics":[],"importance":0.2,"keyFacts":[]}""");
		ReflectionService service = new(_http, _chat, _memory, _config, (_, _) => adapter);
		Assert.True(_memory.Settings.ReflectionEnabled);
		Assert.Equal(2, _memory.Settings.ReflectionRounds);
		_chat.SaveMessage("user", "第一轮用户消息");
		ChatMessage firstAssistant = _chat.SaveMessage("assistant", new string('答', 120));

		for (int attempt = 0; attempt < 3; attempt++)
		{
			ReflectionParseException error = await Assert.ThrowsAsync<ReflectionParseException>(
				() => service.ReflectPendingAsync());
			string diagnostic = ReflectionDiagnostics.Format(error);
			Assert.Contains("category=json_parse", diagnostic, StringComparison.Ordinal);
			Assert.Contains("stage=root", diagnostic, StringComparison.Ordinal);
			Assert.Contains("provider=openai", diagnostic, StringComparison.Ordinal);
			Assert.Contains("model=test-model", diagnostic, StringComparison.Ordinal);
			Assert.DoesNotContain("shouldStore", diagnostic, StringComparison.Ordinal);
		}

		Assert.False(await service.ReflectPendingAsync());
		Assert.Equal(3, adapter.Calls);
		Assert.Equal("0", _memory.Store.GetEngineState("reflection_cursor") ?? "0");
		Assert.Equal("3", _memory.Store.GetEngineState("reflection_failure_count"));
		Assert.Equal(firstAssistant.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
			_memory.Store.GetEngineState("reflection_failure_window"));

		_chat.SaveMessage("user", "第二轮用户消息");
		_chat.SaveMessage("assistant", "第二轮助手消息");
		await Assert.ThrowsAsync<ReflectionParseException>(() => service.ReflectPendingAsync());
		Assert.False(await service.ReflectPendingAsync());
		Assert.Equal(4, adapter.Calls);

		_chat.SaveMessage("user", "第三轮用户消息");
		ChatMessage recoveredAssistant = _chat.SaveMessage("assistant", "第三轮助手消息");
		Assert.True(await service.ReflectPendingAsync());
		Assert.Equal(5, adapter.Calls);
		Assert.Equal(recoveredAssistant.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
			_memory.Store.GetEngineState("reflection_cursor"));
		Assert.Equal("0", _memory.Store.GetEngineState("reflection_failure_count"));
		Assert.Equal("", _memory.Store.GetEngineState("reflection_failure_cursor"));
		Assert.Equal("", _memory.Store.GetEngineState("reflection_last_failure_category"));
	}

	[Fact]
	public async Task 六十四条窗口失败后新消息仍能触发恢复且不跳过旧聊天()
	{
		SequenceAdapter adapter = new(call => call <= 3
			? "{\"shouldStore\":"
			: """{"shouldStore":false,"summary":"","personaSummary":"","topics":[],"importance":0.2,"keyFacts":[]}""");
		ReflectionService service = new(_http, _chat, _memory, _config, (_, _) => adapter);
		ChatMessage? firstWindowLastAssistant = null;
		for (int round = 0; round < 32; round++)
		{
			_chat.SaveMessage("user", $"用户消息 {round}");
			firstWindowLastAssistant = _chat.SaveMessage("assistant", $"助手消息 {round}");
		}

		for (int attempt = 0; attempt < 3; attempt++)
		{
			await Assert.ThrowsAsync<ReflectionParseException>(() => service.ReflectPendingAsync());
		}
		Assert.False(await service.ReflectPendingAsync());
		_chat.SaveMessage("user", "窗口外的新用户消息");
		ChatMessage newestAssistant = _chat.SaveMessage("assistant", "窗口外的新助手消息");

		Assert.True(await service.ReflectPendingAsync());
		Assert.Equal(4, adapter.Calls);
		Assert.NotNull(firstWindowLastAssistant);
		Assert.Equal(firstWindowLastAssistant.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
			_memory.Store.GetEngineState("reflection_cursor"));
		Assert.NotEqual(newestAssistant.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
			_memory.Store.GetEngineState("reflection_cursor"));
	}

	public void Dispose()
	{
		_memory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_http.Dispose();
		_database.Dispose();
		try { Directory.Delete(_root, recursive: true); }
		catch (IOException) { }
		GC.SuppressFinalize(this);
	}

	private sealed class SequenceAdapter(Func<int, string> response) : ILlmAdapter
	{
		public int Calls { get; private set; }

		public Task<string> CompleteAsync(string baseUrl, string apiKey, string model, string systemPrompt,
			IReadOnlyList<ChatMessageInput> messages, CancellationToken cancellationToken = default)
		{
			Calls++;
			return Task.FromResult(response(Calls));
		}

		public Task<string> StreamAsync(string baseUrl, string apiKey, string model, string systemPrompt,
			IReadOnlyList<ChatMessageInput> messages, Action<string> onChunk, Action<LlmUsageInfo>? onUsage = null,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class NoEmbedding : IEmbeddingAdapter
	{
		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input,
			int? dimensions = null, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<float>());

		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model,
			IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => Array.Empty<float>()).ToArray());
	}
}
