using System.Text.Json;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Live2D;

namespace Nori.Core.Agent;

/// <summary>选表情动作所需的最小上下文。</summary>
public sealed record ReplyReactionRequest
{
	/// <summary>她这一轮说的话。</summary>
	public required string ReplyText { get; init; }

	public string? CurrentEmotion { get; init; }

	public IReadOnlyList<string> AvailableMotions { get; init; } = [];

	public IReadOnlyList<string> AvailableExpressions { get; init; } = [];
}

/// <summary>
/// 根据一段回复文本挑一个表情与动作。
///
/// 为什么需要它：对话交给 LuoLiCore 之后，回来的是纯文本。合法的动作与表情名随当前
/// Live2D 模型变化，只有宿主知道；而 LuoLiCore 的内核把用户正文里的指令当作数据处理
/// （提示注入防护），把可用动作列表塞进消息里既不会被采纳，也违背那条防护的用意。
/// 所以「她说什么」在远端决定，「她怎么表现」留在本地。
///
/// 形状照 <see cref="PetInteractionReactionService"/>：同样不带聊天历史、不带工具、
/// 不写库、短超时，解析也复用同一个解析器。区别只在上下文是一段回复而不是一次触摸。
///
/// **拿不到本机 LLM 配置时返回空反应，而不是抛。** 只配了 LuoLiCore 的人本来就没有那三项，
/// 让桌宠因为「挑不出表情」而整轮失败是本末倒置 —— 退化成没有表情，对话照常。
/// </summary>
public sealed class ReplyReactionService(
	HttpClient httpClient,
	ConfigStore config,
	Func<LlmProvider, HttpClient, ILlmAdapter>? adapterFactory = null)
{
	/// <summary>比触摸那条更短：它排在回复之后，等它就是让她把已经想好的话憋着。</summary>
	public const int TimeoutSeconds = 5;

	private const string SystemPrompt = """
		你是 Nori 桌面伴侣的表情选择器。下面给你她刚说出口的一句话。
		只根据这句话的语气决定她此刻的表情与动作。
		严格只输出一个 JSON 对象，不要 Markdown、解释、代码块或工具调用。
		JSON 字段只能使用 emotion、expression、action，不要输出 text。
		emotion 是可选的短情绪名称。
		expression 必须从 availableExpressions 中选择；没有合适的就返回空字符串。
		action 必须从 availableMotions 中选择；没有合适的就返回空字符串。
		不要捏造列表之外的动作或表情。语气平淡时全部返回空字符串，不要硬凑。
		""";

	private readonly HttpClient _httpClient = httpClient;
	private readonly ConfigStore _config = config;
	private readonly Func<LlmProvider, HttpClient, ILlmAdapter> _adapterFactory = adapterFactory ?? LlmClient.CreateAdapter;

	/// <summary>挑一个。任何失败都退化成空反应，绝不让它影响这一轮对话。</summary>
	public async Task<PetInteractionReaction> ReactAsync(
		ReplyReactionRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (string.IsNullOrWhiteSpace(request.ReplyText)) return new PetInteractionReaction();
		if (request.AvailableMotions.Count == 0 && request.AvailableExpressions.Count == 0)
		{
			// 一个都没有就没什么可挑的，省掉这次调用。
			return new PetInteractionReaction();
		}

		AiChatSettings chat = new AiSettingsStore(_config).Read().Chat;
		if (!chat.IsConfigured) return new PetInteractionReaction();

		try
		{
			ILlmAdapter adapter = _adapterFactory(chat.Provider, _httpClient);
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

			string raw = await adapter.CompleteAsync(
				chat.BaseUrl.TrimEnd('/'),
				chat.ApiKey,
				chat.Model,
				SystemPrompt,
				[new ChatMessageInput { Role = "user", Content = BuildUserPrompt(request) }],
				timeout.Token).ConfigureAwait(false);

			return Clamp(PetInteractionReactionParser.Parse(raw), request);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// 整轮被取消要如实传播，不能吞。
			throw;
		}
		catch (Exception)
		{
			// 超时、上游故障、返回的不是 JSON —— 都只是这一次没挑出表情。
			return new PetInteractionReaction();
		}
	}

	/// <summary>可审计的最小输入：只有这一句回复和两张候选名单，没有历史、屏幕或秘密。</summary>
	public static string BuildUserPrompt(ReplyReactionRequest request)
	{
		object payload = new
		{
			reply = Limit(request.ReplyText, 400),
			currentEmotion = Limit(request.CurrentEmotion, PetInteractionReactionParser.MaxEmotionLength),
			availableMotions = request.AvailableMotions,
			availableExpressions = request.AvailableExpressions,
		};
		return JsonSerializer.Serialize(payload, PetInteractionJson.Options);
	}

	/// <summary>
	/// 把模型挑的名字夹回候选名单。
	///
	/// 目录过滤不是边界：提示词里写了「不要捏造」不等于它不会捏造，一个名单外的动作名
	/// 传下去只会在渲染层被静默忽略，表现成「有时候有动作有时候没有」，很难查。
	/// </summary>
	private static PetInteractionReaction Clamp(PetInteractionReaction reaction, ReplyReactionRequest request) => new()
	{
		Text = null,
		Emotion = Blank(reaction.Emotion) ? null : reaction.Emotion,
		Expression = request.AvailableExpressions.Contains(reaction.Expression ?? "", StringComparer.Ordinal)
			? reaction.Expression
			: null,
		Motion = request.AvailableMotions.Contains(reaction.Motion ?? "", StringComparer.Ordinal)
			? reaction.Motion
			: null,
	};

	private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

	private static string? Limit(string? value, int max) =>
		value is null ? null : value.Length <= max ? value : value[..max];
}
