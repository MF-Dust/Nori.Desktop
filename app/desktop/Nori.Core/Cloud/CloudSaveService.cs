using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nori.Core.Configuration;
using Nori.Core.Memory;
using Nori.Core.Proactive;

namespace Nori.Core.Cloud;

/// <summary>
/// 云存档的打包与恢复。
///
/// ── 分工 ──────────────────────────────────────────────────────────────────
/// 这一层只负责「本机数据 ↔ 一份文档」的转换，不认识网络。上传、下载、冲突提示
/// 都在调用方。这样整条路径在没有服务端的情况下可测。
///
/// 记忆那一块**不自己实现**：<see cref="MemoryTransferService"/> 已经有白名单、
/// 限额、去重与预览提交两段式，云存档直接嵌套它的文档。另起一套等于把那些边界
/// 重写一遍，而其中任何一条写漏都不会报错。
///
/// ── 恢复的语义：合并，不删除 ───────────────────────────────────────────────
/// 恢复不会删掉本机有、存档里没有的记忆或提醒。存档是某一时刻的快照，用它去删
/// 本机数据意味着「在 A 机删一条」会变成「在 B 机也删」，而用户并没有表达这个
/// 意图，且不可撤销。
///
/// 代价是删除不跨设备传播：在一台机器上删掉的记忆，另一台恢复之后仍在。这是
/// 明确的取舍，不是遗漏。
///
/// 配置是例外：范围内的键**直接覆盖**。它们是偏好，本来就该以最后一次保存为准，
/// 而且数量有限、可见、随时能改回来。
/// </summary>
public sealed class CloudSaveService(
	ConfigStore config,
	MemoryTransferService memories,
	ReminderStore reminders,
	TimeProvider? timeProvider = null)
{
	/// <summary>单份存档的上限。超过就不上传 —— 服务端也会拦，但本机先拦能给出更清楚的原因。</summary>
	public const int MaxBytes = 4 * 1024 * 1024;

	private static readonly JsonSerializerOptions Json = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = false,
	};

	private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

	/// <summary>
	/// 把本机数据打包成一份存档。
	///
	/// <paramref name="appVersion"/> 写进文档，出问题时用来定位是哪个版本产出的。
	/// </summary>
	public CloudSaveBuild Build(string appVersion = "")
	{
		List<string> skipped = [];

		Dictionary<string, string> scoped = new(StringComparer.Ordinal);
		foreach ((string key, ConfigValue value) in config.GetAll())
		{
			if (!CloudSaveScope.IncludesConfigWithModelSuffix(key)) continue;
			/*
			 * 白名单之外还要再挡一道敏感键。
			 *
			 * 两者本该不相交（白名单里一个密钥都没有，CloudSaveScopeTests 有一条专门
			 * 扫这件事），但这里是数据真正离开本机的最后一个点：多一道判断的代价是
			 * 一次字符串比较，漏一个的代价是把密钥发上云。
			 */
			if (ConfigStore.IsSensitiveKey(key))
			{
				skipped.Add($"{key}：敏感字段，不上传");
				continue;
			}
			scoped[key] = value.ToStorage();
		}

		MemoryTransferDocument? memoryDocument = null;
		int memoryCount = 0;
		try
		{
			MemoryTransferExport export = memories.ExportResult();
			memoryDocument = export.Document;
			memoryCount = export.Document.Memories.Count;
		}
		catch (Exception error)
		{
			// 记忆导不出来不该让整份存档失败：偏好与提醒仍然值得存。
			skipped.Add($"记忆：导出失败（{Describe(error)}）");
		}

		List<CloudReminder> packed = [];
		foreach (ReminderItem item in reminders.List())
		{
			// 终态的提醒不带走：它们在本机已经结束，搬到另一台机器上只是历史噪声。
			if (item.Status is "completed" or "cancelled")
			{
				continue;
			}
			packed.Add(new CloudReminder
			{
				Id = item.Id,
				Content = item.Content,
				TriggerAt = item.TriggerAt,
				RepeatDaily = item.RepeatDaily,
				Timezone = item.Timezone,
				RecurrenceJson = item.RecurrenceJson,
				CreatedAt = item.CreatedAt,
			});
		}

		CloudSaveDocument document = new()
		{
			Format = CloudSaveDocument.FormatName,
			SavedAt = _time.GetUtcNow().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
			AppVersion = string.IsNullOrWhiteSpace(appVersion) ? null : appVersion,
			Config = scoped,
			Memories = memoryDocument,
			Reminders = packed,
		};

		return new CloudSaveBuild
		{
			Document = document,
			Bytes = Encoding.UTF8.GetByteCount(Serialize(document)),
			ConfigCount = scoped.Count,
			MemoryCount = memoryCount,
			ReminderCount = packed.Count,
			Skipped = skipped,
		};
	}

	/// <summary>把一份存档序列化成要上传的字节。</summary>
	public string Serialize(CloudSaveDocument document) => JsonSerializer.Serialize(document, Json);

	/// <summary>
	/// 解析一份存档。
	///
	/// 先核 format：把别的 JSON 当存档吞下去会在恢复时产生一份空的覆盖，而那是
	/// 不可撤销的。
	/// </summary>
	public static CloudSaveDocument? Parse(string? content)
	{
		if (string.IsNullOrWhiteSpace(content)) return null;
		CloudSaveDocument? document;
		try
		{
			document = JsonSerializer.Deserialize<CloudSaveDocument>(content, Json);
		}
		catch (JsonException)
		{
			return null;
		}
		return document?.Format == CloudSaveDocument.FormatName ? document : null;
	}

	/// <summary>文本类配置的长度上限（UTF-8 字节）。偏好值都很短，超长只可能是被改过的存档。</summary>
	private const int MaxConfigValueBytes = 1024;

	/// <summary><c>nori_skills</c> 存的是一份 JSON 清单，单独放宽。</summary>
	private const int MaxSkillsConfigBytes = 64 * 1024;

	private const long DayMs = 24L * 60 * 60 * 1000;

	/// <summary>期望是布尔的配置基名。恢复到一台全新机器时本机没有现值可比，所以要有一份自己的表。</summary>
	private static readonly HashSet<string> BooleanKeys = new(StringComparer.Ordinal)
	{
		"tts_auto_play", "ui_sidebar_collapsed",
		"l2d_shadow", "l2d_click_through", "l2d_ai_interaction_enabled",
		"proactive_daily_greeting", "proactive_idle_enabled",
		"memory_enabled", "memory_decay_enabled", "memory_archive_enabled", "memory_reflection_enabled",
	};

	/// <summary>期望是有限数值的配置基名。取值范围由各读取点自己夹取，这里只挡类型不对的值。</summary>
	private static readonly HashSet<string> NumericKeys = new(StringComparer.Ordinal)
	{
		"audio_volume", "l2d_scale", "l2d_opacity", "l2d_render_scale",
		"proactive_idle_minutes",
		"memory_recall_top_k", "memory_min_similarity", "memory_archive_threshold",
		"memory_reflection_rounds", "memory_reflection_min_chars",
		"tts_speed",
	};

	/// <summary>
	/// 把一份存档恢复到本机。
	///
	/// 配置覆盖，记忆与提醒合并。合并的理由见类型注释。
	///
	/// **先校验、后写入。** 配置、记忆预览、提醒都先在内存里过一遍并定下要做什么，
	/// 全部走完才开始写；存档本身残缺（集合为 null）时一个字也不写。各库之间没有
	/// 跨服务事务，所以写入阶段按「最可能失败的先写」排：记忆 → 提醒 → 配置。
	/// </summary>
	public CloudRestoreResult Restore(CloudSaveDocument? document)
	{
		if (document is null || document.Format != CloudSaveDocument.FormatName)
		{
			return new CloudRestoreResult { Succeeded = false, Error = "存档格式不正确，未做任何改动" };
		}
		// 存档来自网络，反序列化可以给出 null 集合；放到写入中途才踩到就会留下半份。
		if (document.Config is null || document.Reminders is null)
		{
			return new CloudRestoreResult { Succeeded = false, Error = "存档内容不完整，未做任何改动" };
		}

		List<string> skipped = [];

		// ── 校验阶段：只读 ──────────────────────────────────────────────────
		List<(string Key, ConfigValue Value)> configPlan = [];
		foreach ((string key, string raw) in document.Config)
		{
			// 存档是从网络上来的，里面的键不能信。范围与敏感判断在这里再走一遍。
			if (!CloudSaveScope.IncludesConfigWithModelSuffix(key) || ConfigStore.IsSensitiveKey(key))
			{
				skipped.Add($"{key}：不在同步范围内，已忽略");
				continue;
			}
			if (!TryValidateConfig(key, raw, out ConfigValue value, out string reason))
			{
				skipped.Add($"{key}：{reason}，已忽略");
				continue;
			}
			configPlan.Add((key, value));
		}

		string? memoryToken = null;
		if (document.Memories is { } incoming && incoming.Memories.Count > 0)
		{
			// 走 MemoryTransferService 自己的预览提交两段式，沿用它的校验与去重。
			// 这里只预览；提交留到写入阶段。
			MemoryTransferPreview preview = memories.Preview(JsonSerializer.Serialize(incoming, Json));
			if (!preview.IsValid || preview.PreviewToken is null)
			{
				return new CloudRestoreResult { Succeeded = false, Error = "存档里的记忆没有通过校验，未做任何改动", Skipped = skipped };
			}
			else
			{
				memoryToken = preview.PreviewToken;
			}
		}

		long nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
		List<(CloudReminder Item, long TriggerAt)> reminderPlan = [];
		foreach (CloudReminder? item in document.Reminders)
		{
			if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Content))
			{
				skipped.Add("提醒：缺少 id 或内容，已忽略一条");
				continue;
			}
			// 本机已有的一律以本机为准，不碰：更新会重置触发时间、清掉推迟与领取状态，
			// 拿一份旧存档去覆盖，会让本机上排在后面的每日提醒立刻响、推迟也白做。
			// 终态（已完成/已取消）的同 id 也算已有，不能被「复活」。
			if (reminders.Get(item.Id) is not null) continue;

			long triggerAt = item.TriggerAt;
			if (triggerAt <= nowMs)
			{
				if (!item.RepeatDaily)
				{
					// 过期的一次性提醒搬到新机器上会在下一次轮询立刻弹出，没有意义。
					skipped.Add("提醒：已过期的一次性提醒，未恢复");
					continue;
				}
				// 每日提醒：库里没有通用的追赶函数（MarkFired 只 +1 天），这里按整天推进到
				// 下一个未来时刻，保留原来的钟点。不去解析 recurrence_json。
				triggerAt += ((nowMs - triggerAt) / DayMs + 1) * DayMs;
			}
			reminderPlan.Add((item, triggerAt));
		}

		// ── 写入阶段 ────────────────────────────────────────────────────────
		int added = 0;
		int skippedMemories = 0;
		if (memoryToken is not null)
		{
			MemoryTransferCommitResult commit = memories.Commit(memoryToken);
			if (commit.Succeeded)
			{
				added = commit.AddedCount;
				skippedMemories = commit.SkippedCount;
			}
			else
			{
				return new CloudRestoreResult { Succeeded = false, Error = "记忆写入失败，未恢复偏好或提醒", Skipped = skipped };
			}
		}

		int remindersAdded = 0;
		foreach ((CloudReminder item, long triggerAt) in reminderPlan)
		{
			if (reminders.AddExact(item.Id!, item.Content!, triggerAt,
				item.RepeatDaily, item.Timezone ?? "UTC", item.RecurrenceJson, item.CreatedAt)) remindersAdded++;
		}

		foreach ((string key, ConfigValue value) in configPlan) config.Set(key, value);

		return new CloudRestoreResult
		{
			Succeeded = true,
			ConfigApplied = configPlan.Count,
			MemoriesAdded = added,
			MemoriesSkipped = skippedMemories,
			RemindersAdded = remindersAdded,
			Skipped = skipped,
		};
	}

	/// <summary>
	/// 存档里一条配置能不能写。
	///
	/// 设置界面写配置时没有集中的校验器：各读取点自己夹取范围（GetClampedInt 等），
	/// 所以这里不重造范围，只做两道保守的闸：
	/// 一是长度（<c>nori_skills</c> 64 KiB，其余 1 KiB）；
	/// 二是类型 —— 布尔键必须解析成布尔，数值键必须是有限的 invariant 小数。全新机器上没有现值，
	/// 所以先查上面两张按键名的表；不在表里的键再看本机现值是布尔/整数就要求同类型。
	/// </summary>
	private bool TryValidateConfig(string key, string? raw, out ConfigValue value, out string reason)
	{
		value = new ConfigValue.Text("");
		reason = "";
		if (raw is null)
		{
			reason = "值为空";
			return false;
		}
		string baseKey = BaseKey(key);
		int limit = baseKey == "nori_skills" ? MaxSkillsConfigBytes : MaxConfigValueBytes;
		if (Encoding.UTF8.GetByteCount(raw) > limit)
		{
			reason = "值过长";
			return false;
		}

		value = ConfigValue.FromStorage(raw);
		if (BooleanKeys.Contains(baseKey))
		{
			if (value is ConfigValue.Boolean) return true;
			reason = "应为布尔值";
			return false;
		}
		if (NumericKeys.Contains(baseKey))
		{
			if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
				&& double.IsFinite(number)) return true;
			reason = "应为数值";
			return false;
		}

		switch (config.Get(key))
		{
			case ConfigValue.Boolean when value is not ConfigValue.Boolean:
				reason = "应为布尔值";
				return false;
			case ConfigValue.Integer when value is not ConfigValue.Integer:
				reason = "应为整数";
				return false;
			default:
				return true;
		}
	}

	/// <summary>去掉按模型分键的后缀（<c>l2d_scale_nori</c> → <c>l2d_scale</c>）。</summary>
	private static string BaseKey(string key)
	{
		if (CloudSaveScope.ConfigKeys.Contains(key)) return key;
		foreach (string prefix in CloudSaveScope.ConfigKeys)
		{
			if (prefix.StartsWith("l2d_", StringComparison.Ordinal)
				&& key.Length > prefix.Length + 1
				&& key.StartsWith(prefix, StringComparison.Ordinal)
				&& key[prefix.Length] == '_') return prefix;
		}
		return key;
	}

	private static string Describe(Exception error) =>
		error is MemoryTransferException transfer ? transfer.Category.ToString() : error.GetType().Name;
}
