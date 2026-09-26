using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Platform.Storage;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Tools;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Bridge;

public sealed partial class BridgeCommands
{
	private object UpdateReminder(JsonElement args)
	{
		string id = Str(args, "id");
		string? content = OptionalReminderPatchString(args, "content", allowNull: false);
		bool? repeatDaily = OptionalReminderBool(args, "repeatDaily");
		string? timezone = OptionalReminderPatchString(args, "timezone", allowNull: false);
		string? recurrenceJson = OptionalReminderPatchString(args, "recurrenceJson", allowNull: true);
		(long? TriggerAt, double? DelayMinutes) time = ReadReminderUpdateTime(args);
		if (content is null && repeatDaily is null && timezone is null && recurrenceJson is null
			&& time.TriggerAt is null && time.DelayMinutes is null)
			throw new InvalidOperationException("提醒更新至少需要一个字段");
		if (time.DelayMinutes is { } delay)
			return Runtime.Proactive.UpdateReminderAfter(id, content, delay, repeatDaily, timezone, recurrenceJson);
		return Runtime.Proactive.UpdateReminder(id, content, time.TriggerAt, repeatDaily, timezone, recurrenceJson);
	}

	private static (long? TriggerAt, double? DelayMinutes) ReadReminderUpdateTime(JsonElement args)
	{
		bool hasDelay = HasProperty(args, "delayMinutes");
		bool hasTriggerTime = HasProperty(args, "triggerTime");
		bool hasTriggerAt = HasProperty(args, "triggerAt");
		if (hasDelay && (hasTriggerTime || hasTriggerAt))
			throw new InvalidOperationException("只能指定一种提醒时间");
		if (hasTriggerTime && hasTriggerAt)
			throw new InvalidOperationException("只能指定一种提醒时间");
		if (hasDelay) return (null, ReadReminderNumber(args, "delayMinutes")!.Value);
		if (hasTriggerTime) return (ReadReminderTimestamp(args, "triggerTime"), null);
		if (hasTriggerAt) return (ReadReminderTimestamp(args, "triggerAt"), null);
		return (null, null);
	}

	private static string? OptionalReminderPatchString(JsonElement args, string name, bool allowNull)
	{
		if (!HasProperty(args, name)) return null;
		JsonElement value = args.GetProperty(name);
		if (value.ValueKind == JsonValueKind.Null && allowNull) return "";
		if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException($"参数 {name} 无效");
		return value.GetString() ?? "";
	}

	private static bool? OptionalReminderBool(JsonElement args, string name)
	{
		if (!HasProperty(args, name)) return null;
		JsonElement value = args.GetProperty(name);
		if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
		throw new InvalidOperationException($"参数 {name} 必须是布尔值");
	}

	private static double? ReadReminderNumber(JsonElement args, string name, bool allowMissing = false)
	{
		if (!HasProperty(args, name))
		{
			if (allowMissing) return null;
			throw new InvalidOperationException($"缺少参数: {name}");
		}
		JsonElement value = args.GetProperty(name);
		if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
			throw new InvalidOperationException($"参数 {name} 必须是有限数字");
		return number;
	}

	private static long ReadReminderTimestamp(JsonElement args, string name)
	{
		if (!HasProperty(args, name)) throw new InvalidOperationException($"缺少参数: {name}");
		JsonElement value = args.GetProperty(name);
		if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long timestamp))
			throw new InvalidOperationException($"参数 {name} 必须是整数时间");
		return timestamp;
	}

	// ===================================================================
	// 配置写入 (内部直写, 不再暴露给前端通用入口)
	// ===================================================================

	/// <summary>
	/// 文件工具的工作目录与工具轮数上限。
	///
	/// 目录在此处即时校验是否存在。写入不存在的路径不会报错，但该组工具会静默不注册，表现为
	/// 配置项有值而工具不可用，排查成本高。空串为合法取值，表示禁用该功能。
	/// </summary>
	/// <summary>
	/// 开关一条情绪表达通道。
	///
	/// 通道键由前端给出而不是在此处枚举：通道清单在 AppRuntime，两处各维护一份必然漂。
	/// 键必须带 expression_ 前缀，挡住拿这条命令去改任意配置项。
	/// </summary>
	private void UpdateExpressionChannel(JsonElement args)
	{
		if (args.ValueKind != JsonValueKind.Object
			|| !args.TryGetProperty("channel", out JsonElement channel)
			|| channel.GetString() is not {Length: > 0} key
			|| !key.StartsWith(ConfigStore.KeyExpressionPrefix, StringComparison.Ordinal))
		{
			throw new InvalidOperationException("channel 必须是情绪表达通道的键");
		}

		UpdateBoolConfig(args, "enabled", key);

		// 关掉会改桌面设置的通道时立刻还原，不等退出 —— 用户关它多半就是想让桌面变回去。
		if (!_services.Config.GetBoolOr(key, false)) Runtime.RestoreDesktopState();
		Runtime.InvalidateSnapshot();
	}

	private void UpdateTaskSettings(JsonElement args)
	{
		if (args.ValueKind != JsonValueKind.Object
			|| !args.TryGetProperty("tasks", out JsonElement list)
			|| list.ValueKind != JsonValueKind.Array)
		{
			throw new InvalidOperationException("tasks 必须是数组");
		}

		List<WorkspaceTask> parsed = [];
		foreach (JsonElement entry in list.EnumerateArray())
		{
			if (entry.ValueKind != JsonValueKind.Object) continue;
			parsed.Add(new WorkspaceTask
			{
				Name = entry.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "",
				Command = entry.TryGetProperty("command", out JsonElement command) ? command.GetString() ?? "" : "",
			});
		}

		// 改配置之前先记下当前的授权面，改完才能算出哪些已经不再需要。
		IReadOnlyList<string> granted = Runtime.CurrentGrantPaths();

		// 严格校验后再落库：非法条目在读取侧是被跳过的，静默丢一条比当场报错难查得多。
		_services.Config.Set(ConfigStore.KeyWorkspaceTasks, WorkspaceTaskList.Write(WorkspaceTaskList.Validate(parsed)));

		Runtime.RebuildTools();
		Runtime.ReleaseStaleGrants(granted);
		Runtime.InvalidateSnapshot();
	}

	/// <summary>
	/// 整份替换 runTask 的任务清单。
	///
	/// 增量更新需要稳定标识，而任务只有名字这一个键，改名就无法与新增区分。整份替换让
	/// 界面持有唯一真相，代价是并发编辑会互相覆盖 —— 设置窗口是单实例，不存在这种情形。
	/// </summary>
	/// <summary>
	/// 换授权档位。
	///
	/// 选到完全放行时**在这里**按下到期时刻，而不是读取时再算 —— 到期时刻要跟着
	/// 「哪一次点的」走。读取时才算的话，改一次别的设置就等于又续了四小时。
	/// </summary>
	private void UpdatePermissionGear(JsonElement args)
	{
		if (args.ValueKind != JsonValueKind.Object
			|| !args.TryGetProperty("gear", out JsonElement value)
			|| value.ValueKind != JsonValueKind.String)
		{
			throw new InvalidOperationException("缺少 gear");
		}

		string raw = (value.GetString() ?? "").Trim();
		PermissionGear gear = ToolPermissionPolicy.Parse(raw);
		// Parse 认不出的值会退到最严的一档，那对配置读取是对的，但在这里会把一次
		// 拼错的调用变成一次静默的"改成逐次确认"。写入侧要严格。
		if (!string.Equals(ToolPermissionPolicy.Format(gear), raw, StringComparison.Ordinal))
		{
			throw new InvalidOperationException($"不认识的档位: {raw}");
		}

		_services.Config.Set(ToolPermissionPolicy.KeyGear, new ConfigValue.Text(ToolPermissionPolicy.Format(gear)));
		_services.Config.Set(
			ToolPermissionPolicy.KeyBypassUntil,
			new ConfigValue.Text(gear == PermissionGear.Bypass
				? ToolPermissionPolicy.FormatDeadline(ToolPermissionPolicy.BypassDeadline(DateTimeOffset.UtcNow))
				: string.Empty));
		Runtime.InvalidateSnapshot();
	}

	private void UpdateWorkspaceSettings(JsonElement args)
	{
		// 同上：授权面要在改配置之前取。
		IReadOnlyList<string> granted = Runtime.CurrentGrantPaths();

		if (args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty("root", out JsonElement rootValue)
			&& rootValue.ValueKind == JsonValueKind.String)
		{
			string root = (rootValue.GetString() ?? "").Trim();
			if (root.Length > 0 && !Directory.Exists(root))
			{
				throw new InvalidOperationException("这个文件夹不存在，换一个吧");
			}

			_services.Config.Set(ConfigStore.KeyWorkspaceRoot, new ConfigValue.Text(root));
		}

		if (args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty("maxToolIterations", out JsonElement iterations)
			&& iterations.ValueKind == JsonValueKind.Number
			&& iterations.TryGetInt32(out int parsed))
		{
			// 存为 Integer 而非 Text：文本路径上 "0"/"1" 会被解析为布尔并渲染为 "false"/"true"。
			_services.Config.Set(
				ConfigStore.KeyAgentMaxToolIterations,
				new ConfigValue.Integer(Math.Clamp(
					parsed,
					Nori.Core.Agent.AgentEngine.MinToolIterations,
					Nori.Core.Agent.AgentEngine.MaxToolIterationsLimit)));
		}

		// 工具注册表依赖工作目录构建，变更后必须重建，否则要到下次启动才生效。
		Runtime.RebuildTools();

		// 沙箱授权是磁盘上的 ACL，换了工作目录不释放的话旧目录上的 ACE 会永久残留。
		Runtime.ReleaseStaleGrants(granted);
		Runtime.InvalidateSnapshot();
	}

	/// <summary>
	/// 选文件夹。返回选中的路径，用户取消时返回 null。
	///
	/// 仅返回选择结果，不写配置：写入仍由 `settings_update_workspace` 承担，配置只有一个写入点。
	/// </summary>
	private async Task<object?> PickWorkspaceAsync(IBridgeSource source)
	{
		RequireMainVoid(source);
		Avalonia.Controls.Window? self = source.Self ?? throw new InvalidOperationException("来源窗口不可用");
		string? picked = await _uiDispatcher.InvokeTaskAsync(async () =>
		{
			IReadOnlyList<IStorageFolder> folders = await self.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
			{
				Title = "选择可访问的文件夹",
				AllowMultiple = false,
			});
			return folders.Count > 0 ? folders[0].Path.LocalPath : null;
		});

		return picked is null ? null : new { root = picked };
	}

	private void UpdateConfigDirect(string key, string value) =>
		_services.Config.Set(key, new ConfigValue.Text(value));

	/// <summary>更新聊天与 Embedding 配置。</summary>
	private void UpdateUnifiedAiSettings(JsonElement args)
	{
		JsonElement chat = args;
		bool hasNestedChat = TryGetObject(args, "chat", out JsonElement nestedChat);
		if (hasNestedChat) chat = nestedChat;
		string? persona = OptionalStr(args, "persona") ?? OptionalStr(chat, "persona");
		bool hasChatPatch = hasNestedChat
			|| HasAnyString(args, "provider", "baseUrl", "apiKey", "model", "persona")
			|| persona is not null;
		if (hasChatPatch)
		{
			_services.AiSettings.UpdateChat(new AiChatSettingsPatch(
				Provider: OptionalStr(chat, "provider"),
				BaseUrl: OptionalStr(chat, "baseUrl"),
				ApiKey: OptionalStr(chat, "apiKey"),
				Model: OptionalStr(chat, "model"),
				Persona: persona,
				ApiKeySpecified: HasString(chat, "apiKey")));
		}

		JsonElement embedding = args;
		bool hasNestedEmbedding = TryGetObject(args, "embedding", out JsonElement nestedEmbedding);
		if (hasNestedEmbedding) embedding = nestedEmbedding;
		bool hasFlatEmbedding = HasAnyString(args, "embeddingBaseUrl", "embeddingApiKey", "embeddingModel", "embeddingDimensions");
		if (hasNestedEmbedding || hasFlatEmbedding)
		{
			_services.AiSettings.UpdateEmbedding(BuildEmbeddingPatch(
				hasNestedEmbedding ? embedding : args,
				hasNestedEmbedding ? null : "embedding"));
			Runtime.QueueEmbeddingRebuild();
		}
	}

	private static bool TryGetObject(JsonElement args, string name, out JsonElement value)
	{
		value = default;
		return args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty(name, out value)
			&& value.ValueKind == JsonValueKind.Object;
	}

	private static AiEmbeddingSettingsPatch BuildEmbeddingPatch(JsonElement args, string? prefix)
	{
		string Name(string name) => prefix is null ? name : prefix + char.ToUpperInvariant(name[0]) + name[1..];
		return new AiEmbeddingSettingsPatch(
			BaseUrl: OptionalStr(args, prefix is null ? "baseUrl" : Name("baseUrl")),
			ApiKey: OptionalStr(args, prefix is null ? "apiKey" : Name("apiKey")),
			Model: OptionalStr(args, prefix is null ? "model" : Name("model")),
			Dimensions: OptionalStr(args, prefix is null ? "dimensions" : Name("dimensions")),
			ApiKeySpecified: HasString(args, prefix is null ? "apiKey" : Name("apiKey")));
	}

	private static bool HasString(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object
		&& args.TryGetProperty(name, out JsonElement value)
		&& value.ValueKind == JsonValueKind.String;

	private static bool HasAnyString(JsonElement args, params string[] names) => names.Any(name => HasString(args, name));

	/// <summary>可选字段更新: 参数缺失时不动配置</summary>
	private void UpdateOptionalConfig(JsonElement args, string argName, string configKey)
	{
		if (args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty(argName, out JsonElement value)
			&& value.ValueKind == JsonValueKind.String
			&& value.GetString() is { } text)
		{
			UpdateConfigDirect(configKey, text);
		}
	}

	/// <summary>
	/// 秘密字段更新: 缺省不变; 显式空串表示清除; 非空则写入 (DPAPI 由 ConfigStore 自动加密)
	/// </summary>
	private void UpdateSecretConfig(JsonElement args, string argName, string configKey)
	{
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(argName, out JsonElement value)) return;
		if (value.ValueKind != JsonValueKind.String) return;
		string? text = value.GetString();
		if (text is {Length: > 0})
		{
			UpdateConfigDirect(configKey, text);
			_services.Logger.Write(LogSource.Backend, "info", $"已更新敏感配置: {configKey}");
		}
		else
		{
			_services.Config.Delete(configKey);
		}
	}

	private void UpdateBoolConfig(JsonElement args, string argName, string configKey)
	{
		if (args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty(argName, out JsonElement value)
			&& value.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			UpdateConfigDirect(configKey, value.GetBoolean() ? "1" : "0");
		}
	}

	/// <summary>保存遥测三态同意; 首次运行的默认开启只在完成向导时确认。</summary>
	private void UpdateTelemetryConsent(IBridgeSource source, JsonElement args)
	{
		bool? enabled = OptionalBool(args, "telemetryEnabled");
		if (enabled is null) return;

		if (source.Label == WindowLabels.FirstRun)
		{
			_services.Config.SetTelemetryConsent(enabled.Value ? TelemetryConsent.Unset : TelemetryConsent.Denied);
		}
		else
		{
			_services.Config.SetTelemetryConsent(enabled.Value ? TelemetryConsent.Granted : TelemetryConsent.Denied);
		}
	}

	private void UpdateNumberConfig(JsonElement args, string argName, string configKey)
	{
		if (args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty(argName, out JsonElement value)
			&& value.ValueKind == JsonValueKind.Number)
		{
			UpdateConfigDirect(configKey, value.GetRawText());
		}
	}

	/// <summary>行为开关写入并热应用到伴侣视窗。</summary>
	private void SetBehaviorKey(JsonElement args, string argName, string configKey)
	{
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(argName, out JsonElement value)) return;
		if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
			throw new InvalidOperationException($"{argName} 必须是布尔值");
		string storage = value.GetBoolean() ? "1" : "0";
		UpdateConfigDirect(configKey, storage);
		ApplyPetConfig(configKey, storage);
	}

	private void ApplyPetConfig(string key, string storage)
	{
		_services.PetRuntime?.ApplyConfig(key, storage);
		if (key == "l2d_click_through") _uiDispatcher.Post(() => _services.Windows.Pet?.ReapplyInputState());
	}

	/// <summary>持久化工具禁用清单</summary>
	private void PersistDisabledTools()
	{
		IReadOnlyList<string> disabled = Runtime.Tools.DisabledNames();
		string json = JsonSerializer.Serialize(disabled);
		JsonNode? node = JsonNode.Parse(json);
		if (node is not null)
		{
			_services.Config.Set("tools_disabled", new ConfigValue.Json(node));
		}
	}
}
