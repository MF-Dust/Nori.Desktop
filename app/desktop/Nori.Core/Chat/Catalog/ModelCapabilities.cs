namespace Nori.Core.Chat.Catalog;

/// <summary>
/// 单个模型的能力描述 (来源 models.dev)。
///
/// 只保留 Nori 用得到的字段：上下文窗口、输出上限、思考方式与多模态能力。数值为 0 表示目录未提供。
/// </summary>
public sealed record ModelCapabilities
{
	/// <summary>models.dev 中的服务商标识 (如 openai、deepseek)。</summary>
	public required string ProviderId { get; init; }

	/// <summary>models.dev 中的模型标识。</summary>
	public required string ModelId { get; init; }

	/// <summary>模型展示名。</summary>
	public string Name { get; init; } = "";

	/// <summary>总上下文窗口 (token)。</summary>
	public int ContextWindow { get; init; }

	/// <summary>单独的输入上限 (token)；部分模型的输入上限小于总窗口。</summary>
	public int InputLimit { get; init; }

	/// <summary>单次输出上限 (token)。</summary>
	public int OutputLimit { get; init; }

	/// <summary>是否为思考 (推理) 模型。</summary>
	public bool Reasoning { get; init; }

	/// <summary>支持的思考强度取值 (none / minimal / low / medium / high / xhigh / max 的子集，按强度升序)。</summary>
	public IReadOnlyList<string> ReasoningEfforts { get; init; } = [];

	/// <summary>是否支持开关思考。</summary>
	public bool ReasoningToggle { get; init; }

	/// <summary>是否支持思考 token 预算。</summary>
	public bool ReasoningBudget { get; init; }

	/// <summary>思考预算下限；null 表示未知。</summary>
	public int? ReasoningBudgetMin { get; init; }

	/// <summary>思考预算上限；null 表示未知。</summary>
	public int? ReasoningBudgetMax { get; init; }

	/// <summary>是否支持函数工具调用。</summary>
	public bool ToolCall { get; init; }

	/// <summary>是否接受图片输入。</summary>
	public bool ImageInput { get; init; }

	/// <summary>是否能输出图片。</summary>
	public bool ImageOutput { get; init; }

	/// <summary>多轮工具调用时需要回传的思考字段 (如 reasoning_content)；null 表示不需要。</summary>
	public string? InterleavedField { get; init; }

	/// <summary>可用于输入的窗口：有独立输入上限时取它，否则取总窗口。</summary>
	public int EffectiveInputWindow => InputLimit > 0 ? InputLimit : ContextWindow;
}

/// <summary>按协议、地址与模型名查询模型能力。</summary>
public interface IModelCapabilityProvider
{
	/// <summary>查询模型能力；目录不可用或找不到模型时返回 null，不抛异常。</summary>
	Task<ModelCapabilities?> FindAsync(LlmProvider protocol, string? baseUrl, string model, CancellationToken cancellationToken = default);
}
