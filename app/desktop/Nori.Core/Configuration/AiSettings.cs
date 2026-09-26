using System.Globalization;
using System.Text.Json.Serialization;
using Nori.Core.Chat;

namespace Nori.Core.Configuration;

/// <summary>
/// 应用 AI 接入配置的后端领域模型。
///
/// 聊天与 Embedding 使用两个完全独立的端点、模型和密钥。Embedding 密钥允许为空,
/// 以支持本地或匿名的 OpenAI 兼容服务; 不得从聊天配置回退任何字段。
/// </summary>
public sealed record AiProviderSettings
{
	public required AiChatSettings Chat { get; init; }
	public required AiEmbeddingSettings Embedding { get; init; }
}

/// <summary>聊天 Provider 的运行时配置; 密钥只在后端调用链中流转。</summary>
public sealed record AiChatSettings
{
	public required LlmProvider Provider { get; init; }
	public required string BaseUrl { get; init; }
	public required string ApiKey { get; init; }
	public required string Model { get; init; }
	public required string Persona { get; init; }

	public bool IsConfigured => BaseUrl.Length > 0 && ApiKey.Length > 0 && Model.Length > 0;
}

/// <summary>Embedding Provider 的运行时配置; 与聊天配置完全隔离。</summary>
public sealed record AiEmbeddingSettings
{
	public required string BaseUrl { get; init; }
	public required string ApiKey { get; init; }
	public required string Model { get; init; }
	public int? Dimensions { get; init; }

	/// <summary>Embedding 密钥可选, 但端点和模型必须明确配置。</summary>
	public bool IsConfigured => BaseUrl.Length > 0 && Model.Length > 0;
}

/// <summary>聊天配置的部分更新。ApiKeySpecified 用于区分缺省和显式清空。</summary>
public sealed record AiChatSettingsPatch(
	string? Provider = null,
	string? BaseUrl = null,
	string? ApiKey = null,
	string? Model = null,
	string? Persona = null,
	bool ApiKeySpecified = false);

/// <summary>Embedding 配置的部分更新。Embedding API Key 允许显式留空。</summary>
public sealed record AiEmbeddingSettingsPatch(
	string? BaseUrl = null,
	string? ApiKey = null,
	string? Model = null,
	string? Dimensions = null,
	bool ApiKeySpecified = false);

/// <summary>
/// AI 配置领域存储。
/// 所有聊天/Embedding 配置读取和更新都经过此类, 以避免消费者自行拼接回退逻辑。
/// </summary>
public sealed class AiSettingsStore(ConfigStore config)
{
	public const string KeyLlmProvider = "llm_provider";
	public const string KeyLlmBaseUrl = "llm_api_base";
	// 配置键名，不包含任何凭据值。
	public const string KeyLlmApiKey = "llm_api_key"; // nosemgrep
	public const string KeyLlmModel = "llm_model";
	public const string KeyUserPersona = "nori_user_persona";
	public const string KeyEmbeddingBaseUrl = "embedding_api_base";
	// 配置键名，不包含任何凭据值。
	public const string KeyEmbeddingApiKey = "embedding_api_key"; // nosemgrep
	public const string KeyEmbeddingModel = "embedding_model";
	public const string KeyEmbeddingDimensions = "embedding_dimensions";
	public const string DefaultEmbeddingModel = "BAAI/bge-m3";
	private static readonly string[] SettingsKeys =
	[
		KeyLlmProvider,
		KeyLlmBaseUrl,
		KeyLlmApiKey,
		KeyLlmModel,
		KeyUserPersona,
		KeyEmbeddingBaseUrl,
		KeyEmbeddingApiKey,
		KeyEmbeddingModel,
		KeyEmbeddingDimensions,
	];

	private readonly ConfigStore _config = config;

	/// <summary>读取完整 AI 配置; Embedding 不读取任何 llm_* 键。</summary>
	public AiProviderSettings Read()
	{
		IReadOnlyDictionary<string, ConfigValue> values = _config.GetMany(SettingsKeys);
		ConfigValue? Find(string key) => values.TryGetValue(key, out ConfigValue? value) ? value : null;
		string GetString(string key, string fallback) => ConfigValue.AsStringOr(Find(key), fallback);

		return new AiProviderSettings
		{
			Chat = new AiChatSettings
			{
				Provider = LlmProviderExtensions.ParseProvider(GetString(KeyLlmProvider, "openai")),
				BaseUrl = GetString(KeyLlmBaseUrl, "").Trim(),
				ApiKey = GetString(KeyLlmApiKey, ""),
				Model = GetString(KeyLlmModel, "").Trim(),
				Persona = GetString(KeyUserPersona, ""),
			},
			Embedding = new AiEmbeddingSettings
			{
				BaseUrl = GetString(KeyEmbeddingBaseUrl, "").Trim(),
				ApiKey = GetString(KeyEmbeddingApiKey, ""),
				Model = GetString(KeyEmbeddingModel, DefaultEmbeddingModel).Trim(),
				Dimensions = ParseDimensions(GetString(KeyEmbeddingDimensions, "")),
			},
		};
	}

	/// <summary>部分更新聊天配置; 敏感字段仍由 ConfigStore 加密。</summary>
	public void UpdateChat(AiChatSettingsPatch patch)
	{
		if (patch.Provider is not null) SetText(KeyLlmProvider, patch.Provider);
		if (patch.BaseUrl is not null) SetText(KeyLlmBaseUrl, patch.BaseUrl);
		if (patch.ApiKeySpecified) SetSecret(KeyLlmApiKey, patch.ApiKey);
		if (patch.Model is not null) SetText(KeyLlmModel, patch.Model);
		if (patch.Persona is not null) SetText(KeyUserPersona, patch.Persona);
	}

	/// <summary>部分更新 Embedding 配置; 不会修改聊天配置。</summary>
	public void UpdateEmbedding(AiEmbeddingSettingsPatch patch)
	{
		if (patch.BaseUrl is not null) SetText(KeyEmbeddingBaseUrl, patch.BaseUrl);
		if (patch.ApiKeySpecified) SetSecret(KeyEmbeddingApiKey, patch.ApiKey);
		if (patch.Model is not null) SetText(KeyEmbeddingModel, patch.Model);
		if (patch.Dimensions is not null) SetText(KeyEmbeddingDimensions, patch.Dimensions);
	}

	private void SetText(string key, string value) => _config.Set(key, new ConfigValue.Text(value));

	private void SetSecret(string key, string? value)
	{
		if (string.IsNullOrEmpty(value)) _config.Delete(key);
		else _config.Set(key, new ConfigValue.Text(value));
	}

	private static int? ParseDimensions(string value) =>
		int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
			? parsed
			: null;
}

/// <summary>聊天配置的脱敏快照 DTO。</summary>
public sealed record AiChatSettingsSnapshot
{
	[JsonPropertyName("configured")]
	public required bool Configured { get; init; }

	[JsonPropertyName("provider")]
	public required string Provider { get; init; }

	[JsonPropertyName("baseUrl")]
	public required string BaseUrl { get; init; }

	[JsonPropertyName("model")]
	public required string Model { get; init; }

	[JsonPropertyName("persona")]
	public required string Persona { get; init; }

	[JsonPropertyName("hasApiKey")]
	public required bool HasApiKey { get; init; }

	public static AiChatSettingsSnapshot From(AiChatSettings settings) => new()
	{
		Configured = settings.IsConfigured,
		Provider = settings.Provider.AsString(),
		BaseUrl = settings.BaseUrl,
		Model = settings.Model,
		Persona = settings.Persona,
		HasApiKey = settings.ApiKey.Length > 0,
	};
}

/// <summary>Embedding 配置的脱敏快照 DTO。</summary>
public sealed record AiEmbeddingSettingsSnapshot
{
	[JsonPropertyName("configured")]
	public required bool Configured { get; init; }

	[JsonPropertyName("model")]
	public required string Model { get; init; }

	[JsonPropertyName("baseUrl")]
	public required string BaseUrl { get; init; }

	[JsonPropertyName("dimensions")]
	public required string Dimensions { get; init; }

	[JsonPropertyName("hasApiKey")]
	public required bool HasApiKey { get; init; }

	public static AiEmbeddingSettingsSnapshot From(AiEmbeddingSettings settings) => new()
	{
		Configured = settings.IsConfigured,
		Model = settings.Model,
		BaseUrl = settings.BaseUrl,
		Dimensions = settings.Dimensions?.ToString(CultureInfo.InvariantCulture) ?? "",
		HasApiKey = settings.ApiKey.Length > 0,
	};
}
