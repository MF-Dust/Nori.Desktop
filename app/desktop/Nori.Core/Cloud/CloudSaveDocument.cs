using System.Text.Json.Serialization;
using Nori.Core.Memory;

namespace Nori.Core.Cloud;

/// <summary>
/// 云存档的线上格式。
///
/// 三块内容各自有既定的边界：
///   config     由 <see cref="CloudSaveScope"/> 的白名单过滤
///   memories   直接嵌套 <c>nori-memory-v1</c>，复用它已有的白名单、限额与去重
///   reminders  按 id 合并；id 本来就是跨机稳定的
///
/// **不含对话原文**，也不含向量。前者是用户选定的范围之外；后者由本机的嵌入模型
/// 算出，换一台机器上的模型或维度不同，搬过去只是一堆用不了的数字，恢复之后本地
/// 会重新算。
/// </summary>
public sealed record CloudSaveDocument
{
	/// <summary>格式标识。解析前先核它，避免把别的 JSON 当存档吞下去。</summary>
	public const string FormatName = "nori-cloud-v1";

	[JsonPropertyName("format")]
	public string? Format { get; init; }

	/// <summary>生成时刻（ISO 8601 UTC）。冲突时给人看，不参与合并判断。</summary>
	[JsonPropertyName("saved_at")]
	public string? SavedAt { get; init; }

	/// <summary>生成这份存档的客户端版本。出问题时用来定位。</summary>
	[JsonPropertyName("app_version")]
	public string? AppVersion { get; init; }

	[JsonPropertyName("config")]
	public IReadOnlyDictionary<string, string> Config { get; init; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	[JsonPropertyName("memories")]
	public MemoryTransferDocument? Memories { get; init; }

	[JsonPropertyName("reminders")]
	public IReadOnlyList<CloudReminder> Reminders { get; init; } = [];
}

/// <summary>
/// 存档里的一条提醒。
///
/// 只带跨设备有意义的字段。领取状态（claimed_at / fired_at / status）属于**本机的
/// 投递进度**：另一台机器上那条提醒有没有弹出来，跟这台机器无关，搬过去会让一条
/// 还没到时间的提醒显示成已经响过。
/// </summary>
public sealed record CloudReminder
{
	[JsonPropertyName("id")]
	public string? Id { get; init; }

	[JsonPropertyName("content")]
	public string? Content { get; init; }

	[JsonPropertyName("trigger_at")]
	public long TriggerAt { get; init; }

	[JsonPropertyName("repeat_daily")]
	public bool RepeatDaily { get; init; }

	[JsonPropertyName("timezone")]
	public string? Timezone { get; init; }

	[JsonPropertyName("recurrence")]
	public string? RecurrenceJson { get; init; }

	[JsonPropertyName("created_at")]
	public string? CreatedAt { get; init; }
}

/// <summary>打包的结果。</summary>
public sealed record CloudSaveBuild
{
	public required CloudSaveDocument Document { get; init; }

	/// <summary>序列化之后的字节数。用于在上传前判断有没有超出服务端的上限。</summary>
	public required int Bytes { get; init; }

	public required int ConfigCount { get; init; }
	public required int MemoryCount { get; init; }
	public required int ReminderCount { get; init; }

	/// <summary>
	/// 打包过程中被跳过的东西及原因。
	///
	/// 「静默少传了一部分」是这类功能最难发现的故障，所以跳过必须留痕并能展示。
	/// </summary>
	public IReadOnlyList<string> Skipped { get; init; } = [];
}

/// <summary>恢复的结果。</summary>
public sealed record CloudRestoreResult
{
	public required bool Succeeded { get; init; }

	/// <summary>失败原因；成功时为空串。要能直接展示。</summary>
	public string Error { get; init; } = "";

	public int ConfigApplied { get; init; }
	public int MemoriesAdded { get; init; }
	public int MemoriesSkipped { get; init; }
	public int RemindersAdded { get; init; }
	public int RemindersUpdated { get; init; }

	/// <summary>被忽略的内容及原因。</summary>
	public IReadOnlyList<string> Skipped { get; init; } = [];
}
