namespace Nori.Core.Chat;

/// <summary>
/// LLM 协议类型
/// </summary>
public enum LlmProvider
{
	/// <summary>OpenAI Chat Completions 协议 (默认)</summary>
	OpenAi,

	/// <summary>OpenAI Responses 协议</summary>
	OpenAiResponses,

	/// <summary>Anthropic Messages 协议</summary>
	Anthropic,

	/// <summary>Google GenAI (Gemini) 协议</summary>
	Google,
}

/// <summary>
/// LLM 协议类型扩展方法
/// </summary>
public static class LlmProviderExtensions
{
	/// <summary>
	/// 解析协议类型字符串
	/// </summary>
	public static LlmProvider ParseProvider(string? value)
	{
		if (IsCustomProvider(value)) throw new ChatException("插件聊天提供商必须通过实例注册表解析");
		if (string.IsNullOrWhiteSpace(value)) return LlmProvider.OpenAi;

		return value.Trim().ToLowerInvariant() switch
		{
			"openai_responses" or "responses" => LlmProvider.OpenAiResponses,
			"anthropic" or "claude" => LlmProvider.Anthropic,
			"google" or "gemini" or "google_genai" or "googlegenai" => LlmProvider.Google,
			_ => LlmProvider.OpenAi,
		};
	}

	/// <summary>识别插件前缀，避免未知或已撤销的插件 ID 被当成 OpenAI。</summary>
	public static bool IsCustomProvider(string? value) =>
		value?.Trim().StartsWith("plugin:", StringComparison.OrdinalIgnoreCase) == true;

	/// <summary>
	/// 转换为标准配置字符串
	/// </summary>
	public static string AsString(this LlmProvider provider) => provider switch
	{
		LlmProvider.OpenAi => "openai",
		LlmProvider.OpenAiResponses => "openai_responses",
		LlmProvider.Anthropic => "anthropic",
		LlmProvider.Google => "google",
		_ => "openai",
	};

	/// <summary>
	/// 获取默认 Base URL
	/// </summary>
	public static string DefaultBaseUrl(this LlmProvider provider) => provider switch
	{
		LlmProvider.OpenAi => "https://api.openai.com/v1",
		LlmProvider.OpenAiResponses => "https://api.openai.com/v1",
		LlmProvider.Anthropic => "https://api.anthropic.com/v1",
		LlmProvider.Google => "https://generativelanguage.googleapis.com/v1beta",
		_ => "https://api.openai.com/v1",
	};
}
