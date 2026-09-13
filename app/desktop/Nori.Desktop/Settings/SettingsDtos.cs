using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nori.Desktop.Settings;

/// <summary>原生设置窗口使用的 AI 对话配置补丁。</summary>
public sealed record SettingsAiChatPatchDto
{
	[JsonPropertyName("provider")]
	public string? Provider { get; init; }

	[JsonPropertyName("baseUrl")]
	public string? BaseUrl { get; init; }

	[JsonPropertyName("apiKey")]
	public string? ApiKey { get; init; }

	[JsonPropertyName("model")]
	public string? Model { get; init; }

	[JsonPropertyName("persona")]
	public string? Persona { get; init; }
}

/// <summary>原生设置窗口使用的 Embedding 配置补丁。</summary>
public sealed record SettingsAiEmbeddingPatchDto
{
	[JsonPropertyName("baseUrl")]
	public string? BaseUrl { get; init; }

	[JsonPropertyName("apiKey")]
	public string? ApiKey { get; init; }

	[JsonPropertyName("model")]
	public string? Model { get; init; }

	[JsonPropertyName("dimensions")]
	public string? Dimensions { get; init; }
}

/// <summary>原生设置窗口使用的统一 AI 配置补丁。</summary>
public sealed record SettingsAiPatchDto
{
	[JsonPropertyName("chat")]
	public SettingsAiChatPatchDto? Chat { get; init; }

	[JsonPropertyName("embedding")]
	public SettingsAiEmbeddingPatchDto? Embedding { get; init; }

	[JsonPropertyName("persona")]
	public string? Persona { get; init; }
}

/// <summary>原生设置窗口使用的语音配置补丁。</summary>
public sealed record SettingsVoicePatchDto
{
	[JsonPropertyName("volume")]
	public string? Volume { get; init; }

	[JsonPropertyName("ttsProvider")]
	public string? TtsProvider { get; init; }

	[JsonPropertyName("ttsBaseUrl")]
	public string? TtsBaseUrl { get; init; }

	[JsonPropertyName("ttsModel")]
	public string? TtsModel { get; init; }

	[JsonPropertyName("ttsApiKey")]
	public string? TtsApiKey { get; init; }

	[JsonPropertyName("ttsVoice")]
	public string? TtsVoice { get; init; }

	[JsonPropertyName("ttsSpeed")]
	public string? TtsSpeed { get; init; }

	[JsonPropertyName("ttsAutoPlay")]
	public bool? TtsAutoPlay { get; init; }

	[JsonPropertyName("gptsovitsBaseUrl")]
	public string? GptsovitsBaseUrl { get; init; }

	[JsonPropertyName("gptsovitsRefAudio")]
	public string? GptsovitsRefAudio { get; init; }

	[JsonPropertyName("gptsovitsPromptText")]
	public string? GptsovitsPromptText { get; init; }

	[JsonPropertyName("gptsovitsPromptLang")]
	public string? GptsovitsPromptLang { get; init; }

	[JsonPropertyName("indexttsTemplateAudio")]
	public string? IndexttsTemplateAudio { get; init; }

	[JsonPropertyName("indexttsEmoAlpha")]
	public string? IndexttsEmoAlpha { get; init; }

	[JsonPropertyName("sttProvider")]
	public string? SttProvider { get; init; }

	[JsonPropertyName("sttBaseUrl")]
	public string? SttBaseUrl { get; init; }

	[JsonPropertyName("sttApiKey")]
	public string? SttApiKey { get; init; }
}

/// <summary>原生设置窗口使用的通用配置补丁。</summary>
public sealed record SettingsGeneralPatchDto
{
	[JsonPropertyName("language")]
	public string? Language { get; init; }

	[JsonPropertyName("petAutoSummon")]
	public bool? PetAutoSummon { get; init; }

	[JsonPropertyName("sidebarCollapsed")]
	public bool? SidebarCollapsed { get; init; }

	[JsonPropertyName("autoCheckUpdates")]
	public bool? AutoCheckUpdates { get; init; }

	[JsonPropertyName("telemetryEnabled")]
	public bool? TelemetryEnabled { get; init; }
}

/// <summary>原生设置窗口使用的主动行为配置补丁。</summary>
public sealed record SettingsProactivePatchDto
{
	[JsonPropertyName("idleEnabled")]
	public bool? IdleEnabled { get; init; }

	[JsonPropertyName("idleMinutes")]
	public double? IdleMinutes { get; init; }

	[JsonPropertyName("dailyGreeting")]
	public bool? DailyGreeting { get; init; }
}

/// <summary>原生设置窗口使用的自动化配置补丁。</summary>
public sealed record SettingsAutomationPatchDto
{
	[JsonPropertyName("enabled")]
	public bool? Enabled { get; init; }

	[JsonPropertyName("desktopEnabled")]
	public bool? DesktopEnabled { get; init; }

	[JsonPropertyName("browserEnabled")]
	public bool? BrowserEnabled { get; init; }
}

/// <summary>原生设置窗口使用的进程内插件风险确认状态。</summary>
public sealed record SettingsPluginTrustDto
{
	[JsonPropertyName("confirmed")]
	public bool Confirmed { get; init; }
}

/// <summary>原生设置快照的稳定外壳；未知领域字段保留在 Additional 中。</summary>
public sealed record SettingsSnapshotDto
{
	[JsonPropertyName("version")]
	public int Version { get; init; }

	[JsonPropertyName("app")]
	public JsonElement App { get; init; }

	[JsonPropertyName("general")]
	public JsonElement General { get; init; }

	[JsonPropertyName("ai")]
	public JsonElement Ai { get; init; }

	[JsonPropertyName("voice")]
	public JsonElement Voice { get; init; }

	[JsonPropertyName("proactive")]
	public JsonElement Proactive { get; init; }

	[JsonPropertyName("automation")]
	public JsonElement Automation { get; init; }

	[JsonPropertyName("telemetry")]
	public JsonElement Telemetry { get; init; }

	[JsonExtensionData]
	public Dictionary<string, JsonElement>? Additional { get; init; }
}
