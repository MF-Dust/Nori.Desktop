using System.Text.Json.Serialization;

namespace Nori.Core.Configuration;

/// <summary>
/// 首次初始化配置快照
///
/// 字段用 camelCase 序列化, 与前端 services/initConfig.ts 的 InitConfig 接口保持一致
/// </summary>
public sealed record InitConfig
{
	/// <summary>配置结构版本</summary>
	[JsonPropertyName("configSchemaVersion")]
	public required long ConfigSchemaVersion { get; init; }

	/// <summary>应用版本 (首次安装时的版本)</summary>
	[JsonPropertyName("appVersion")]
	public required string AppVersion { get; init; }

	/// <summary>首次启动时间</summary>
	[JsonPropertyName("installedAt")]
	public required string InstalledAt { get; init; }

	/// <summary>首次初始化完成时间, 未完成时为 null</summary>
	[JsonPropertyName("initializedAt")]
	public required string? InitializedAt { get; init; }

	/// <summary>界面语言</summary>
	[JsonPropertyName("language")]
	public required string Language { get; init; }

	/// <summary>伴侣模型</summary>
	[JsonPropertyName("selectedModel")]
	public required string SelectedModel { get; init; }
}
