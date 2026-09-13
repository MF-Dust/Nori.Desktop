using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Nori.Core;
using Nori.Core.Agent;
using Nori.Core.Automation;
using Nori.Core.Configuration;
using Nori.Core.Emotion;
using Nori.Core.Logging;
using Nori.Core.Live2D;
using Nori.Core.Memory;
using Nori.Core.Mcp;
using Nori.Core.Network;
using Nori.Core.Proactive;
using Nori.Core.Skills;
using Nori.Core.Security;
using Nori.Core.Tools;
using Nori.Core.Telemetry;
using Nori.Core.Voice;
using Nori.PluginRuntime;
using Nori.Desktop.Audio;
using Nori.Desktop.Automation;
using Nori.Desktop.Bridge;
using Nori.Desktop.Telemetry;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Runtime;

/// <summary>
/// 应用运行时协调层
///
/// 承接前端迁移过来的全部业务编排: Agent 会话与取消、工具授权、技能/情绪/提醒/
/// 记忆/语音服务装配, 以及面向 WebView 的带版本号 UI 状态快照。
///
/// 事件出口约定:
/// - nori:agent-event   → 仅推送给发起会话的窗口 (状态/chunk/用量/授权/完成/错误)
/// - nori:state-changed → 全局广播 (快照版本 + 变更主题)
/// - nori:proactive-message / nori:stt-result / nori:voice-notice → 对应窗口或全局
///
/// 秘密纪律: 快照只返回 hasApiKey 等脱敏标记, 明文绝不回传事件/日志/错误。
/// </summary>
public sealed class AppRuntime : IAsyncDisposable
{
	/// <summary>工具授权等待超时 (秒); 超时一律 fail-closed 拒绝</summary>
	public const int ApprovalTimeoutSeconds = 60;

	private const string McpToolCategory = "mcp";
	private const string PluginToolCategory = "plugin";
	private const int PluginToolsRefreshDebounceMs = 500;
	private const int McpRefreshLogMaxCharacters = 192;
	private const int McpRefreshLogServerIdMaxCharacters = 64;

	private readonly ConcurrentDictionary<string, AgentSessionState> _sessions = new();
	private readonly ConcurrentDictionary<string, PendingApproval> _approvals = new();
	private readonly ConcurrentDictionary<string, PendingDesktopApproval> _desktopApprovals = new();
	private readonly ConcurrentDictionary<Task, byte> _backgroundTasks = new();
	private readonly CancellationTokenSource _lifetimeCts = new();
	private readonly WebViewAudioPlayback _playback;
	private readonly WebViewMicrophoneRecorder _recorder;
	private readonly AudioHostChannel _audioChannel;
	private readonly ReflectionWorker _reflectionWorker;
	private readonly PetInteractionReactionService _petInteractionService;
	private readonly SemaphoreSlim _petInteractionGate = new(1, 1);
	private readonly SemaphoreSlim _mcpRefreshGate = new(1, 1);
	private readonly SemaphoreSlim _pluginToolsRefreshGate = new(1, 1);
	private System.Threading.Timer? _pluginToolsRefreshTimer;
	private readonly Lock _petInteractionThrottleGate = new();
	private readonly Lock _petSpeechGate = new();
	private CancellationTokenSource? _petInteractionCts;
	private CancellationTokenSource? _petSpeechCts;
	private PetInteractionTrigger? _activePetInteractionTrigger;
	private bool _activePetInteractionFallbackPosted;
	private DateTimeOffset _lastPetInteractionAt = DateTimeOffset.MinValue;
	private bool _petInteractionSubscribed;
	private int _disposed;

	public AppServices Services { get; }

	public ToolRegistry Tools { get; }

	public SkillService Skills { get; }

	public EmotionManager Emotion { get; }

	public ProactiveScheduler Proactive { get; }

	public MemoryService Memory { get; }

	public KnowledgeService Knowledge { get; }

	public MemoryLifecycleService Lifecycle { get; }

	public VoiceService Voice { get; }

	public AgentEngine Engine { get; }

	/// <summary>伴侣轻量互动 LLM 服务, 不进入聊天历史和工具链。</summary>
	public PetInteractionReactionService PetInteraction => _petInteractionService;

	/// <summary>当前快照版本号 (每次状态变更递增)</summary>
	public int SnapshotVersion => Volatile.Read(ref _snapshotVersion);

	/// <summary>运行时快照失效时通知原生设置窗口。</summary>
	public event Action? StateChanged;

	private int _snapshotVersion = 1;
	private readonly Lock _snapshotCacheGate = new();
	private object? _cachedSnapshot;
	private int _cachedSnapshotVersion;

	private int _initStartPending;

	/// <summary>
	/// 托盘是否真的可用
	///
	/// 由 App 在装载托盘后回填; 不可用时前端在主窗内显示常驻入口与退出按钮。
	/// </summary>
	public bool TrayAvailable { get; set; } = true;

	/// <summary>
	/// 标记“初始化开始”已发生
	///
	/// 首启路径下 init 窗口隐藏启动, 向导完成时广播的 nori:init-start 有可能早于
	/// init 页面订阅 (WebView 加载比广播慢), 事件就会永久丢失 —— 页面卡在转圈.
	/// 因此额外留一个标志供页面就绪时回放.
	/// </summary>
	public void MarkInitStartPending() => Interlocked.Exchange(ref _initStartPending, 1);

	/// <summary>取走并清除“初始化开始”标志 (只能被消费一次)</summary>
	public bool ConsumeInitStartPending() => Interlocked.Exchange(ref _initStartPending, 0) == 1;

	public AppRuntime(AppServices services)
	{
		Services = services;
		ConfigStore config = services.Config;
		services.Automation ??= new AutomationRuntime(
			config,
			services.SafeMode,
			OperatingSystem.IsWindows(),
			visionAvailable: !services.SafeMode,
			browserRunnerFactory: services.AutomationBrowserRunnerFactory,
			chatService: services.SafeMode ? null : services.Chat,
			desktopVisionRunnerFactory: services.SafeMode ? null : services.AutomationDesktopVisionRunnerFactory,
			desktopVisionPlannerFactory: services.SafeMode ? null : services.AutomationDesktopVisionPlannerFactory,
			desktopVisionActionFactory: services.SafeMode ? null : services.AutomationDesktopVisionActionFactory,
			desktopVisionScreenshotFactory: services.SafeMode ? null : services.AutomationDesktopVisionScreenshotFactory,
			desktopVisionWindowCatalogFactory: services.SafeMode ? null : services.AutomationDesktopVisionWindowCatalogFactory,
			desktopVisionApprovalCallback: services.SafeMode ? null : services.AutomationDesktopVisionApprovalCallback,
			auditSink: services.AutomationAudit);
		services.Automation.AuditSink ??= services.AutomationAudit;
		if (!services.SafeMode && services.Automation.DesktopVisionApprovalCallback is null)
		{
			services.Automation.DesktopVisionApprovalCallback = RequestAutomationApprovalAsync;
		}
		if (!services.SafeMode && services.Automation.BrowserApprovalCallback is null)
		{
			services.Automation.BrowserApprovalCallback = RequestAutomationApprovalAsync;
		}
		services.Automation.Changed += OnAutomationChanged;
		if (services.Update is not null) services.Update.StatusChanged += OnUpdateStatusChanged;

		Memory = new MemoryService(services.Memory, services.Embedding, config, startBackgroundWorker: !services.SafeMode);
		// 嵌入批处理恢复期的单轮失败降级为 warn 日志, 连续多轮失败才由 MemoryService 升级为 Error 遥测。
		Memory.EmbeddingDiagnostic = (severity, message) =>
		{
			try { services.Logger.Write(LogSource.Backend, severity, message); } catch { }
		};
		Knowledge = new KnowledgeService(services.Database, Memory, config, services.Paths.KnowledgePath);
		Knowledge.StatusChanged = () => InvalidateSnapshot("memory");
		Memory.Knowledge = Knowledge;
		Lifecycle = new MemoryLifecycleService(Memory);
		ReflectionService reflection = new(services.Http, services.Chat, Memory, config);
		_reflectionWorker = new ReflectionWorker(reflection, exception =>
		{
			try { services.Logger.Write(LogSource.Backend, "warn", $"记忆整理失败: {SensitiveDataRedactor.ExceptionSummary(exception)}"); }
			catch { }
		}, () => InvalidateSnapshot("memory"));
		Skills = new SkillService(config, services.PublicHttp);
		Emotion = new EmotionManager(config);

		ReminderStore reminderStore = new(services.Database);
		Proactive = new ProactiveScheduler(
			reminderStore, config, services.Logger,
			GetIdleSecondsSafe);

		// 音频与录音下沉到 main 窗口的 WebAudio / MediaRecorder: 三平台一套代码, 不再依赖 NAudio
		MediaExchange media = services.Assets?.Media ?? new MediaExchange();
		Func<string, string> mediaUrl = services.Assets is {} assets
			? assets.MediaUrl
			: _ => throw new InvalidOperationException("资源服务未启动, 音频端点不可用");
		AudioHostChannel channel = new(() => services.Windows?.GetNoriWindow(WindowLabels.Main));
		WebViewAudioPlayback playback = new(media, mediaUrl, channel);
		WebViewMicrophoneRecorder recorder = new(media, mediaUrl, channel);
		_playback = playback;
		_recorder = recorder;
		_audioChannel = channel;
		Voice = new VoiceService(services.Http, config, playback, () => VoiceRetired() ? null : recorder, services.Paths);
		_petInteractionService = new PetInteractionReactionService(services.Http, config);

		Tools = BuildToolRegistry(true);
		Engine = new AgentEngine(
			services.Http,
			config,
			services.Chat,
			Tools,
			Skills,
			Emotion,
			Memory,
			pet: new PetActionsAdapter(() => services.PetRuntime),
			motionNames: () => FlattenMotionNames(),
			expressionNames: () => services.PetRuntime?.Expressions ?? [],
			trace: services.AgentTrace,
			// 未启用时这条路完全不参与，桌宠照旧走本机 agent。
			luoLiCore: new Nori.Core.Chat.LuoLiCore.LuoLiCoreConversation(
				new Nori.Core.Chat.LuoLiCore.LuoLiCoreSettingsStore(config),
				options => new Nori.Core.Chat.LuoLiCore.LuoLiCoreSdkClient(services.Http, options)),
			// 走 LuoLiCore 时远端只给文本，表情动作在本地挑。
			replyReaction: new ReplyReactionService(services.Http, config));

		// 窗口显隐变化 (含托盘切换伴侣) 直接作废快照, 主界面的伴侣状态因此不会陈旧
		if (services.Windows is not null)
		{
			services.Windows.VisibilityChanged += (label, visible) =>
			{
				if (label == WindowLabels.Pet && !visible)
				{
					CancelPetInteractionRequest();
					CancelPetInteractionSpeech();
				}
				InvalidateSnapshot(label == WindowLabels.Pet ? "pet" : "windows");
			};
		}
	}

	// ===================================================================
	// 启动装配
	// ===================================================================

	/// <summary>启动各子系统并接线事件</summary>
	public void Start()
	{
		Emotion.Initialize();
		if (!_petInteractionSubscribed && Services.PetRuntime is not null)
		{
			Services.PetRuntime.InteractionTriggered += OnPetInteractionTriggered;
			Services.PetRuntime.ModelLoadRequested += CancelPetInteractionRequest;
			Services.PetRuntime.ModelLoadRequested += CancelPetInteractionPresentation;
			Services.PetRuntime.ModelLoadRequested += OnPetModelStateChanged;
			Services.PetRuntime.ModelChanged += OnPetModelStateChanged;
			Services.PetRuntime.ModelLoadFailed += OnPetModelStateChanged;
			_petInteractionSubscribed = true;
		}
		Emotion.ExpressionRequested += expression =>
		{
			try
			{
				Services.PetRuntime?.PlayExpression(expression);
			}
			catch
			{
				/* 表情未匹配时忽略 */
			}
		};

		// 回放持久化的工具禁用清单
		if (Services.Config.Get("tools_disabled") is ConfigValue.Json {Value: JsonNode node})
		{
			try
			{
				List<string>? names = node.Deserialize<List<string>>(BridgeJson.Options);
				if (names is {Count: > 0}) Tools.RestoreDisabled(names);
			}
			catch
			{
				/* 清单损坏时忽略 */
			}
		}

		if (!Services.SafeMode)
		{
			Proactive.Message += message => Dispatcher.UIThread.Post(() => OnProactiveMessage(message));
			Proactive.Start();

			// 插件贡献动作 → AI 工具 (plugin 分类): 活跃插件变化时防抖刷新
			if (Services.PluginRuntime is not null)
			{
				Services.PluginRuntime.ActivePluginsChanged += SchedulePluginToolsRefresh;
				SchedulePluginToolsRefresh();
			}

			// Knowledge 和 Reflection 都在后台启动；索引或整理失败不能阻塞聊天。
			_reflectionWorker.Start();
			_reflectionWorker.TryEnqueue();
			TrackBackground(InitializeKnowledgeAsync, "Memory.md index");
			TrackBackground(() => Memory.ReembedAllAsync(_lifetimeCts.Token, false), "memory embedding rebuild");
			TrackBackground(RunMemoryMaintenanceAsync, "memory lifecycle");
		}

		// 口型同步: 前端回传的播放音量采样直驱原生伴侣嘴型
		_playback.VolumeSampled += level =>
		{
			try
			{
				Services.PetRuntime?.SetMouthOpen((float)level, true);
			}
			catch
			{
				/* 伴侣未加载时忽略 */
			}
		};
		_playback.PlayingChanged += playing =>
		{
			try
			{
				Services.PetRuntime?.SetMouthOpen(0, playing);
			}
			catch
			{
				/* 伴侣未加载时忽略 */
			}
		};
		Voice.SpeakingChanged += _ => InvalidateSnapshot("voice");

		Voice.VolumeChanged += volume => _playback.SetDeviceVolume(volume);
		_playback.SetDeviceVolume(Voice.GetVolume());

		if (!Services.SafeMode)
		{
			DetectLegacyVoiceConfig();
			TrackBackground(() => RefreshMcpToolsAsync(), "MCP tools refresh");
		}

		InvalidateSnapshot("all");
	}

	/// <summary>安全获取系统空闲秒数 (非 Windows 返回 null)</summary>
	private static double? GetIdleSecondsSafe()
	{
		if (!OperatingSystem.IsWindows()) return null;
		try
		{
			return SystemIdleTime.GetIdleSeconds();
		}
		catch
		{
			return null;
		}
	}

	private void DetectLegacyVoiceConfig()
	{
		if (!Voice.HasRetiredVoiceConfig()) return;
		string flagged = Services.Config.GetStringOr("voice_notice_pending", "");
		if (flagged.Length > 0) return; // 已提示过或已处理
		Services.Config.Set("voice_notice_pending", new ConfigValue.Text("1"));
	}

	private bool VoiceRetired() => VoiceService.RetiredProviders.Contains(Services.Config.GetStringOr("stt_provider", ""));

	private void OnProactiveMessage(ProactiveMessage message)
	{
		try
		{
			Services.PetRuntime?.PlayMotionByName(message.Motion);
			Services.PetRuntime?.PlayExpression(message.Expression);
		}
		catch
		{
			/* 伴侣未加载时忽略 */
		}
		BroadcastEvent("nori:proactive-message", new {text = message.Text});
		bool autoTts = ParseBoolFlag(Services.Config.GetStringOr("tts_auto_play", "")) ?? false;
		if (autoTts)
		{
			_ = SpeakSafelyAsync(message.Text);
		}
	}

	private void CancelPetInteractionRequest() => CancelPetInteractionRequest(false);

	/// <summary>取消当前伴侣 AI 请求；聊天抢占时只补发一次本地兜底。</summary>
	private void CancelPetInteractionRequest(bool applyLocalFallback)
	{
		CancellationTokenSource? requestCts;
		PetInteractionTrigger? fallback = null;
		lock (_petInteractionThrottleGate)
		{
			requestCts = _petInteractionCts;
			if (applyLocalFallback
				&& !_activePetInteractionFallbackPosted
				&& _activePetInteractionTrigger is { } trigger)
			{
				_activePetInteractionFallbackPosted = true;
				fallback = trigger;
			}
		}
		try { requestCts?.Cancel(); }
		catch (ObjectDisposedException) { }
		if (fallback is not null) PostPetInteractionFallback(fallback);
	}

	private void OnPetInteractionTriggered(PetInteractionTrigger trigger)
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		if (!IsPetInteractionAiEnabled() || !IsLlmConfigured() || _sessions.Count > 0)
		{
			PostPetInteractionFallback(trigger);
			return;
		}
		if (!_petInteractionGate.Wait(0))
		{
			PostPetInteractionFallback(trigger);
			return;
		}

		DateTimeOffset now = DateTimeOffset.UtcNow;
		lock (_petInteractionThrottleGate)
		{
			if (now - _lastPetInteractionAt < TimeSpan.FromSeconds(3))
			{
				_petInteractionGate.Release();
				PostPetInteractionFallback(trigger);
				return;
			}
			_lastPetInteractionAt = now;
		}

		Task task = RunPetInteractionAsync(trigger);
		TrackTask(task);
	}

	private async Task RunPetInteractionAsync(PetInteractionTrigger trigger)
	{
		using CancellationTokenSource requestCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
		lock (_petInteractionThrottleGate)
		{
			_petInteractionCts = requestCts;
			_activePetInteractionTrigger = trigger;
			_activePetInteractionFallbackPosted = false;
		}
		try
		{
			PetInteractionReactionRequest request = new()
			{
				ModelId = trigger.ModelId,
				RegionId = trigger.Hit.Region.Id,
				RegionName = trigger.Hit.Region.Name,
				ModelX = trigger.Hit.ModelX,
				ModelY = trigger.Hit.ModelY,
				RegionX = trigger.Hit.RegionX,
				RegionY = trigger.Hit.RegionY,
				CurrentEmotion = Emotion.CurrentType,
				AvailableMotions = Services.PetRuntime.MotionGroups
					.Select(group => new MotionGroupInfo {Group = group.Group, Names = [.. group.Names]})
					.ToArray(),
				AvailableExpressions = Services.PetRuntime.Expressions.ToArray(),
			};
			PetInteractionReaction reaction = await _petInteractionService.ReactAsync(request, requestCts.Token).ConfigureAwait(false);
			if (requestCts.IsCancellationRequested || !IsCurrentPetInteraction(trigger)) return;
			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (!requestCts.IsCancellationRequested) ApplyPetInteractionReaction(trigger, reaction);
			});
		}
		catch (OperationCanceledException) when (requestCts.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
		{
			// 应用退出、模型切换、隐藏或聊天抢占时取消，不显示错误也不应用旧结果。
		}
		catch (Exception exception)
		{
			try { Services.Logger.Write(LogSource.Backend, "warn", $"伴侣 AI 互动失败: {SensitiveDataRedactor.ExceptionSummary(exception)}"); } catch { }
			PostActivePetInteractionFallback(trigger, requestCts);
		}
		finally
		{
			lock (_petInteractionThrottleGate)
			{
				if (ReferenceEquals(_petInteractionCts, requestCts))
				{
					_petInteractionCts = null;
					_activePetInteractionTrigger = null;
					_activePetInteractionFallbackPosted = false;
				}
			}
			_petInteractionGate.Release();
		}
	}

	private void ApplyPetInteractionReaction(PetInteractionTrigger trigger, PetInteractionReaction reaction)
	{
		if (!IsCurrentPetInteraction(trigger)) return;
		if (!string.IsNullOrWhiteSpace(reaction.Emotion) && EmotionTypes.IsValid(reaction.Emotion))
		{
			try { Emotion.SetEmotion(reaction.Emotion); } catch { }
		}
		if (!string.IsNullOrWhiteSpace(reaction.Motion)) Services.PetRuntime.PlayMotionByName(reaction.Motion);
		if (!string.IsNullOrWhiteSpace(reaction.Expression)) Services.PetRuntime.PlayExpression(reaction.Expression);
		if (string.IsNullOrWhiteSpace(reaction.Text)) return;
		Services.Windows.ShowPetSpeech(reaction.Text);
		bool autoTts = ParseBoolFlag(Services.Config.GetStringOr("tts_auto_play", "")) ?? false;
		if (autoTts) StartPetInteractionSpeech(reaction.Text);
	}

	private void PostActivePetInteractionFallback(PetInteractionTrigger trigger, CancellationTokenSource requestCts)
	{
		bool shouldPost = false;
		lock (_petInteractionThrottleGate)
		{
			if (ReferenceEquals(_petInteractionCts, requestCts) && !_activePetInteractionFallbackPosted)
			{
				_activePetInteractionFallbackPosted = true;
				shouldPost = true;
			}
		}
		if (shouldPost) PostPetInteractionFallback(trigger);
	}

	private void PostPetInteractionFallback(PetInteractionTrigger trigger)
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (IsCurrentPetInteraction(trigger)) Services.PetRuntime.ApplyLocalInteraction(trigger.Hit.Region);
		});
	}

	private bool IsCurrentPetInteraction(PetInteractionTrigger trigger) =>
		Services.Windows.IsWindowVisible(WindowLabels.Pet)
		&& Services.PetRuntime.CurrentModelId.Equals(trigger.ModelId, StringComparison.OrdinalIgnoreCase)
		&& Services.PetRuntime.ModelGeneration == trigger.ModelGeneration;

	private bool IsPetInteractionAiEnabled() =>
		!Services.SafeMode
		&& (ParseBoolFlag(Services.Config.GetStringOr(PetInteractionConfig.AiEnabledKey, "")) ?? false);

	private bool IsLlmConfigured() => Services.AiSettings.Read().Chat.IsConfigured;

	private void StartPetInteractionSpeech(string text)
	{
		CancelPetInteractionSpeech();
		CancellationTokenSource speechCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
		lock (_petSpeechGate) _petSpeechCts = speechCts;
		TrackTask(SpeakPetInteractionSafelyAsync(text, speechCts));
	}

	private void CancelPetInteractionPresentation()
	{
		CancelPetInteractionSpeech();
		Dispatcher.UIThread.Post(Services.Windows.ClearPetSpeech);
	}

	private void OnPetModelStateChanged() => InvalidateSnapshot("models", "pet");

	private void CancelPetInteractionSpeech()
	{
		CancellationTokenSource? speechCts;
		lock (_petSpeechGate)
		{
			speechCts = _petSpeechCts;
			_petSpeechCts = null;
		}
		try { speechCts?.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	private async Task SpeakPetInteractionSafelyAsync(string text, CancellationTokenSource speechCts)
	{
		try
		{
			// 伴侣互动朗读同样带上全局情绪状态，让 TTS 情感与表情联动一致。
			TtsSynthesizeOptions speechOptions = new() {EmotionText = Emotion.CurrentType};
			await Voice.SpeakAsync(text, speechOptions, speechCts.Token);
		}
		catch (OperationCanceledException) when (speechCts.IsCancellationRequested)
		{
			// 隐藏、切换模型、开始聊天或退出时取消，不作为播放失败。
		}
		catch (Exception exception)
		{
			try { Services.Logger.Write(LogSource.Backend, "warn", $"伴侣互动朗读失败: {SensitiveDataRedactor.ExceptionSummary(exception)}"); } catch { }
		}
		finally
		{
			lock (_petSpeechGate)
			{
				if (ReferenceEquals(_petSpeechCts, speechCts)) _petSpeechCts = null;
			}
			speechCts.Dispose();
		}
	}

	private async Task SpeakSafelyAsync(string text)
	{
		try
		{
			await Voice.SpeakAsync(text);
		}
		catch (Exception exception)
		{
			try
			{
				Services.Logger.Write(LogSource.Backend, "warn", $"主动朗读失败: {SensitiveDataRedactor.ExceptionSummary(exception)}");
			}
			catch
			{
				// 日志失败保持静默
			}
		}
	}

	/// <summary>
	/// 同步已连接 MCP 工具到 Agent 注册表。
	/// 每个动态工具默认 confirm, 由 AgentRuntime 的逐调用授权链路 fail-closed 控制。
	/// </summary>
	public async Task RefreshMcpToolsAsync(CancellationToken cancellationToken = default)
	{
		// 安全模式不能通过聊天启动或其他间接路径刷新外部 MCP。
		if (Services.SafeMode) return;

		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
		CancellationToken ct = linkedCts.Token;
		bool entered = false;
		string failureServerId = "unknown";
		try
		{
			// 串行化刷新, 防止较早的慢刷新在较新的结果之后覆盖工具集合。
			await _mcpRefreshGate.WaitAsync(ct).ConfigureAwait(false);
			entered = true;
			ct.ThrowIfCancellationRequested();

			// 所有连接状态、Schema 和工具闭包都先在局部集合中完成。
			// 任何失败或取消都不能触碰注册表中的上一版工具。
			IReadOnlyList<McpServerStatusInfo> servers = await Services.Mcp.GetServersAsync().ConfigureAwait(false);
			ct.ThrowIfCancellationRequested();

			McpServerStatusInfo? unavailable = servers.FirstOrDefault(server =>
				string.Equals(server.Status, "error", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(server.Status, "connecting", StringComparison.OrdinalIgnoreCase)
				|| (!string.Equals(server.Status, "connected", StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(server.Status, "disconnected", StringComparison.OrdinalIgnoreCase)));
			if (unavailable is not null)
			{
				LogMcpRefreshFailure(
					unavailable.ServerId,
					string.Equals(unavailable.Status, "error", StringComparison.OrdinalIgnoreCase)
						? "server-error"
						: "server-not-ready");
				return;
			}

			List<RegisteredTool> replacements = [];
			HashSet<string> replacementNames = new(StringComparer.Ordinal);
			foreach (McpServerStatusInfo server in servers.Where(server =>
				string.Equals(server.Status, "connected", StringComparison.OrdinalIgnoreCase)))
			{
				failureServerId = server.ServerId;
				foreach (McpToolDefinition definition in server.Tools)
				{
					ct.ThrowIfCancellationRequested();
					string serverId = server.ServerId;
					string toolName = definition.Name;
					if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(toolName))
						throw new InvalidOperationException("MCP 工具定义无效");

					string fullName = $"mcp__{serverId}__{toolName}";
					if (!replacementNames.Add(fullName))
						throw new InvalidOperationException("MCP 工具名称重复");

					JsonObject schema = ToolLimits.CapSchema(definition.InputSchema);
					replacements.Add(new RegisteredTool
					{
						Name = fullName,
						Description = $"[{server.Name}] {McpConfigValidator.CapDescription(definition.Description ?? toolName)}",
						Parameters = schema,
						PermissionLevel = "confirm",
						Category = McpToolCategory,
						Execute = async (arguments, context) =>
						{
							JsonObject? objectArguments = arguments as JsonObject;
							McpToolResult result = await Services.Mcp.CallToolAsync(serverId, toolName, objectArguments, context.CancellationToken);
							if (result.IsError) throw new InvalidOperationException(result.AsText());
							return result.AsText();
						},
					});
				}
			}

			ct.ThrowIfCancellationRequested();
			failureServerId = "unknown";
			Tools.ReplaceCategory(McpToolCategory, replacements);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			LogMcpRefreshFailure(failureServerId, "cancelled");
			throw;
		}
		catch (Exception exception)
		{
			// 只记录服务 ID 和固定类别, 不写入异常正文、Schema、参数或工具结果。
			LogMcpRefreshFailure(failureServerId, McpRefreshErrorCategory(exception));
		}
		finally
		{
			if (entered) _mcpRefreshGate.Release();
		}
	}

	private void LogMcpRefreshFailure(string? serverId, string category)
	{
		string safeServerId = CapMcpLogPart(serverId, McpRefreshLogServerIdMaxCharacters);
		string safeCategory = CapMcpLogPart(category, 32);
		string message = $"MCP 工具刷新失败: server_id={safeServerId} category={safeCategory}";
		if (message.Length > McpRefreshLogMaxCharacters) message = message[..McpRefreshLogMaxCharacters];
		try { Services.Logger.Write(LogSource.Backend, "warn", message); }
		catch { }
	}

	private static string McpRefreshErrorCategory(Exception exception) => exception switch
	{
		OperationCanceledException => "cancelled",
		TimeoutException => "timeout",
		JsonException => "schema",
		IOException => "transport",
		ObjectDisposedException => "lifecycle",
		InvalidOperationException => "definition",
		_ => "refresh",
	};

	private static string CapMcpLogPart(string? value, int maxCharacters)
	{
		if (string.IsNullOrEmpty(value) || maxCharacters <= 0) return "unknown";
		return value.Length <= maxCharacters ? value : value[..maxCharacters];
	}

	/// <summary>活跃插件集合变化后防抖刷新插件工具。</summary>
	private void SchedulePluginToolsRefresh()
	{
		_pluginToolsRefreshTimer?.Dispose();
		_pluginToolsRefreshTimer = new Timer(
			_ => { _ = RefreshPluginToolsAsync(); },
			null, PluginToolsRefreshDebounceMs, Timeout.Infinite);
	}

	private async Task RefreshPluginToolsAsync()
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		PluginRuntimeHost? pluginRuntime = Services.PluginRuntime;
		if (pluginRuntime is null) return;
		bool entered = false;
		try
		{
			if (!await _pluginToolsRefreshGate.WaitAsync(0).ConfigureAwait(false)) return;
			entered = true;

			List<RegisteredTool> tools = [];
			HashSet<string> names = new(StringComparer.Ordinal);
			foreach ((PluginDescriptor plugin, IPluginActionContribution action) in pluginRuntime.GetContributionsWithSource<IPluginActionContribution>())
			{
				if (string.IsNullOrWhiteSpace(action.Id)) continue;
				string pluginName = plugin.Id.Replace('.', '_');
				string fullName = $"plugin__{pluginName}__{action.Id}";
				if (!names.Add(fullName)) continue;
				tools.Add(new RegisteredTool
				{
					Name = fullName,
					Description = $"[{plugin.Name}] {action.Description}",
					Parameters = ToolLimits.CapSchema(action.ParametersSchema as JsonObject ?? new JsonObject()),
					PermissionLevel = "safe",
					Category = PluginToolCategory,
					Execute = async (arguments, context) =>
						await action.InvokeAsync(arguments, context.CancellationToken).ConfigureAwait(false),
				});
			}
			Tools.ReplaceCategory(PluginToolCategory, tools);
		}
		catch (Exception exception)
		{
			// 只记录类别与摘要, 不写入插件参数或结果
			try { Services.Logger.Write(LogSource.Backend, "warn", $"插件工具刷新失败: {SensitiveDataRedactor.ExceptionSummary(exception)}"); }
			catch { }
		}
		finally
		{
			if (entered) _pluginToolsRefreshGate.Release();
		}
	}

	private ToolRegistry BuildToolRegistry(bool audioAvailable)
	{
		ToolRegistry registry = new();
		BuiltinTools.RegisterAll(registry, new BuiltinToolDeps
		{
			Memory = Memory,
			Emotion = Emotion,
			Proactive = Proactive,
			Pet = new PetActionsAdapter(() => Services.PetRuntime),
			Clipboard = audioAvailable ? new AvaloniaClipboardOps(() => Services.Windows.Get(WindowLabels.Main)) : null,
			SystemInfo = new DesktopSystemInfo(Services.Config),
			Fetcher = new WebPageFetcher(Services.PublicHttp),
			Http = Services.PublicHttp,
			Config = Services.Config,
			OpenUrl = url => ShellOpen.OpenUrl(url),
		});
		return registry;
	}

	private IReadOnlyList<string> FlattenMotionNames()
	{
		IReadOnlyList<Core.Live2D.MotionGroupInfo>? groups = Services.PetRuntime?.MotionGroups;
		if (groups is null || groups.Count == 0) return [];
		return groups.SelectMany(group => group.Names).Distinct().ToList();
	}

	// ===================================================================
	// 聊天会话
	// ===================================================================

	/// <summary>
	/// 启动一次 Agent 会话; 返回 sessionId 供取消/授权关联
	/// </summary>
	public string StartChat(IBridgeSource source, string text)
	{
		if (Volatile.Read(ref _disposed) != 0) throw new InvalidOperationException("Application is shutting down");
		string sessionId = $"agent-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{Interlocked.Increment(ref _sessionCounter):x}";
		AgentSessionState session = new(source.Label);
		_sessions[sessionId] = session;
		// 聊天请求优先于伴侣轻量请求与其语音；旧 AI 请求改走该区域的本地兜底。
		CancelPetInteractionRequest(true);
		CancelPetInteractionPresentation();

		AgentCallbacks callbacks = new()
		{
			OnState = state => PostAgentEvent(session.SourceLabel, new {type = "state", sessionId, state = state.ToString().ToLowerInvariant()}),
			OnTextChunk = chunk => PostAgentEvent(session.SourceLabel, new {type = "chunk", sessionId, chunk}),
			OnToolExecuting = (name, args) => PostAgentEvent(session.SourceLabel, new {type = "tool-executing", sessionId, toolName = name, arguments = args}),
			OnToolExecuted = (name, result, error) => PostAgentEvent(session.SourceLabel, new
			{
				type = "tool-executed",
				sessionId,
				toolName = name,
				result = ToJsonNode(result),
				success = error is null,
				error,
			}),
			OnUsage = usage => PostAgentEvent(session.SourceLabel, new
			{
				type = "usage",
				sessionId,
				promptTokens = usage.PromptTokens,
				completionTokens = usage.CompletionTokens,
				totalTokens = usage.TotalTokens,
				cachedTokens = usage.CachedTokens,
				cacheHitRate = usage.CacheHitRate,
				durationMs = usage.DurationMs,
				model = usage.Model,
			}),
			RequestApproval = request => RequestApprovalAsync(session, sessionId, request),
			OnComplete = _ =>
			{
				/* complete 事件在 RunAsync 正常返回后统一发出 */
			},
		};

		Task worker = Task.Run(async () =>
		{
			using ITelemetryTransaction operation = Services.Telemetry.StartTransaction("agent.run");
			try
			{
				await RefreshMcpToolsAsync(session.Cts.Token);
				ProtocolMessage final = await Engine.RunAsync(text, sessionId, callbacks, session.Cts.Token);
				_reflectionWorker.TryEnqueue();
				PostAgentEvent(session.SourceLabel, new
				{
					type = "complete",
					sessionId,
					message = new
					{
						text = final.Text,
						emotion = final.Emotion,
						expression = final.Expression,
						action = final.Action,
					},
				});

				await AutoSpeakAsync(final.Text, final.Emotion, session.SourceLabel, session.Cts.Token);
			}
			catch (OperationCanceledException)
			{
				PostAgentEvent(session.SourceLabel, new {type = "cancelled", sessionId});
			}
			catch (Exception exception)
			{
				PostAgentEvent(session.SourceLabel, new {type = "error", sessionId, error = SensitiveDataRedactor.Redact(exception.Message)});
			}
			finally
			{
				_sessions.TryRemove(sessionId, out _);
				session.Dispose();
			}
		});
		session.Worker = worker;
		TrackTask(worker);

		return sessionId;
	}

	private int _sessionCounter;

	private async Task AutoSpeakAsync(string text, string? messageEmotion, string sourceLabel, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		bool autoTts = ParseBoolFlag(Services.Config.GetStringOr("tts_auto_play", "")) ?? false;
		if (!autoTts) return;

		// 情绪自动推断：优先本条 AI 回复自带情绪，否则用全局情绪状态机当前值；
		// 都没有 (或为 neutral) 时不传情绪，让 TTS 用音色自带情绪。
		string? emotion = string.IsNullOrWhiteSpace(messageEmotion) ? Emotion.CurrentType : messageEmotion.Trim();
		TtsSynthesizeOptions speechOptions = new() {EmotionText = emotion};
		PostAgentEvent(sourceLabel, new {type = "state", state = AgentRunState.Speaking.ToString().ToLowerInvariant()});
		try
		{
			await Voice.SpeakAsync(text, speechOptions, ct);
		}
		catch
		{
			/* 自动朗读失败不阻断完成事件 */
		}
		finally
		{
			PostAgentEvent(sourceLabel, new {type = "state", state = AgentRunState.Idle.ToString().ToLowerInvariant()});
		}
	}

	/// <summary>取消指定来源窗口的会话</summary>
	public bool CancelChat(string sourceLabel, string sessionId)
	{
		if (!_sessions.TryGetValue(sessionId, out AgentSessionState? session) || session.SourceLabel != sourceLabel)
		{
			return false;
		}
		session.Cts.Cancel();
		// 取消所有该会话挂起的授权 (fail-closed)
		foreach ((string requestId, PendingApproval approval) in _approvals)
		{
			if (approval.SessionId != sessionId) continue;
			if (_approvals.TryRemove(new KeyValuePair<string, PendingApproval>(requestId, approval)))
			{
				approval.Tcs.TrySetResult(false);
				approval.Dispose();
				PostAgentEvent(approval.SourceLabel, new {type = "approval-result", sessionId, requestId, approved = false, reason = "cancelled"});
			}
		}
		return true;
	}

	/// <summary>会话是否仍在运行</summary>
	public bool IsSessionActive(string sessionId) => _sessions.ContainsKey(sessionId);

	// ===================================================================
	// 工具授权
	// ===================================================================

	private async Task<bool> RequestApprovalAsync(AgentSessionState session, string sessionId, ToolApprovalRequest request)
	{
		TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PendingApproval approval = new(request.RequestId, session.SourceLabel, sessionId, tcs);

		if (!_approvals.TryAdd(request.RequestId, approval))
		{
			return false;
		}

		approval.ArmTimeout(ApprovalTimeoutSeconds, () =>
		{
			if (_approvals.TryRemove(request.RequestId, out PendingApproval? expired))
			{
				expired.Tcs.TrySetResult(false);
				expired.Dispose();
				PostAgentEvent(expired.SourceLabel, new {type = "approval-result", sessionId, requestId = request.RequestId, approved = false, reason = "timeout"});
			}
		});

		PostAgentEvent(session.SourceLabel, new
		{
			type = "approval-request",
			sessionId,
			requestId = request.RequestId,
			toolName = request.ToolName,
			arguments = request.Arguments,
			description = request.Description,
			permissionLevel = request.PermissionLevel,
			category = request.Category,
		});

		return await tcs.Task;
	}

	/// <summary>等待桌面或浏览器高风险动作的用户决定；未装配或取消时一律不自动放行。</summary>
	private async Task<AutomationApprovalDecision> RequestAutomationApprovalAsync(
		AutomationApprovalRequest request,
		CancellationToken cancellationToken)
	{
		if (Services.SafeMode)
		{
			Services.Automation?.RecordApprovalOutcome(request, AutomationApprovalOutcome.Denied);
			return AutomationApprovalDecision.Create(request, AutomationApprovalOutcome.Denied, DateTimeOffset.UtcNow);
		}
		TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PendingDesktopApproval approval = new(request, tcs);
		if (!_desktopApprovals.TryAdd(request.RequestId.ToString("D"), approval))
		{
			Services.Automation?.RecordApprovalOutcome(request, AutomationApprovalOutcome.Denied);
			return AutomationApprovalDecision.Create(request, AutomationApprovalOutcome.Denied, DateTimeOffset.UtcNow);
		}

		Services.Automation?.SetAutomationApproval(request);
		approval.ArmTimeout(ApprovalTimeoutSeconds, () =>
		{
			if (_desktopApprovals.TryRemove(request.RequestId.ToString("D"), out PendingDesktopApproval? expired))
			{
				expired.Tcs.TrySetResult(false);
				expired.Dispose();
				Services.Automation?.ClearAutomationApproval(request.RequestId);
				Services.Automation?.RecordApprovalOutcome(request, AutomationApprovalOutcome.Expired);
				PostAgentEvent(WindowLabels.Main, new
				{
					type = "approval-result",
					requestId = request.RequestId,
					approved = false,
					reason = "timeout",
				});
			}
		});
		PostAgentEvent(WindowLabels.Main, new
		{
			type = "approval-request",
			requestId = request.RequestId,
			taskId = request.TaskId,
			actionKinds = request.ActionKinds,
			permissionLevel = "confirm",
			category = "automation",
		});

		try
		{
			bool approved = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
			return AutomationApprovalDecision.Create(
				request,
				approved ? AutomationApprovalOutcome.Approved : AutomationApprovalOutcome.Denied,
				DateTimeOffset.UtcNow);
		}
		catch (OperationCanceledException)
		{
			Services.Automation?.RecordApprovalCancellation(request);
			throw;
		}
		finally
		{
			if (_desktopApprovals.TryRemove(request.RequestId.ToString("D"), out PendingDesktopApproval? removed))
			{
				removed.Dispose();
				Services.Automation?.ClearAutomationApproval(request.RequestId);
			}
		}
	}

	/// <summary>
	/// 回传授权决定; 只允许原始窗口响应。原生设置窗口可在明确可信上下文中
	/// 响应主窗口发起的自动化审批，未匹配的请求 fail-closed 忽略。
	/// </summary>
	public bool RespondApproval(string sourceLabel, string requestId, bool approved, bool allowNativeSettings = false)
	{
		if (_approvals.TryGetValue(requestId, out PendingApproval? approval)
			&& (approval.SourceLabel == sourceLabel
				|| (allowNativeSettings && approval.SourceLabel == WindowLabels.Main)))
		{
			if (!_approvals.TryRemove(new KeyValuePair<string, PendingApproval>(requestId, approval))) return false;
			approval.Dispose(); // 停掉超时定时器
			PostAgentEvent(approval.SourceLabel, new
			{
				type = "approval-result",
				sessionId = approval.SessionId,
				requestId,
				approved,
				reason = approved ? "approved" : "denied",
			});
			return approval.Tcs.TrySetResult(approved);
		}

		if (sourceLabel != WindowLabels.Main && !allowNativeSettings
			|| !_desktopApprovals.TryGetValue(requestId, out PendingDesktopApproval? desktopApproval)) return false;
		if (!_desktopApprovals.TryRemove(new KeyValuePair<string, PendingDesktopApproval>(requestId, desktopApproval))) return false;
		desktopApproval.Dispose();
		Services.Automation?.ClearAutomationApproval(desktopApproval.Request.RequestId);
		Services.Automation?.RecordApprovalOutcome(
			desktopApproval.Request,
			approved ? AutomationApprovalOutcome.Approved : AutomationApprovalOutcome.Denied);
		PostAgentEvent(WindowLabels.Main, new
		{
			type = "approval-result",
			requestId,
			taskId = desktopApproval.Request.TaskId,
			approved,
			reason = approved ? "approved" : "denied",
		});
		return desktopApproval.Tcs.TrySetResult(approved);
	}

	// ===================================================================
	// 前端音频宿主回报
	// ===================================================================

	/// <summary>前端回报一段音频播放结束 (或失败)</summary>
	public void ReportPlaybackFinished(string token, string? error) =>
		_playback.ReportPlaybackFinished(token, error);

	/// <summary>前端回报实时播放音量 (0~1), 驱动伴侣口型</summary>
	public void ReportAudioLevel(double level) => _playback.ReportLevel(level);

	/// <summary>前端 main WebView 完成监听器安装后的就绪握手。</summary>
	public void MarkAudioHostReady() => _audioChannel.MarkReady();

	/// <summary>前端回报 MediaRecorder 已获权并开始。</summary>
	public void ReportRecordingReady(string token) => _recorder.ReportRecordingReady(token);

	/// <summary>前端回报麦克风权限、录音或上传失败。</summary>
	public void ReportRecordingFailed(string token, string? error) => _recorder.ReportRecordingFailed(token, error);

	// ===================================================================
	// UI 状态快照
	// ===================================================================

	/// <summary>使快照失效并广播变更主题</summary>
	public void InvalidateSnapshot(params string[] topics)
	{
		Interlocked.Increment(ref _snapshotVersion);
		RaiseStateChanged();
		BroadcastEvent("nori:state-changed", new {version = SnapshotVersion, topics});
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
		AiEmbeddingSettingsSnapshot embeddingSnapshot = AiEmbeddingSettingsSnapshot.From(aiSettings.Embedding);

		var models = ModelCatalogIds().Select(id => new
		{
			id,
			installed = IsModelInstalled(id),
		}).ToArray();

		string selectedModel = config.GetStringOr("selected_model", ConfigStore.DefaultModel);
		float modelOpacity = ReadFloat(config, $"l2d_opacity_{selectedModel}") ?? ReadFloat(config, "l2d_opacity") ?? 1.0f;
		float modelRenderScale = ReadFloat(config, $"l2d_render_scale_{selectedModel}") ?? ReadFloat(config, "l2d_render_scale") ?? 2.0f;
		bool modelShadow = ParseBoolFlag(ReadModelString(config, "l2d_shadow", selectedModel, "true")) ?? true;
		string modelQualityMode = ReadModelString(config, "l2d_quality_mode", selectedModel, "adaptive");
		int modelMaxFps = (int)(ReadFloat(config, $"l2d_max_fps_{selectedModel}") ?? ReadFloat(config, "l2d_max_fps") ?? 0);
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
				sidebarCollapsed = ParseBoolFlag(config.GetStringOr("ui_sidebar_collapsed", "")) ?? false,
				autoCheckUpdates = config.GetBoolOr("auto_check_updates", true),
			},
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
				scale = ReadFloat(config, $"l2d_scale_{selectedModel}") ?? ReadFloat(config, "l2d_scale") ?? 1.0,
				expressions = ModelExpressions(selectedModel),
			},
			pet = new
			{
				visible = Services.Windows.IsWindowVisible(WindowLabels.Pet),
				renderMetrics = Services.PetRuntime?.RenderMetrics,
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

	// ===================================================================
	// 事件出口
	// ===================================================================

	/// <summary>向指定窗口推送 Agent 事件</summary>
	private void PostAgentEvent(string label, object payload)
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		try { Services.Windows.GetNoriWindow(label)?.PostEvent(AgentEventName, payload); }
		catch { /* windows may already be closing */ }
	}

	/// <summary>自动化状态变化只广播脱敏生命周期汇总。</summary>
	private void OnAutomationChanged()
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		InvalidateSnapshot("automation");
		AutomationSnapshot? snapshot = Services.Automation?.GetSnapshot();
		if (snapshot is not null) BroadcastEvent("nori:automation-changed", snapshot);
	}

	private void OnUpdateStatusChanged()
	{
		if (Volatile.Read(ref _disposed) == 0) InvalidateSnapshot("updater");
	}

	/// <summary>向所有 WebView 窗口广播</summary>
	private void BroadcastEvent(string name, object payload)
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		Dispatcher.UIThread.Post(() =>
		{
			if (Volatile.Read(ref _disposed) == 0)
			{
				try { Services.Windows.Broadcast(name, payload); }
				catch { /* windows may already be closing */ }
			}
		});
	}

	/// <summary>Agent 事件通道名</summary>
	public const string AgentEventName = "nori:agent-event";

	private static JsonNode? ToJsonNode(object? value)
	{
		if (value is null) return null;
		try
		{
			return JsonSerializerNode(value);
		}
		catch
		{
			return System.Text.Json.Nodes.JsonValue.Create(value.ToString());
		}
	}

	private static System.Text.Json.Nodes.JsonNode? JsonSerializerNode(object value)
	{
		string json = System.Text.Json.JsonSerializer.Serialize(value, BridgeJson.Options);
		return System.Text.Json.Nodes.JsonNode.Parse(json);
	}

	private static bool? ParseBoolFlag(string raw) => raw switch
	{
		"1" => true,
		"0" => false,
		_ when raw.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
		_ when raw.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
		_ => null,
	};

	private static string ReadModelString(ConfigStore config, string baseKey, string modelId, string fallback)
	{
		string modelValue = config.GetStringOr($"{baseKey}_{modelId}", "");
		return modelValue.Length > 0 ? modelValue : config.GetStringOr(baseKey, fallback);
	}

	private static float? ReadFloat(ConfigStore config, string key)
	{
		string raw = config.GetStringOr(key, "");
		if (raw.Length == 0) return null;
		if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return 1f;
		if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return 0f;
		return float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : null;
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_lifetimeCts.Cancel();
		if (_petInteractionSubscribed && Services.PetRuntime is not null)
		{
			Services.PetRuntime.InteractionTriggered -= OnPetInteractionTriggered;
			Services.PetRuntime.ModelLoadRequested -= CancelPetInteractionRequest;
			Services.PetRuntime.ModelLoadRequested -= CancelPetInteractionPresentation;
			Services.PetRuntime.ModelLoadRequested -= OnPetModelStateChanged;
			Services.PetRuntime.ModelChanged -= OnPetModelStateChanged;
			Services.PetRuntime.ModelLoadFailed -= OnPetModelStateChanged;
			_petInteractionSubscribed = false;
		}
		CancelPetInteractionRequest();
		CancelPetInteractionSpeech();
		if (Services.Automation is not null) Services.Automation.Changed -= OnAutomationChanged;
		if (Services.Update is not null) Services.Update.StatusChanged -= OnUpdateStatusChanged;

		foreach ((string _, AgentSessionState session) in _sessions)
		{
			session.Cts.Cancel();
		}

		foreach ((string _, PendingApproval approval) in _approvals)
		{
			approval.Tcs.TrySetResult(false);
			approval.Dispose();
		}
		foreach ((string _, PendingDesktopApproval approval) in _desktopApprovals)
		{
			approval.Tcs.TrySetResult(false);
			approval.Dispose();
			Services.Automation?.ClearAutomationApproval(approval.Request.RequestId);
			Services.Automation?.RecordApprovalCancellation(approval.Request);
		}
		_desktopApprovals.Clear();

		Task[] workers = _sessions.Values.Select(session => session.Worker).OfType<Task>().ToArray();
		await WaitBoundedAsync(workers, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		foreach (AgentSessionState session in _sessions.Values) session.Dispose();
		_sessions.Clear();
		await WaitBoundedAsync(_backgroundTasks.Keys.ToArray(), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		_backgroundTasks.Clear();

		try { await _reflectionWorker.DisposeAsync().ConfigureAwait(false); } catch { }
		try { await Knowledge.DisposeAsync().ConfigureAwait(false); } catch { }
		try { Proactive.Dispose(); } catch { }
		try { Emotion.Dispose(); } catch { }
		// Voice.Dispose 会逆向释放 _playback; 录音票据要单独作废
		try { _recorder.Dispose(); } catch { }
		try { Voice.Dispose(); } catch { }
		// 音频宿主通道最后解除: 让所有 WaitUntilReadyAsync 等待者立即结束而不是等超时
		try { _audioChannel.Dispose(); } catch { }
		try { if (Services.Automation is not null) await Services.Automation.DisposeAsync().ConfigureAwait(false); } catch { }
		_petInteractionGate.Dispose();
		_pluginToolsRefreshTimer?.Dispose();
		_pluginToolsRefreshGate.Dispose();
		_mcpRefreshGate.Dispose();
		_lifetimeCts.Dispose();
	}

	private void TrackTask(Task task)
	{
		_backgroundTasks.TryAdd(task, 0);
		_ = task.ContinueWith(
			completed => _backgroundTasks.TryRemove(completed, out _),
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private Task TrackBackground(Func<Task> operation, string name)
	{
		Task task = ObserveBackgroundAsync(operation, name);
		TrackTask(task);
		return task;
	}

	private async Task InitializeKnowledgeAsync()
	{
		Knowledge.EnsureDefaultFile();
		Knowledge.StartWatcher();
		await Knowledge.ReindexAsync(_lifetimeCts.Token).ConfigureAwait(false);
	}

	/// <summary>Embedding 配置变化后重新检查知识和个人记忆向量。</summary>
	public void QueueEmbeddingRebuild()
	{
		TrackBackground(() => Knowledge.ReindexAsync(_lifetimeCts.Token), "Memory.md embedding rebuild");
		TrackBackground(() => Memory.ReembedAllAsync(_lifetimeCts.Token, false), "memory embedding rebuild");
		InvalidateSnapshot("memory", "embedding");
	}

	private async Task RunMemoryMaintenanceAsync()
	{
		while (!_lifetimeCts.IsCancellationRequested)
		{
			int changed = Lifecycle.RunOnce();
			if (changed > 0) InvalidateSnapshot("memory");
			try { await Task.Delay(TimeSpan.FromHours(6), _lifetimeCts.Token).ConfigureAwait(false); }
			catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { break; }
		}
	}

	private async Task ObserveBackgroundAsync(Func<Task> operation, string name)
	{
		try { await operation().ConfigureAwait(false); }
		catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
		catch (Exception exception)
		{
			try
			{
				Services.Telemetry.CaptureException(exception, "runtime.background_task");
				Services.Logger.Write(LogSource.Backend, "warn", $"{name} failed: {SensitiveDataRedactor.ExceptionSummary(exception)}");
			}
			catch { }
		}
	}

	private static async Task WaitBoundedAsync(IReadOnlyCollection<Task> tasks, TimeSpan timeout)
	{
		if (tasks.Count == 0) return;
		Task all = Task.WhenAll(tasks);
		await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false);
	}

	/// <summary>活动 Agent 会话状态</summary>
	private sealed class AgentSessionState(string sourceLabel) : IDisposable
	{
		public string SourceLabel { get; } = sourceLabel;

		public CancellationTokenSource Cts { get; } = new();

		public Task? Worker { get; set; }

		public void Dispose()
		{
			Cts.Dispose();
		}
	}

	/// <summary>待决桌面视觉授权请求；只保存动作种类和任务标识。</summary>
	private sealed class PendingDesktopApproval(AutomationApprovalRequest request, TaskCompletionSource<bool> tcs) : IDisposable
	{
		public AutomationApprovalRequest Request { get; } = request;
		public TaskCompletionSource<bool> Tcs { get; } = tcs;

		private System.Threading.Timer? _timeout;

		public void ArmTimeout(int seconds, Action onExpired)
		{
			_timeout = new System.Threading.Timer(_ => onExpired(), null, seconds * 1000, Timeout.Infinite);
		}

		public void Dispose()
		{
			_timeout?.Dispose();
			_timeout = null;
		}
	}

	/// <summary>待决授权请求</summary>
	private sealed class PendingApproval(string requestId, string sourceLabel, string sessionId, TaskCompletionSource<bool> tcs) : IDisposable
	{
		public string RequestId { get; } = requestId;
		public string SourceLabel { get; } = sourceLabel;
		public string SessionId { get; } = sessionId;
		public TaskCompletionSource<bool> Tcs { get; } = tcs;

		private System.Threading.Timer? _timeout;

		/// <summary>启动超时定时器; 触发时执行回调 (fail-closed)</summary>
		public void ArmTimeout(int seconds, Action onExpired)
		{
			_timeout = new System.Threading.Timer(_ => onExpired(), null, seconds * 1000, Timeout.Infinite);
		}

		public void Dispose()
		{
			_timeout?.Dispose();
			_timeout = null;
		}
	}
}
