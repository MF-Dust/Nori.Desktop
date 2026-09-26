using System.Text.Json;
using Nori.Core.Agent;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Live2D;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

public sealed class PetInteractionReactionServiceTests : IDisposable
{
	private sealed class StubAdapter(string response) : ILlmAdapter
	{
		public string? SystemPrompt { get; private set; }
		public IReadOnlyList<ChatMessageInput>? Messages { get; private set; }

		public Task<string> CompleteAsync(string baseUrl, string apiKey, string model, string systemPrompt, IReadOnlyList<ChatMessageInput> messages, CancellationToken cancellationToken = default)
		{
			SystemPrompt = systemPrompt;
			Messages = messages;
			return Task.FromResult(response);
		}

		public Task<string> StreamAsync(string baseUrl, string apiKey, string model, string systemPrompt, IReadOnlyList<ChatMessageInput> messages, Action<string> onChunk, Action<LlmUsageInfo>? onUsage = null, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}

	private readonly TempDatabase _tempDatabase = new("nori-pet-reaction");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public PetInteractionReactionServiceTests()
	{
		_database = NoriDatabase.Open(_tempDatabase.Path);
		_config = new ConfigStore(_database, new FixedKeyStore());
		_config.InitDefaults("0.1.0");
		_config.Set("llm_provider", new ConfigValue.Text("openai"));
		_config.Set("llm_api_base", new ConfigValue.Text("https://example.invalid/v1"));
		_config.Set("llm_api_key", new ConfigValue.Text("secret"));
		_config.Set("llm_model", new ConfigValue.Text("test-model"));
		_config.Set("nori_user_persona", new ConfigValue.Text("温柔但喜欢吐槽"));
	}

	public void Dispose()
	{
		_database.Dispose();
		_tempDatabase.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task ReactsWithoutChatHistoryAndNormalizesAvailableNames()
	{
		StubAdapter adapter = new("{\"text\":\"呀\",\"expression\":\"happy\",\"action\":\"nod\"}");
		PetInteractionReactionService service = new(new HttpClient(), _config, (_, _) => adapter);
		PetInteractionReactionRequest request = Request();

		PetInteractionReaction result = await service.ReactAsync(request);

		Assert.Equal("呀", result.Text);
		Assert.Equal("13_Happy", result.Expression);
		Assert.Equal("02_Nod", result.Motion);
		Assert.NotNull(adapter.Messages);
		Assert.Single(adapter.Messages!);
		Assert.DoesNotContain("chat", adapter.Messages[0].Content, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("screen", adapter.Messages[0].Content, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("secret", adapter.Messages[0].Content, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("温柔但喜欢吐槽", adapter.Messages[0].Content, StringComparison.Ordinal);
	}

	[Fact]
	public void PromptContainsOnlyNormalizedInteractionContext()
	{
		PetInteractionReactionService service = new(new HttpClient(), _config, (_, _) => throw new InvalidOperationException());
		string prompt = service.BuildUserPrompt(Request());
		using JsonDocument document = JsonDocument.Parse(prompt);
		JsonElement root = document.RootElement;

		Assert.Equal(0.123, root.GetProperty("modelPoint").GetProperty("x").GetDouble(), 3);
		Assert.Equal(0.988, root.GetProperty("regionPoint").GetProperty("y").GetDouble(), 3);
		Assert.False(root.TryGetProperty("persona", out _));
		Assert.False(root.TryGetProperty("screen", out _));
		Assert.False(root.TryGetProperty("history", out _));
	}

	private static PetInteractionReactionRequest Request() => new()
	{
		ModelId = "nori",
		RegionId = "head",
		RegionName = "头部",
		ModelX = 0.1234,
		ModelY = 0.4567,
		RegionX = 0.2222,
		RegionY = 0.9876,
		CurrentEmotion = "happy",
		AvailableMotions = [new MotionGroupInfo {Group = "Reactions", Names = ["02_Nod"]}],
		AvailableExpressions = ["13_Happy"],
	};
}
