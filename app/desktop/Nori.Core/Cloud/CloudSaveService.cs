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

	/// <summary>
	/// 把一份存档恢复到本机。
	///
	/// 配置覆盖，记忆与提醒合并。合并的理由见类型注释。
	/// </summary>
	public CloudRestoreResult Restore(CloudSaveDocument? document)
	{
		if (document is null || document.Format != CloudSaveDocument.FormatName)
		{
			return new CloudRestoreResult { Succeeded = false, Error = "存档格式不正确，未做任何改动" };
		}

		List<string> skipped = [];

		int applied = 0;
		foreach ((string key, string raw) in document.Config)
		{
			// 存档是从网络上来的，里面的键不能信。范围与敏感判断在这里再走一遍。
			if (!CloudSaveScope.IncludesConfigWithModelSuffix(key) || ConfigStore.IsSensitiveKey(key))
			{
				skipped.Add($"{key}：不在同步范围内，已忽略");
				continue;
			}
			config.Set(key, ConfigValue.FromStorage(raw));
			applied++;
		}

		int added = 0;
		int skippedMemories = 0;
		if (document.Memories is { } incoming && incoming.Memories.Count > 0)
		{
			// 走 MemoryTransferService 自己的预览提交两段式，沿用它的校验与去重。
			MemoryTransferPreview preview = memories.Preview(JsonSerializer.Serialize(incoming, Json));
			if (!preview.IsValid || preview.PreviewToken is null)
			{
				skipped.Add("记忆：存档里的记忆没有通过校验，已整体忽略");
			}
			else
			{
				MemoryTransferCommitResult commit = memories.Commit(preview.PreviewToken);
				if (commit.Succeeded)
				{
					added = commit.AddedCount;
					skippedMemories = commit.SkippedCount;
				}
				else
				{
					skipped.Add("记忆：写入失败，已整体忽略");
				}
			}
		}

		int remindersAdded = 0;
		int remindersUpdated = 0;
		foreach (CloudReminder item in document.Reminders)
		{
			if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Content))
			{
				skipped.Add("提醒：缺少 id 或内容，已忽略一条");
				continue;
			}
			if (reminders.Get(item.Id) is not null)
			{
				// 同一个 id 说明是同一条提醒。以存档为准更新内容与时间，但不动它在
				// 本机的投递状态 —— 那是这台机器自己的进度。
				if (reminders.Update(item.Id, item.Content, item.TriggerAt,
					item.RepeatDaily, item.Timezone, item.RecurrenceJson)) remindersUpdated++;
				continue;
			}
			reminders.AddExact(item.Id, item.Content, item.TriggerAt,
				item.RepeatDaily, item.Timezone ?? "UTC", item.RecurrenceJson, item.CreatedAt);
			remindersAdded++;
		}

		return new CloudRestoreResult
		{
			Succeeded = true,
			ConfigApplied = applied,
			MemoriesAdded = added,
			MemoriesSkipped = skippedMemories,
			RemindersAdded = remindersAdded,
			RemindersUpdated = remindersUpdated,
			Skipped = skipped,
		};
	}

	private static string Describe(Exception error) =>
		error is MemoryTransferException transfer ? transfer.Category.ToString() : error.GetType().Name;
}
