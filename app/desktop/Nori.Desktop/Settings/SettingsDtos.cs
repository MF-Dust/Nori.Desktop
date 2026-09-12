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

/// <summary>原生设置窗口使用的提醒创建参数。</summary>
public sealed record SettingsReminderAddDto
{
	[JsonPropertyName("content")]
	public required string Content { get; init; }

	[JsonPropertyName("delayMinutes")]
	public double? DelayMinutes { get; init; }
}

/// <summary>原生设置窗口使用的提醒更新参数。</summary>
public sealed record SettingsReminderUpdateDto
{
	[JsonPropertyName("id")]
	public required string Id { get; init; }

	[JsonPropertyName("content")]
	public string? Content { get; init; }

	[JsonPropertyName("triggerTime")]
	public string? TriggerTime { get; init; }

	[JsonPropertyName("delayMinutes")]
	public double? DelayMinutes { get; init; }

	[JsonPropertyName("repeatDaily")]
	public bool? RepeatDaily { get; init; }

	[JsonPropertyName("timezone")]
	public string? Timezone { get; init; }

	[JsonPropertyName("recurrenceJson")]
	public string? RecurrenceJson { get; init; }
}

/// <summary>原生设置窗口使用的提醒延后参数。</summary>
public sealed record SettingsReminderSnoozeDto
{
	[JsonPropertyName("id")]
	public required string Id { get; init; }

	[JsonPropertyName("delayMinutes")]
	public double? DelayMinutes { get; init; }

	[JsonPropertyName("snoozedUntil")]
	public string? SnoozedUntil { get; init; }
}

/// <summary>原生设置窗口使用的 MCP 服务器参数。</summary>
public sealed record SettingsMcpServerPatchDto
{
	[JsonPropertyName("id")]
	public required string Id { get; init; }

	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("transport")]
	public required string Transport { get; init; }

	[JsonPropertyName("command")]
	public string? Command { get; init; }

	[JsonPropertyName("args")]
	public IReadOnlyList<string>? Args { get; init; }

	[JsonPropertyName("env")]
	public IReadOnlyDictionary<string, string>? Environment { get; init; }

	[JsonPropertyName("url")]
	public string? Url { get; init; }

	[JsonPropertyName("enabled")]
	public bool? Enabled { get; init; }

	[JsonPropertyName("autoConnect")]
	public bool? AutoConnect { get; init; }
}

/// <summary>原生设置窗口使用的标识参数。</summary>
public sealed record SettingsIdDto
{
	[JsonPropertyName("id")]
	public required string Id { get; init; }
}

/// <summary>原生设置窗口使用的插件卸载参数。</summary>
public sealed record SettingsPluginUninstallDto
{
	[JsonPropertyName("id")]
	public required string Id { get; init; }

	[JsonPropertyName("deleteData")]
	public bool DeleteData { get; init; }
}

/// <summary>原生设置窗口使用的技能开关参数。</summary>
public sealed record SettingsSkillToggleDto
{
	[JsonPropertyName("id")]
	public required string Id { get; init; }

	[JsonPropertyName("enabled")]
	public bool Enabled { get; init; }
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
