using System.Text.Json;
using Nori.Core.Agent;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Security;

namespace Nori.Core.Tests;

/// <summary>
/// 回复文本 → 表情动作。
///
/// 两个方向都要钉住：名单外的名字必须被夹掉（目录过滤不是边界）；挑不出来时必须退化成
/// 空反应而不是抛（挑表情失败不该让整轮对话失败）。
/// </summary>
public sealed class ReplyReactionServiceTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-reply-reaction-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	private sealed class FixedKeyStore : ISecretKeyStore
	{
		private readonly byte[] _key = Enumerable.Range(0, SecretKeyStore.KeySize).Select(index => (byte)index).ToArray();

		public byte[] LoadOrCreate() => _key;

		public bool IsFileFallback => true;
	}

	public ReplyReactionServiceTests()
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

	private void ConfigureLocalLlm()
	{
		_config.Set(AiSettingsStore.KeyLlmBaseUrl, new ConfigValue.Text("https://llm.example/v1"));
		_config.Set(AiSettingsStore.KeyLlmApiKey, new ConfigValue.Text("sk-local"));
		_config.Set(AiSettingsStore.KeyLlmModel, new ConfigValue.Text("m"));
	}

	/// <summary>固定回一段 JSON 的适配器；同时记下收到的用户提示，供断言最小输入用。</summary>
	private sealed class ScriptedAdapter(string reply, List<string> prompts) : ILlmAdapter
	{
		public Task<string> CompleteAsync(
			string baseUrl, string apiKey, string model, string systemPrompt,
			IReadOnlyList<ChatMessageInput> messages, CancellationToken cancellationToken = default)
		{
			prompts.Add(messages[^1].Content);
			return Task.FromResult(reply);
		}

		public Task<string> StreamAsync(
			string baseUrl, string apiKey, string model, string systemPrompt,
			IReadOnlyList<ChatMessageInput> messages, Action<string> onChunk,
			Action<LlmUsageInfo>? onUsage = null, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("表情挑选只走非流式");

		public Task<IReadOnlyList<string>> FetchModelsAsync(
			string baseUrl, string apiKey, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}

	private ReplyReactionService Build(string reply, List<string>? prompts = null) =>
		new(new HttpClient(), _config, (_, _) => new ScriptedAdapter(reply, prompts ?? []));

	private static ReplyReactionRequest Request(string text = "今天真不错") => new()
	{
		ReplyText = text,
		AvailableMotions = ["Tap", "Idle"],
		AvailableExpressions = ["smile", "sad"],
	};

	[Fact]
	public async Task 名单内的选择被采纳()
	{
		ConfigureLocalLlm();
		ReplyReactionService service = Build("{\"emotion\":\"happy\",\"expression\":\"smile\",\"action\":\"Tap\"}");

		PetInteractionReaction reaction = await service.ReactAsync(Request(), CancellationToken.None);

		Assert.Equal("happy", reaction.Emotion);
		Assert.Equal("smile", reaction.Expression);
		Assert.Equal("Tap", reaction.Motion);
	}

	/// <summary>提示词里写了「不要捏造」不等于它不会捏造。</summary>
	[Fact]
	public async Task 名单外的名字被夹掉()
	{
		ConfigureLocalLlm();
		ReplyReactionService service = Build("{\"emotion\":\"happy\",\"expression\":\"wink\",\"action\":\"Dance\"}");

		PetInteractionReaction reaction = await service.ReactAsync(Request(), CancellationToken.None);

		Assert.Equal("happy", reaction.Emotion);
		Assert.Null(reaction.Expression);
		Assert.Null(reaction.Motion);
	}

	[Fact]
	public async Task 语气平淡时全空也是合法结果()
	{
		ConfigureLocalLlm();
		ReplyReactionService service = Build("{\"emotion\":\"\",\"expression\":\"\",\"action\":\"\"}");

		PetInteractionReaction reaction = await service.ReactAsync(Request(), CancellationToken.None);

		Assert.Null(reaction.Emotion);
		Assert.Null(reaction.Expression);
		Assert.Null(reaction.Motion);
	}

	/// <summary>只配了 LuoLiCore 的人本来就没有本机三项，不该因此整轮失败。</summary>
	[Fact]
	public async Task 没有本机LLM配置时退化成空反应而不是抛()
	{
		ReplyReactionService service = Build("{\"expression\":\"smile\"}");

		PetInteractionReaction reaction = await service.ReactAsync(Request(), CancellationToken.None);

		Assert.Null(reaction.Expression);
	}

	[Fact]
	public async Task 上游故障退化成空反应()
	{
		ConfigureLocalLlm();
		ReplyReactionService service = new(
			new HttpClient(), _config, (_, _) => throw new ChatException("上游挂了"));

		PetInteractionReaction reaction = await service.ReactAsync(Request(), CancellationToken.None);

		Assert.Null(reaction.Expression);
		Assert.Null(reaction.Motion);
	}

	[Fact]
	public async Task 返回的不是json也退化成空反应()
	{
		ConfigureLocalLlm();
		ReplyReactionService service = Build("我觉得应该是 smile 吧");

		PetInteractionReaction reaction = await service.ReactAsync(Request(), CancellationToken.None);

		Assert.Null(reaction.Expression);
	}

	[Fact]
	public async Task 候选名单为空时不发请求()
	{
		ConfigureLocalLlm();
		List<string> prompts = [];
		ReplyReactionService service = Build("{\"expression\":\"smile\"}", prompts);

		await service.ReactAsync(
			new ReplyReactionRequest { ReplyText = "在的" },
			CancellationToken.None);

		Assert.Empty(prompts);
	}

	[Fact]
	public async Task 回复为空时不发请求()
	{
		ConfigureLocalLlm();
		List<string> prompts = [];
		ReplyReactionService service = Build("{\"expression\":\"smile\"}", prompts);

		await service.ReactAsync(Request("   "), CancellationToken.None);

		Assert.Empty(prompts);
	}

	/// <summary>输入是可审计的最小集合：只有这句回复和两张候选名单，没有历史也没有秘密。</summary>
	[Fact]
	public void 提示词不带历史与秘密()
	{
		JsonElement prompt = JsonDocument.Parse(ReplyReactionService.BuildUserPrompt(Request("今天真不错"))).RootElement;

		Assert.Equal("今天真不错", prompt.GetProperty("reply").GetString());
		Assert.Equal(new[] { "smile", "sad" }, prompt.GetProperty("availableExpressions").EnumerateArray().Select(item => item.GetString()!));
		// 字段就这三个（currentEmotion 为空时不序列化）：多一个都意味着有东西被顺带送了出去。
		Assert.Equal(
			new[] { "availableExpressions", "availableMotions", "reply" },
			prompt.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
	}

	[Fact]
	public void 过长的回复被截断()
	{
		JsonElement prompt = JsonDocument.Parse(ReplyReactionService.BuildUserPrompt(Request(new string('字', 900)))).RootElement;

		Assert.Equal(400, prompt.GetProperty("reply").GetString()!.Length);
	}
}
