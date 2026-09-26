using Nori.Core;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Live2D;
using Nori.Core.Proactive;
using Nori.Core.Sandbox;
using Nori.Core.Security;
using Nori.Core.Tools;
using Nori.Desktop.Bridge;
using Nori.Desktop.Telemetry;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Runtime;

public sealed partial class AppRuntime
{
	// ===================================================================
	// UI 状态快照
	// ===================================================================

	/// <summary>使快照失效并通知已订阅的原生窗口。</summary>
	public void InvalidateSnapshot()
	{
		Interlocked.Increment(ref _snapshotVersion);
		RaiseStateChanged();
	}

	/// <summary>构建脱敏 UI 状态快照; 同一版本直接复用不可变 DTO。</summary>
	public object BuildSnapshot(IBridgeSource source)
	{
		_ = source;
		return BuildSnapshot();
	}

	/// <summary>构建不依赖 WebView 来源的脱敏 UI 状态快照。</summary>
	public object BuildSnapshot()
	{
		while (true)
		{
			int version = SnapshotVersion;
			lock (_snapshotCacheGate)
			{
				if (_cachedSnapshotVersion == version && _cachedSnapshot is not null) return _cachedSnapshot;
			}

			object snapshot = BuildSnapshotCore(version);
			if (SnapshotVersion != version) continue;
			lock (_snapshotCacheGate)
			{
				if (SnapshotVersion != version) continue;
				_cachedSnapshot = snapshot;
				_cachedSnapshotVersion = version;
				return snapshot;
			}
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "状态订阅者和诊断记录必须彼此隔离，单个失败不能阻断其余通知。")]
	private void RaiseStateChanged()
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		Action? handlers = StateChanged;
		if (handlers is null) return;
		foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
		{
			try { handler(); }
			catch (Exception exception)
			{
				try { Services.Logger.Write(LogSource.Backend, "warn", $"原生设置状态通知失败: {SensitiveDataRedactor.ExceptionSummary(exception)}"); }
				catch { }
			}
		}
	}

	private object BuildSnapshotCore(int snapshotVersion)
	{
		ConfigStore config = Services.Config;
		var updateStatus = Services.Update?.CurrentStatus;
		AiProviderSettings aiSettings = Services.AiSettings.Read();
		AiChatSettingsSnapshot chatSnapshot = AiChatSettingsSnapshot.From(aiSettings.Chat);
		bool remoteChat = new Nori.Core.Chat.LuoLiCore.LuoLiCoreSettingsStore(config).Read().IsActive;
		AiEmbeddingSettingsSnapshot embeddingSnapshot = AiEmbeddingSettingsSnapshot.From(aiSettings.Embedding);

		var models = ModelCatalogIds().Select(id => new
		{
			id,
			installed = IsModelInstalled(id),
		}).ToArray();

		string selectedModel = config.GetStringOr("selected_model", ConfigStore.DefaultModel);
		IReadOnlyDictionary<string, ConfigValue> modelDisplay = config.GetMany(Live2DModelConfig.DisplayKeys(selectedModel));
		float modelOpacity = Live2DModelConfig.ReadFloat(modelDisplay, Live2DModelConfig.OpacityKey, selectedModel, 1.0f);
		float modelRenderScale = Live2DModelConfig.ReadFloat(modelDisplay, Live2DModelConfig.RenderScaleKey, selectedModel, 2.0f);
		bool modelShadow = Live2DModelConfig.ReadBool(modelDisplay, Live2DModelConfig.ShadowKey, selectedModel, true);
		string modelQualityMode = Live2DModelConfig.ReadPreferredText(modelDisplay, Live2DModelConfig.QualityModeKey, selectedModel, "adaptive");
		int modelMaxFps = (int)Live2DModelConfig.ReadFloat(modelDisplay, Live2DModelConfig.MaxFpsKey, selectedModel, 0f);
		Live2DRenderSettings modelRenderSettings = Live2DRenderSettings.Normalize(
			selectedModel, modelOpacity, modelShadow, modelRenderScale, modelQualityMode, modelMaxFps);

		Nori.Core.Memory.MemorySettings memorySettings = Memory.Settings;
		(int activeMemories, int atomCount, int archivedMemories, int totalMemories) = Memory.GetOverview();
		Nori.Core.Memory.MemoryIndexStatus memoryIndex = Knowledge.Status;

		return new
		{
			version = snapshotVersion,
			app = new
			{
				appVersion = ProductVersion.Current,
				productVersion = ProductVersion.Current,
				platform = PlatformOsName(),
				debugCrashTestsAvailable = !SentryTelemetry.IsProductionBuild,
				safeMode = Services.SafeMode,
			},
			general = new
			{
				language = config.GetStringOr("language", "zh-CN"),
				petAutoSummon = ParseBoolFlag(config.GetStringOr("pet_auto_summon", "true")) ?? true,
				quickChatEnabled = ParseBoolFlag(config.GetStringOr(ConfigStore.KeyQuickChatEnabled, "true")) ?? true,
				backgroundBlurEnabled = config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, true),
				sidebarCollapsed = ParseBoolFlag(config.GetStringOr("ui_sidebar_collapsed", "")) ?? false,
				autoCheckUpdates = config.GetBoolOr("auto_check_updates", true),
			},
			// 文件工具那一族的配置。`workspaceRoot` 为空即整族不注册，界面据此显示「未启用」。
			workspace = new
			{
				root = config.GetStringOr(ConfigStore.KeyWorkspaceRoot, ""),
				// 目录被删除或移动后配置仍在，但工具已不再注册，界面需要区分这两种状态。
				available = new WorkspaceAccess(config.GetStringOr(ConfigStore.KeyWorkspaceRoot, "")).IsConfigured,
				maxToolIterations = Engine.ConfiguredToolIterations,
				// 待决授权是否也发成系统通知。非 Windows 上界面据此显示「本平台不支持」。
				toastApprovals = config.GetBoolOr(ConfigStore.KeyToastApprovals, true),
				toastSupported = OperatingSystem.IsWindows(),
				tasks = WorkspaceTaskList.Read(config.Get(ConfigStore.KeyWorkspaceTasks))
					.Select(task => new { name = task.Name, command = task.Command }),
				// 界面要能说清「命令跑在什么边界里」：无隔离与 AppContainer 的安全含义完全不同。
				// 启动器尚未建立时报本平台的预期值，不报 unknown —— 用户在配置命令之前就该知道。
				isolation = (ExistingSandbox?.Isolation ?? SandboxLauncherFactory.PlannedIsolation)
					.ToString().ToLowerInvariant(),
				screenEnabled = config.GetBoolOr(ConfigStore.KeyScreenReadingEnabled, false),
				// 平台不支持或模型没配时，界面要说清是「开不了」而不是「没开」。
				screenAvailable = ScreenCapture is {IsAvailable: true} && VisionAnalyzer.IsConfigured,
				/* ── 授权档位 ────────────────────────────────────────────────
				 * gear 是用户存的那个值，effective 是此刻真正生效的 —— 完全放行到期
				 * 之后两者会不一样，界面必须能同时说出「你选的是什么」和「现在按什么走」。
				 * 只报一个的话，用户看到还写着完全放行却仍然被弹框，只能怀疑是坏了。 */
				permissions = new
				{
					gear = ToolPermissionPolicy.Format(StoredGear),
					effective = ToolPermissionPolicy.Format(EffectiveGear),
					bypassUntil = BypassUntil,
					bypassRemainingSeconds = (int?)ToolPermissionPolicy
						.BypassRemaining(StoredGear, BypassUntil, DateTimeOffset.UtcNow)?.TotalSeconds,
					// 安全模式下需要确认的工具一律拒绝，档位说了不算。
					safeMode = Services.SafeMode,
				},
			},
			// 每条通道两项：开没开（用户的选择）与能不能用（环境是否具备）。界面要能说出差别，
			// 否则「开了没反应」无从排查。
			expression = ExpressionChannels.ToDictionary(
				channel => channel.Key,
				channel => (object)new
				{
					enabled = IsExpressionChannelEnabled(channel.Key),
					available = channel.IsAvailable,
					level = channel.Level.ToString().ToLowerInvariant(),
				},
				StringComparer.Ordinal),
			telemetry = new
			{
				consent = ConfigValidation.TelemetryConsentStorage(config.GetTelemetryConsent()),
				enabled = config.GetTelemetryConsent() == TelemetryConsent.Granted,
				available = Services.Telemetry.IsAvailable,
			},
			updater = updateStatus is not null
				? new
				{
					state = updateStatus.State.ToString().ToLowerInvariant(),
					progress = updateStatus.Progress,
					downloadedBytes = updateStatus.DownloadedBytes,
					totalBytes = updateStatus.TotalBytes,
					message = updateStatus.Message,
					currentVersion = updateStatus.CurrentVersion,
					availableVersion = updateStatus.AvailableVersion,
					releaseTag = updateStatus.ReleaseTag,
					releaseNotes = updateStatus.ReleaseNotes,
					lastCheckedAt = updateStatus.LastCheckedAt,
					unavailableReason = updateStatus.UnavailableReason,
					manualDownloadUrl = updateStatus.ManualDownloadUrl,
				}
				: null,
			secretIssues = config.GetSecretIssues().Select(issue => new
			{
				key = issue.Key,
				category = issue.Code,
				requiresUserAction = issue.RequiresUserAction,
			}).ToArray(),
			chat = new
			{
				configured = !Services.SafeMode && (remoteChat || chatSnapshot.Configured),
				backend = remoteChat ? "luolicore" : "local",
			},
			ai = new
			{
				// 保留旧版扁平字段, 同时提供统一的 chat/embedding DTO。
				configured = chatSnapshot.Configured,
				provider = chatSnapshot.Provider,
				baseUrl = chatSnapshot.BaseUrl,
				model = chatSnapshot.Model,
				persona = chatSnapshot.Persona,
				hasApiKey = chatSnapshot.HasApiKey,
				chat = chatSnapshot,
				embedding = embeddingSnapshot,
			},
			models = new
			{
				selected = selectedModel,
				items = models,
				loadError = Services.PetRuntime?.LastModelLoadError,
				scale = (double)Live2DModelConfig.ReadFloat(modelDisplay, Live2DModelConfig.ScaleKey, selectedModel, 1f),
				expressions = ModelExpressions(selectedModel),
			},
			pet = new
			{
				visible = Services.Windows.IsWindowVisible(WindowLabels.Pet),
				renderMetrics = Services.PetRuntime?.RenderMetrics,
			},
			/* ── 各个窗口开着没有 ──────────────────────────────────────────────
			 * 侧边栏那四项点下去是**另开一个窗口**, 不是切页。此前界面拿不到这四个
			 * 状态, 只好统一画成"未选中", 于是点了「对话」之后窗口开在旁边, 侧边栏
			 * 却还是一副什么都没发生的样子。
			 *
			 * 注意 VisibilityChanged 里本来就在调 InvalidateSnapshot() ——
			 * 也就是说刷新这条路早就接好了, 缺的一直是这一段本身, 而缺了也不报错。 */
			windows = new
			{
				chat = Services.Windows.IsWindowVisible(WindowLabels.Chat),
				models = Services.Windows.IsWindowVisible(WindowLabels.Models),
				memory = Services.Windows.IsWindowVisible(WindowLabels.Memory),
				settings = Services.Windows.IsWindowVisible(WindowLabels.Settings),
			},
			platform = new
			{
				os = PlatformOsName(),
				sessionType = Nori.Core.Platform.PlatformServices.Current.Session.ToString().ToLowerInvariant(),
				supportsGlobalCursor = Nori.Core.Platform.PlatformServices.Current.Capabilities.SupportsGlobalCursor,
				supportsWindowDrag = Nori.Core.Platform.PlatformServices.Current.Capabilities.SupportsWindowDrag,
				supportsHitThrough = Nori.Core.Platform.PlatformServices.Current.Capabilities.SupportsHitThrough,
				supportsTopmost = Nori.Core.Platform.PlatformServices.Current.Capabilities.SupportsTopmost,
				supportsTray = Nori.Core.Platform.PlatformServices.Current.Capabilities.SupportsTray && TrayAvailable,
			},
			behaviors = new
			{
				clickInteraction = ParseBoolFlag(config.GetStringOr("l2d_click_interaction", "true")) ?? true,
				clickThrough = ParseBoolFlag(config.GetStringOr("l2d_click_through", "")) ?? false,
				autoBlink = ParseBoolFlag(config.GetStringOr("l2d_auto_blink", "true")) ?? true,
				eyeTracking = ParseBoolFlag(config.GetStringOr("l2d_eye_tracking", "true")) ?? true,
				idleEyeAnimation = ParseBoolFlag(config.GetStringOr("l2d_idle_eye_animation", "true")) ?? true,
				idleAnimation = ParseBoolFlag(config.GetStringOr("l2d_idle_animation", "true")) ?? true,
				expressionEnabled = ParseBoolFlag(config.GetStringOr("l2d_expression_enabled", "true")) ?? true,
				lipSync = ParseBoolFlag(config.GetStringOr("l2d_lip_sync", "true")) ?? true,
				shadow = modelRenderSettings.ShadowEnabled,
				beatSync = ParseBoolFlag(config.GetStringOr("l2d_beat_sync", "")) ?? false,
				aiInteraction = !Services.SafeMode && (ParseBoolFlag(config.GetStringOr(PetInteractionConfig.AiEnabledKey, "")) ?? false),
				opacity = modelRenderSettings.Opacity,
				renderScale = modelRenderSettings.RenderScale,
				qualityMode = Live2DRenderSettings.QualityModeToStorage(modelRenderSettings.QualityMode),
				maxFps = modelRenderSettings.MaxFps,
			},
			memory = new
			{
				enabled = memorySettings.Enabled,
				reflectionEnabled = !Services.SafeMode && memorySettings.ReflectionEnabled,
				decayEnabled = memorySettings.DecayEnabled,
				archiveEnabled = memorySettings.ArchiveEnabled,
				active = activeMemories,
				atoms = atomCount,
				archived = archivedMemories,
				total = totalMemories,
				knowledgePath = Knowledge.Path,
				knowledgeChunks = Knowledge.Status.Total,
				indexState = memoryIndex.State.ToString().ToLowerInvariant(),
				indexProcessed = memoryIndex.Processed,
				indexTotal = memoryIndex.Total,
				lastError = memoryIndex.LastError,
				lastReflection = Memory.Store.GetEngineState("last_reflection_at"),
				lastMaintenance = Memory.Store.GetEngineState("last_maintenance_at"),
				ftsAvailable = Services.Memory.IsFtsAvailable,
				reflectionRounds = memorySettings.ReflectionRounds,
				reflectionMinChars = memorySettings.ReflectionMinChars,
				recallTopK = memorySettings.RecallTopK,
				keywordTopK = memorySettings.KeywordTopK,
				vectorTopK = memorySettings.VectorTopK,
				rrfK = memorySettings.RrfK,
				minSimilarity = memorySettings.MinSimilarity,
				sourceRetentionThreshold = memorySettings.SourceRetentionThreshold,
				archiveThreshold = memorySettings.ArchiveThreshold,
				knowledgeEnabled = memorySettings.KnowledgeEnabled,
				knowledgeWatch = !Services.SafeMode && memorySettings.KnowledgeWatch,
				debugRetrieval = memorySettings.DebugRetrieval,
			},
			voice = new
			{
				volume = Voice.GetVolume(),
				ttsProvider = Voice.ResolveProviderName(),
				ttsBaseUrl = config.GetStringOr("tts_base_url", ""),
				ttsModel = config.GetStringOr("tts_model", "tts-1"),
				hasTtsApiKey = config.GetStringOr("tts_api_key", "").Length > 0,
				ttsVoice = config.GetStringOr("tts_voice", ""),
				ttsSpeed = ReadFloat(config, "tts_speed") ?? 1.0,
				ttsAutoPlay = ParseBoolFlag(config.GetStringOr("tts_auto_play", "")) ?? false,
				gptsovitsBaseUrl = config.GetStringOr("gptsovits_base_url", "http://127.0.0.1:9880"),
				gptsovitsRefAudio = config.GetStringOr("gptsovits_ref_audio", ""),
				gptsovitsPromptText = config.GetStringOr("gptsovits_prompt_text", ""),
				gptsovitsPromptLang = config.GetStringOr("gptsovits_prompt_lang", "zh"),
				indexttsTemplateAudio = config.GetStringOr("indextts_template_audio", ""),
				indexttsEmoAlpha = ReadFloat(config, "indextts_emo_alpha") ?? 0.3,
				sttProvider = config.GetStringOr("stt_provider", "whisper"),
				sttBaseUrl = config.GetStringOr("stt_base_url", ""),
				hasSttApiKey = config.GetStringOr("stt_api_key", "").Length > 0,
				noticePending = config.GetStringOr("voice_notice_pending", "") == "1",
				speaking = Voice.IsSpeaking,
			},
			embedding = embeddingSnapshot,
			proactive = new
			{
				idleEnabled = !Services.SafeMode && (ParseBoolFlag(config.GetStringOr("proactive_idle_enabled", "true")) ?? true),
				idleMinutes = (int)(ReadFloat(config, "proactive_idle_minutes") ?? ProactiveScheduler.DefaultIdleMinutes),
				dailyGreeting = !Services.SafeMode && (ParseBoolFlag(config.GetStringOr("proactive_daily_greeting", "true")) ?? true),
				reminders = Proactive.ListReminders().Select(item => new
				{
					id = item.Id,
					content = item.Content,
					triggerTime = item.TriggerAt,
					repeatDaily = item.RepeatDaily,
					status = item.Status,
					timezone = item.Timezone,
					recurrenceJson = item.RecurrenceJson,
					snoozedUntil = item.SnoozedUntil,
				}).ToArray(),
			},
			skills = Skills.GetInstalled().Select(skill => new
			{
				id = skill.Id, name = skill.Name, description = skill.Description, author = skill.Author,
				version = skill.Version, icon = skill.Icon, tags = skill.Tags.ToArray(), category = skill.Category,
				instructions = "", // 详情按需 skills_export 获取, 避免快照膨胀
				enabled = skill.Enabled, source = skill.Source,
			}).ToArray(),
			enabledSkillsCount = Skills.GetEnabled().Count,
			tools = Tools.List().Select(tool => new
			{
				name = tool.Name, description = tool.Description,
				permissionLevel = tool.PermissionLevel, category = tool.Category, enabled = tool.Enabled,
			}).ToArray(),
			mcpServersCount = McpServerCount(),
			emotion = new {type = Emotion.CurrentType},
			automation = Services.Automation?.GetSnapshot(),
		};
	}

	/// <summary>当前操作系统名 (前端按它决定平台相关文案)</summary>
	private static string PlatformOsName()
	{
		if (OperatingSystem.IsWindows()) return "windows";
		if (OperatingSystem.IsMacOS()) return "macos";
		if (OperatingSystem.IsLinux()) return "linux";
		return "unknown";
	}

	/// <summary>已知模型目录 (展示名由前端静态目录映射)</summary>
	private static IReadOnlyList<string> ModelCatalogIds() => SupportedModelIds.All;

	private IReadOnlyList<string> ModelExpressions(string modelId)
	{
		try
		{
			string dir = Services.Resources.ResourceDir(Nori.Core.Resources.ResourceType.Live2D, modelId);
			return Core.Live2D.Model3Meta.Read(dir).Expressions;
		}
		catch
		{
			return [];
		}
	}

	private bool IsModelInstalled(string modelId)
	{
		try
		{
			return Services.Resources.IsInstalled(Nori.Core.Resources.ResourceType.Live2D, modelId);
		}
		catch
		{
			return false;
		}
	}

	private int McpServerCount()
	{
		try
		{
			return Services.Mcp.GetServerConfigs().Count;
		}
		catch
		{
			return 0;
		}
	}
}
