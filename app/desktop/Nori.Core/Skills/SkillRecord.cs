using System.Text.Json.Serialization;

namespace Nori.Core.Skills;

/// <summary>
/// 技能数据模型 (Nori Skill Definition)
///
/// 字段名与前端 runtime SkillDto 完全一致 (camelCase JSON)。
/// </summary>
public sealed record SkillRecord
{
	/// <summary>技能唯一 ID (如 "code-reviewer")</summary>
	[JsonPropertyName("id")]
	public string Id { get; set; } = "";

	/// <summary>技能显示名称</summary>
	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	/// <summary>技能简要描述</summary>
	[JsonPropertyName("description")]
	public string Description { get; set; } = "";

	/// <summary>作者名称</summary>
	[JsonPropertyName("author")]
	public string Author { get; set; } = "";

	/// <summary>语义化版本号</summary>
	[JsonPropertyName("version")]
	public string Version { get; set; } = "1.0.0";

	/// <summary>显示图标</summary>
	[JsonPropertyName("icon")]
	public string Icon { get; set; } = "sparkles";

	/// <summary>分类标签列表</summary>
	[JsonPropertyName("tags")]
	public IReadOnlyList<string> Tags { get; set; } = [];

	/// <summary>所属主分类</summary>
	[JsonPropertyName("category")]
	public string Category { get; set; } = "productivity";

	/// <summary>注入 Agent System Prompt 的行为指引 / 技能指令</summary>
	[JsonPropertyName("instructions")]
	public string Instructions { get; set; } = "";

	/// <summary>该技能依赖或推荐启用的工具名称列表</summary>
	[JsonPropertyName("tools")]
	public IReadOnlyList<string>? Tools { get; set; }

	/// <summary>是否已启用</summary>
	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; }

	/// <summary>技能来源: builtin / market / custom / url</summary>
	[JsonPropertyName("source")]
	public string Source { get; set; } = "custom";

	/// <summary>安装时间戳 (毫秒)</summary>
	[JsonPropertyName("installedAt")]
	public long InstalledAt { get; set; }

	/// <summary>远程来源 URL (若从网络安装)</summary>
	[JsonPropertyName("url")]
	public string? Url { get; set; }
}

/// <summary>本地技能导入预览。不含指令正文。</summary>
public sealed record SkillImportPreview
{
	[JsonPropertyName("token")]
	public required string Token { get; init; }

	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("description")]
	public string Description { get; init; } = "";

	[JsonPropertyName("category")]
	public string Category { get; init; } = "productivity";

	[JsonPropertyName("tools")]
	public IReadOnlyList<string> Tools { get; init; } = [];
}
