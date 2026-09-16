namespace Nori.Core.FirstRun;

/// <summary>
/// 首次运行那一步收集到的模型服务配置。
///
/// 这一步**可跳过**，所以草稿不当场落盘，由向导在离开该步时统一保存。纯逻辑收在
/// 这里，窗口只管渲染，补丁构造也就单测得了。
///
/// 后端授权面的限制要记着：<c>settings_update_ai_providers</c> 与
/// <c>llm_fetch_models</c> 允许首次运行窗口调用，而 <c>ai_test_connection</c> 只允许
/// 主窗口。所以这一步用「获取模型」验证地址与密钥 —— 拉得到列表就说明这套凭据是通的。
/// </summary>
public sealed record AiDraft
{
	/// <summary>对话协议。与设置页那份清单保持一致。</summary>
	public string Provider { get; init; } = AiDraftDefaults.OpenAi;

	/// <summary>留空即用该协议的默认地址，与输入框的占位文字一致。</summary>
	public string BaseUrl { get; init; } = "";

	public string ApiKey { get; init; } = "";

	public string Model { get; init; } = "";
}

/// <summary>协议清单、默认地址，以及「这份草稿要不要保存」的判定。</summary>
public static class AiDraftDefaults
{
	public const string OpenAi = "openai";
	public const string OpenAiResponses = "openai_responses";
	public const string Anthropic = "anthropic";
	public const string Google = "google";

	/// <summary>协议顺序。下拉框按它排。</summary>
	public static IReadOnlyList<string> Providers => [OpenAi, OpenAiResponses, Anthropic, Google];

	/// <summary>各协议的默认 API 地址。</summary>
	public static string DefaultBaseUrl(string provider) => provider switch
	{
		OpenAiResponses => "https://api.openai.com/v1",
		Anthropic => "https://api.anthropic.com/v1",
		Google => "https://generativelanguage.googleapis.com/v1beta",
		_ => "https://api.openai.com/v1",
	};

	/// <summary>各协议密钥的样子。只做占位提示，不做校验。</summary>
	public static string ApiKeyHint(string provider) => provider switch
	{
		Anthropic => "sk-ant-...",
		Google => "AIza...",
		_ => "sk-...",
	};

	/// <summary>生效的 API 地址：填了用填的，没填用该协议的默认值。</summary>
	public static string EffectiveBaseUrl(AiDraft draft) =>
		draft.BaseUrl.Trim() is {Length: > 0} typed ? typed : DefaultBaseUrl(draft.Provider);

	/// <summary>
	/// 填了东西没有。
	///
	/// **只选了协议不算** —— 没有密钥也没有模型的协议选择存下去没有意义，
	/// 也不该让「下一步」白打一次后端。
	/// </summary>
	public static bool IsFilled(AiDraft draft) =>
		draft.ApiKey.Trim().Length > 0 || draft.Model.Trim().Length > 0 || draft.BaseUrl.Trim().Length > 0;

	/// <summary>
	/// 构造要保存的补丁；返回 null 表示这一步不用保存。
	///
	/// 填了内容时**一并写入生效地址**：这一步验证过的就是那个地址，不写下去会出现
	/// 「向导里能拉到模型，进主界面却连不上」。
	/// </summary>
	public static AiChatPatch? BuildPatch(AiDraft draft)
	{
		if (!IsFilled(draft)) return null;
		string apiKey = draft.ApiKey.Trim();
		string model = draft.Model.Trim();
		return new AiChatPatch
		{
			Provider = draft.Provider,
			BaseUrl = EffectiveBaseUrl(draft),
			ApiKey = apiKey.Length > 0 ? apiKey : null,
			Model = model.Length > 0 ? model : null,
		};
	}
}

/// <summary>要写进配置的那几项。null 表示这一项不动。</summary>
public sealed record AiChatPatch
{
	public required string Provider { get; init; }
	public required string BaseUrl { get; init; }
	public string? ApiKey { get; init; }
	public string? Model { get; init; }
}
