using System.Text.Json;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Live2D;

namespace Nori.Core.Agent;

/// <summary>发送给伴侣互动 AI 的最小上下文。</summary>
public sealed record PetInteractionReactionRequest
{
	public required string ModelId { get; init; }
	public required string RegionId { get; init; }
	public required string RegionName { get; init; }
	public double ModelX { get; init; }
	public double ModelY { get; init; }
	public double RegionX { get; init; }
	public double RegionY { get; init; }
	public string? CurrentEmotion { get; init; }
	public IReadOnlyList<MotionGroupInfo> AvailableMotions { get; init; } = [];
	public IReadOnlyList<string> AvailableExpressions { get; init; } = [];
}

/// <summary>
/// 伴侣互动的独立 LLM 请求。
/// 不加载聊天历史、工具、技能或长期记忆，也不会写入数据库。
/// </summary>
public sealed class PetInteractionReactionService
{
	public const int TimeoutSeconds = 6;

	private const string SystemPrompt = """
		你是 Nori 桌面伴侣的轻量互动反应器。用户刚刚触碰了 Nori 的一个自定义交互区域。
		只根据本次交互上下文决定一个简短、符合 Nori 性格的动作反应。
		严格只输出一个 JSON 对象，不要 Markdown、解释、代码块或工具调用。
		JSON 字段只能使用 text、emotion、expression、action。
		text 是可选的中文短句，最多 120 个字符；不需要说话时返回空字符串。
		emotion 是可选的短情绪名称。
		expression 必须从 availableExpressions 中选择；没有合适的表情时返回空字符串。
		action 必须从 availableMotions 中的 name 中选择；没有合适的动作时返回空字符串。
		不要捏造列表之外的动作或表情，不要执行任何工具，不要请求更多信息。
		""";

	private readonly HttpClient _httpClient;
	private readonly ConfigStore _config;
	private readonly Func<LlmProvider, HttpClient, ILlmAdapter> _adapterFactory;

	public PetInteractionReactionService(
		HttpClient httpClient,
		ConfigStore config,
		Func<LlmProvider, HttpClient, ILlmAdapter>? adapterFactory = null)
	{
		_httpClient = httpClient;
		_config = config;
		_adapterFactory = adapterFactory ?? LlmClient.CreateAdapter;
	}

	/// <summary>发起一次不写聊天历史的伴侣互动请求。</summary>
	public async Task<PetInteractionReaction> ReactAsync(
		PetInteractionReactionRequest request,
		CancellationToken cancellationToken = default)
	{
		ValidateRequest(request);
		AiChatSettings chatSettings = new AiSettingsStore(_config).Read().Chat;
		string providerText = chatSettings.Provider.AsString();
		string baseUrl = chatSettings.BaseUrl;
		string apiKey = chatSettings.ApiKey;
		string model = chatSettings.Model;
		if (baseUrl.Length == 0 || apiKey.Length == 0 || model.Length == 0)
		{
			throw new InvalidOperationException("伴侣互动缺少完整的 LLM 配置");
		}

		LlmProvider provider = LlmProviderExtensions.ParseProvider(providerText);
		ILlmAdapter adapter = _adapterFactory(provider, _httpClient);
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

		string prompt = BuildUserPrompt(request);
		IReadOnlyList<ChatMessageInput> messages =
		[
			new ChatMessageInput {Role = "user", Content = prompt},
		];
		string raw = await adapter.CompleteAsync(
			baseUrl.TrimEnd('/'), apiKey, model, SystemPrompt, messages, timeout.Token).ConfigureAwait(false);
		PetInteractionReaction reaction = PetInteractionReactionParser.Parse(raw);
		return NormalizeAvailableChoices(reaction, request);
	}

	/// <summary>生成可审计的最小输入；不包含聊天历史、屏幕信息或秘密。</summary>
	public string BuildUserPrompt(PetInteractionReactionRequest request)
	{
		ValidateRequest(request);
		object payload = new
		{
			gesture = "tap",
			modelId = request.ModelId,
			regionId = request.RegionId,
			regionName = request.RegionName,
			modelPoint = new {x = RoundCoordinate(request.ModelX), y = RoundCoordinate(request.ModelY)},
			regionPoint = new {x = RoundCoordinate(request.RegionX), y = RoundCoordinate(request.RegionY)},
			currentEmotion = Limit(request.CurrentEmotion, 32),
			availableMotions = request.AvailableMotions.Select(group => new
			{
				group = group.Group,
				names = group.Names,
			}).ToArray(),
			availableExpressions = request.AvailableExpressions,
		};
		return JsonSerializer.Serialize(payload, PetInteractionJson.Options);
	}

	private static PetInteractionReaction NormalizeAvailableChoices(
		PetInteractionReaction reaction,
		PetInteractionReactionRequest request)
	{
		string? motion = string.IsNullOrWhiteSpace(reaction.Motion)
			? null
			: PetActionResolver.ResolveMotion(request.AvailableMotions, reaction.Motion);
		string? expression = string.IsNullOrWhiteSpace(reaction.Expression)
			? null
			: PetActionResolver.ResolveExpression(request.AvailableExpressions, reaction.Expression);
		return reaction with {Motion = motion, Expression = expression};
	}

	private static void ValidateRequest(PetInteractionReactionRequest request)
	{
		if (string.IsNullOrWhiteSpace(request.ModelId)) throw new InvalidOperationException("互动模型 ID 不能为空");
		if (string.IsNullOrWhiteSpace(request.RegionId)) throw new InvalidOperationException("互动区域 ID 不能为空");
		if (string.IsNullOrWhiteSpace(request.RegionName)) throw new InvalidOperationException("互动区域名称不能为空");
		if (!double.IsFinite(request.ModelX) || !double.IsFinite(request.ModelY)
			|| !double.IsFinite(request.RegionX) || !double.IsFinite(request.RegionY)
			|| request.ModelX < 0 || request.ModelX > 1 || request.ModelY < 0 || request.ModelY > 1
			|| request.RegionX < 0 || request.RegionX > 1 || request.RegionY < 0 || request.RegionY > 1)
		{
			throw new InvalidOperationException("互动坐标必须是 0 到 1 之间的有限数值");
		}
	}

	private static double RoundCoordinate(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

	private static string? Limit(string? value, int maxLength)
	{
		if (string.IsNullOrWhiteSpace(value)) return null;
		string trimmed = value.Trim();
		return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
	}
}
