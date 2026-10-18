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

	/// <summary>图像生成配置 (原始值，未做回退)；生效配置见 <see cref="AiSettingsStore.ResolveImage"/>。</summary>
	public AiImageSettings Image { get; init; } = AiImageSettings.Empty;
}

/// <summary>聊天 Provider 的运行时配置; 密钥只在后端调用链中流转。</summary>
public sealed record AiChatSettings
{
	private string? _providerId;

	public required LlmProvider Provider { get; init; }

	/// <summary>保留原始提供商 ID；旧调用方只设置枚举时仍返回内置协议名。</summary>
	public string ProviderId
	{
		get => _providerId ?? Provider.AsString();
		init => _providerId = value;
	}
	public required string BaseUrl { get; init; }
	public required string ApiKey { get; init; }
	public required string Model { get; init; }
	public required string Persona { get; init; }

	/// <summary>思考档位；默认不发送思考参数。</summary>
	public LlmReasoningLevel Reasoning { get; init; } = LlmReasoningLevel.Auto;

	/// <summary>插件的地址和密钥要求由实例注册表验证；此处只判断模型是否已选择。</summary>
	public bool IsConfigured => Model.Length > 0
		&& (LlmProviderExtensions.IsCustomProvider(ProviderId) || (BaseUrl.Length > 0 && ApiKey.Length > 0));
}

/// <summary>
/// 图像生成服务配置。
///
/// 地址或密钥留空时，若对话服务使用同一家协议 (OpenAI 兼容 / Google)，生效配置沿用对话服务的
/// 地址与密钥 —— 绝大多数用户两者是同一个账号。协议不同则不回退，避免把密钥发给另一家服务。
/// </summary>
public sealed record AiImageSettings
{
	/// <summary>未配置。</summary>
	public static AiImageSettings Empty { get; } = new() {Provider = "openai", BaseUrl = "", ApiKey = "", Model = "", Size = "auto"};

	/// <summary>协议：openai (Images API) 或 google (Gemini generateContent)。</summary>
	public required string Provider { get; init; }
	public required string BaseUrl { get; init; }
	public required string ApiKey { get; init; }
	public required string Model { get; init; }

	/// <summary>默认尺寸或比例 (auto / 1024x1024 / 16:9 等)。</summary>
	public required string Size { get; init; }

	/// <summary>地址、密钥与模型都已具备。</summary>
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
	bool ApiKeySpecified = false,
	string? Reasoning = null);

/// <summary>图像生成配置的部分更新。</summary>
public sealed record AiImageSettingsPatch(
	string? Provider = null,
	string? BaseUrl = null,
	string? ApiKey = null,
	string? Model = null,
	string? Size = null,
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
	public const string KeyLlmReasoning = "llm_reasoning_effort";
	public const string KeyImageProvider = "image_provider";
	public const string KeyImageBaseUrl = "image_api_base";
	// 配置键名，不包含任何凭据值。
	public const string KeyImageApiKey = "image_api_key"; // nosemgrep
	public const string KeyImageModel = "image_model";
	public const string KeyImageSize = "image_size";
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
		KeyLlmReasoning,
		KeyImageProvider,
		KeyImageBaseUrl,
		KeyImageApiKey,
		KeyImageModel,
		KeyImageSize,
	];

	private readonly ConfigStore _config = config;

	/// <summary>读取完整 AI 配置; Embedding 不读取任何 llm_* 键。</summary>
	public AiProviderSettings Read()
	{
		IReadOnlyDictionary<string, ConfigValue> values = _config.GetMany(SettingsKeys);
		ConfigValue? Find(string key) => values.TryGetValue(key, out ConfigValue? value) ? value : null;
		string GetString(string key, string fallback) => ConfigValue.AsStringOr(Find(key), fallback);
		string providerId = GetString(KeyLlmProvider, "openai");

		return new AiProviderSettings
		{
			Chat = new AiChatSettings
			{
				Provider = LlmProviderExtensions.IsCustomProvider(providerId) ? LlmProvider.OpenAi : LlmProviderExtensions.ParseProvider(providerId),
				ProviderId = providerId,
				BaseUrl = GetString(KeyLlmBaseUrl, "").Trim(),
				ApiKey = GetString(KeyLlmApiKey, ""),
				Model = GetString(KeyLlmModel, "").Trim(),
				Persona = GetString(KeyUserPersona, ""),
				Reasoning = LlmReasoningLevels.Parse(GetString(KeyLlmReasoning, "auto")),
			},
			Embedding = new AiEmbeddingSettings
			{
				BaseUrl = GetString(KeyEmbeddingBaseUrl, "").Trim(),
				ApiKey = GetString(KeyEmbeddingApiKey, ""),
				Model = GetString(KeyEmbeddingModel, DefaultEmbeddingModel).Trim(),
				Dimensions = ParseDimensions(GetString(KeyEmbeddingDimensions, "")),
			},
			Image = new AiImageSettings
			{
				Provider = NormalizeImageProvider(GetString(KeyImageProvider, "openai")),
				BaseUrl = GetString(KeyImageBaseUrl, "").Trim(),
				ApiKey = GetString(KeyImageApiKey, ""),
				Model = GetString(KeyImageModel, "").Trim(),
				Size = GetString(KeyImageSize, "auto").Trim() is {Length: > 0} size ? size : "auto",
			},
		};
	}

	/// <summary>
	/// 计算图像生成的生效配置：地址/密钥留空且对话服务是同一协议时沿用对话服务的值。
	/// </summary>
	public static AiImageSettings ResolveImage(AiProviderSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		AiImageSettings image = settings.Image;
		if (LlmProviderExtensions.IsCustomProvider(settings.Chat.ProviderId)) return image;
		bool sameFamily = image.Provider == "google"
			? settings.Chat.Provider == LlmProvider.Google
			: settings.Chat.Provider is LlmProvider.OpenAi or LlmProvider.OpenAiResponses;
		if (!sameFamily) return image;
		return image with
		{
			BaseUrl = image.BaseUrl.Length > 0 ? image.BaseUrl : settings.Chat.BaseUrl,
			ApiKey = image.ApiKey.Length > 0 ? image.ApiKey : settings.Chat.ApiKey,
		};
	}

	/// <summary>规范化图像协议；未知取值按 openai 处理。</summary>
	public static string NormalizeImageProvider(string? value) =>
		value?.Trim().ToLowerInvariant() is "google" or "gemini" ? "google" : "openai";

	/// <summary>部分更新聊天配置; 敏感字段仍由 ConfigStore 加密。</summary>
	public void UpdateChat(AiChatSettingsPatch patch)
	{
		if (patch.Provider is not null) SetText(KeyLlmProvider, patch.Provider);
		if (patch.BaseUrl is not null) SetText(KeyLlmBaseUrl, patch.BaseUrl);
		if (patch.ApiKeySpecified) SetSecret(KeyLlmApiKey, patch.ApiKey);
		if (patch.Model is not null) SetText(KeyLlmModel, patch.Model);
		if (patch.Persona is not null) SetText(KeyUserPersona, patch.Persona);
		if (patch.Reasoning is not null) SetText(KeyLlmReasoning, LlmReasoningLevels.Parse(patch.Reasoning).AsString());
	}

	/// <summary>部分更新图像生成配置。</summary>
	public void UpdateImage(AiImageSettingsPatch patch)
	{
		if (patch.Provider is not null) SetText(KeyImageProvider, NormalizeImageProvider(patch.Provider));
		if (patch.BaseUrl is not null) SetText(KeyImageBaseUrl, patch.BaseUrl.Trim());
		if (patch.ApiKeySpecified) SetSecret(KeyImageApiKey, patch.ApiKey);
		if (patch.Model is not null) SetText(KeyImageModel, patch.Model.Trim());
		if (patch.Size is not null) SetText(KeyImageSize, patch.Size.Trim().Length == 0 ? "auto" : patch.Size.Trim());
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

	[JsonPropertyName("reasoning")]
	public string Reasoning { get; init; } = "auto";

	public static AiChatSettingsSnapshot From(AiChatSettings settings) => new()
	{
		Reasoning = settings.Reasoning.AsString(),
		Configured = settings.IsConfigured,
		Provider = LlmProviderExtensions.IsCustomProvider(settings.ProviderId) ? settings.ProviderId : settings.Provider.AsString(),
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

/// <summary>图像生成配置的脱敏快照 DTO。</summary>
public sealed record AiImageSettingsSnapshot
{
	[JsonPropertyName("configured")]
	public required bool Configured { get; init; }

	[JsonPropertyName("provider")]
	public required string Provider { get; init; }

	[JsonPropertyName("baseUrl")]
	public required string BaseUrl { get; init; }

	[JsonPropertyName("model")]
	public required string Model { get; init; }

	[JsonPropertyName("size")]
	public required string Size { get; init; }

	[JsonPropertyName("hasApiKey")]
	public required bool HasApiKey { get; init; }

	/// <summary>raw 为用户填写值；configured 按生效配置 (含对话服务回退) 判断。</summary>
	public static AiImageSettingsSnapshot From(AiImageSettings raw, AiImageSettings effective) => new()
	{
		Configured = effective.IsConfigured,
		Provider = raw.Provider,
		BaseUrl = raw.BaseUrl,
		Model = raw.Model,
		Size = raw.Size,
		HasApiKey = raw.ApiKey.Length > 0,
	};
}
