using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Live2DCSharpSDK.App;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.Framework.Math;
using Live2DCSharpSDK.Framework.Motion;
using Live2DCSharpSDK.OpenGL;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Live2D;
using Nori.Core.Logging;
using Nori.Core.Resources;
using Nori.Desktop.Bridge;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Live2D.Behaviors;

namespace Nori.Desktop.Live2D;

/// <summary>
/// 原生 Live2D 桌宠运行时
///
/// 负责模型生命周期、OpenGL 渲染调度、行为管线协调以及外部命令响应。
/// </summary>
public sealed class PetRuntime
{
	private readonly AppServices _services;
	private readonly BehaviorPipeline _pipeline = new();
	private readonly ExpressionStore _expressionStore = new();
	private readonly ExpressionBehavior _expressionBehavior;
	private readonly AutoBlinkBehavior _autoBlink = new();
	private readonly EyeFocusBehavior _eyeFocus = new();
	private readonly IdleDisableBehavior _idleDisable = new();
	private readonly BeatSyncBehavior _beatSync = new();
	private readonly LipSyncBehavior _lipSync = new();
	private readonly ModelParameters _modelParams = new();
	/// <summary>行为上下文逐帧复用, 避免渲染热路径上每帧分配</summary>
	private readonly BehaviorContext _behaviorContext = new();
	private readonly Random _random = new();
	private readonly Lock _qualityGate = new();
	private readonly Lock _metricsGate = new();
	private Live2DRenderSettings _renderSettings = Live2DRenderSettings.Normalize("arg-nori");
	private RenderQualityPolicy _qualityPolicy = new(
		Live2DRenderSettings.Normalize("arg-nori"),
		Live2DPowerSource.Ac);
	private readonly Queue<double> _frameTimesMs = new();
	private readonly Queue<double> _maskTimesMs = new();
	private const int MetricsWindowSize = 120;
	private int _droppedFrames;
	private bool _shadowApplied;
	private bool _offscreenRendering;
	private bool _renderVisible;
	private int _appliedMaskBufferSize;

	private LAppDelegateOpenGL? _app;
	private AvaloniaGlApi? _gl;
	private LAppModel? _currentModel;
	private string _currentModelId = "arg-nori";
	private string _currentModelDir = "";
	private List<MotionGroupInfo> _motionGroups = [];
	private readonly CubismMatrix44 _projectionMatrix = new();

	private double _lastUpdateTime;
	private double _lastTapTime;
	private readonly Lock _interactionGate = new();
	private PetInteractionConfig _interactionConfig = PetInteractionConfig.Empty;
	private PetViewportMapping? _viewportMapping;

	// ---- 后台模型准备 (世代归属) ----
	//
	// 模型的加载与销毁都要创建/释放 GL 纹理与着色器, 必须在 OpenGL 上下文 current 的时候做。
	// Avalonia 只在 OnOpenGlInit / OnOpenGlRender / OnOpenGlDeinit 里让上下文 current,
	// 所以磁盘扫描/JSON 解析全部放到后台 Prepare 任务; RenderFrame 每帧只观察任务,
	// 仅当"完成 + 未取消 + 世代仍匹配"时才在 GL 区做最小资源交换。
	private readonly Lock _prepareGate = new();
	private long _modelGeneration;
	private ModelLoadOperation? _pendingModelLoad;

	// 配置项
	public float UserScale { get; set; } = 1.0f;
	public float Opacity { get; set; } = 1.0f;
	public bool AutoBlinkEnabled { get; set; } = true;
	public bool EyeTrackingEnabled { get; set; } = true;
	public bool IdleEyeAnimationEnabled { get; set; } = true;
	public bool IdleAnimationEnabled { get; set; } = true;
	public bool ExpressionEnabled { get; set; } = true;
	public bool ShadowEnabled { get; set; } = true;
	public bool LipSyncEnabled { get; set; } = true;
	public bool BeatSyncEnabled { get; set; }
	public bool ClickInteraction { get; set; } = true;
	public bool ClickThroughEnabled { get; set; }
	public float RenderScale { get; private set; } = Live2DRenderSettings.DefaultRenderScale;
	public string QualityMode { get; private set; } = Live2DRenderSettings.DefaultQualityMode;
	public int MaxFps { get; private set; }

	/// <summary>当前质量策略的有效目标 FPS。</summary>
	public int EffectiveFps
	{
		get { lock (_qualityGate) return _qualityPolicy.Current.EffectiveFps; }
	}

	/// <summary>当前质量策略的有效渲染倍率。</summary>
	public float EffectiveRenderScale
	{
		get { lock (_qualityGate) return _qualityPolicy.Current.EffectiveRenderScale; }
	}

	/// <summary>当前质量策略快照。</summary>
	public RenderQualityDecision QualityDecision
	{
		get { lock (_qualityGate) return _qualityPolicy.Current; }
	}

	public event Action? ModelChanged;
	public event Action? ModelLoadRequested;
	public event Action? ModelLoadFailed;
	public event Action? FrameRendered;
	public event Action<PetInteractionTrigger>? InteractionTriggered;

	/// <summary>当前模型加载世代, AI 结果应用前用它判断是否已经切换模型。</summary>
	public long ModelGeneration => Volatile.Read(ref _modelGeneration);

	/// <summary>当前模型的自定义互动配置快照。</summary>
	public PetInteractionConfig InteractionConfig
	{
		get
		{
			lock (_interactionGate) return _interactionConfig;
		}
	}

	/// <summary>缩放变化: 桌宠窗口据此重算窗口尺寸</summary>
	public event Action? LayoutChanged;

	public PetRuntime(AppServices services)
	{
		_services = services;
		_expressionBehavior = new ExpressionBehavior(_expressionStore);

		// 注册行为插件（pre / post / final）
		_pipeline.Register(_idleDisable, PipelineStage.Pre);
		_pipeline.Register(_beatSync, PipelineStage.Pre);
		_pipeline.Register(_eyeFocus, PipelineStage.Post);
		_pipeline.Register(_expressionBehavior, PipelineStage.Final);
		_pipeline.Register(_autoBlink, PipelineStage.Final);
		_pipeline.Register(_lipSync, PipelineStage.Final);
	}

	public LAppModel? CurrentModel => _currentModel;
	public string CurrentModelId => _currentModelId;
	public string? LastModelLoadError { get; private set; }
	public IReadOnlyList<MotionGroupInfo> MotionGroups => _motionGroups;
	public IReadOnlyList<string> Expressions => _expressionStore.AllGroupNames().Count > 0
		? _expressionStore.AllGroupNames()
		: _expressionStore.AllNames();

	/// <summary>给快照与诊断使用的滚动渲染指标。</summary>
	public PetRenderMetrics RenderMetrics
	{
		get
		{
			lock (_metricsGate)
			{
				RenderQualityDecision quality = QualityDecision;
				return new PetRenderMetrics
				{
					EffectiveQuality = quality.EffectiveQuality,
					EffectiveFps = quality.EffectiveFps,
					EffectiveRenderScale = quality.EffectiveRenderScale,
					FrameTimeP95Ms = Percentile(_frameTimesMs, 0.95),
					MaskCostP95Ms = Percentile(_maskTimesMs, 0.95),
					DroppedFrames = _droppedFrames,
					ShadowRequested = ShadowEnabled,
					ShadowApplied = _shadowApplied,
					OffscreenRendering = _offscreenRendering,
					Visible = _renderVisible,
				};
			}
		}
	}

	/// <summary>
	/// Cubism SDK 日志落到应用自己的文件日志
	///
	/// 绝不能用 Console.WriteLine: 宿主是 WinExe 没有控制台, 写控制台会抛 IOException,
	/// 而 Cubism 的日志是在渲染回调里发出的, 抛出去就是进程崩溃。
	/// </summary>
	public void WriteCubismLog(string message)
	{
		try
		{
			_services.Logger.Write(LogSource.Backend, "warn", $"[Live2D] {message}");
		}
		catch
		{
			// 日志本身失败时保持静默, 绝不允许把异常带回渲染线程
		}
	}

	/// <summary>记录渲染线程的帧耗时与命中掩码耗时。</summary>
	public void RecordRenderMetrics(double frameTimeMs, double maskCostMs)
	{
		if (frameTimeMs > 0 && double.IsFinite(frameTimeMs))
		{
			lock (_metricsGate) AddMetric(_frameTimesMs, frameTimeMs);
			lock (_qualityGate) _qualityPolicy.ObserveFrame(frameTimeMs);
		}
		if (maskCostMs > 0 && double.IsFinite(maskCostMs))
		{
			lock (_metricsGate) AddMetric(_maskTimesMs, maskCostMs);
		}
	}

	/// <summary>记录调度器因已有请求未完成而跳过的帧。</summary>
	public void RecordDroppedFrame()
	{
		lock (_metricsGate) _droppedFrames++;
	}

	/// <summary>记录当前渲染后端能力，失败时不把配置伪装成已生效。</summary>
	public void SetRenderSurfaceState(bool offscreenRendering, bool shadowApplied, bool visible)
	{
		lock (_metricsGate)
		{
			_offscreenRendering = offscreenRendering;
			_shadowApplied = shadowApplied;
			_renderVisible = visible;
		}
	}

	private static void AddMetric(Queue<double> values, double value)
	{
		values.Enqueue(value);
		while (values.Count > MetricsWindowSize) values.Dequeue();
	}

	private static double Percentile(IEnumerable<double> values, double percentile)
	{
		double[] ordered = values.OrderBy(value => value).ToArray();
		if (ordered.Length == 0) return 0;
		int index = Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1);
		return Math.Round(ordered[index], 2);
	}

	public void OnGlInit(LAppDelegateOpenGL app, AvaloniaGlApi gl)
	{
		_app = app;
		_gl = gl;
		_services.Logger.Write(LogSource.Backend, "info", "Live2D OpenGL 初始化完成");
		string savedModel = _services.Config.GetStringOr("selected_model", "arg-nori");
		if (!string.IsNullOrWhiteSpace(savedModel)) _currentModelId = savedModel.Trim();
		LoadConfigs();
		if (_services.SafeMode)
		{
			LastModelLoadError = "安全模式已禁用桌宠模型自动加载";
			return;
		}
		RequestModelLoad(_currentModelId);
	}

	public void OnGlDeinit()
	{
		lock (_prepareGate) CancelPendingModelLoadLocked();
		// 同上: 释放交给 manager, PetGlControl 随后的 _lapp.Dispose() 会走到 ReleaseAllModel()
		_currentModel = null;
		lock (_interactionGate) _viewportMapping = null;
		_app?.Live2dManager.ReleaseAllModel();
		_app = null;
		_gl = null;
	}

	public void LoadConfigs()
	{
		float userScale = ParseFloatConfig($"l2d_scale_{_currentModelId}", ParseFloatConfig("l2d_scale", 1.0f));
		float opacity = ParseFloatConfig(ModelConfigKey("l2d_opacity"), ParseFloatConfig("l2d_opacity", Live2DRenderSettings.DefaultOpacity));
		bool shadow = ParseBoolConfig(ModelConfigKey("l2d_shadow"), ParseBoolConfig("l2d_shadow", true));
		float renderScale = ParseFloatConfig(ModelConfigKey("l2d_render_scale"), ParseFloatConfig("l2d_render_scale", Live2DRenderSettings.DefaultRenderScale));
		string qualityMode = ReadModelConfig("l2d_quality_mode", ReadConfig("l2d_quality_mode", Live2DRenderSettings.DefaultQualityMode));
		int maxFps = (int)ParseFloatConfig(ModelConfigKey("l2d_max_fps"), ParseFloatConfig("l2d_max_fps", Live2DRenderSettings.DefaultMaxFps));

		Live2DRenderSettings settings = Live2DRenderSettings.Normalize(
			_currentModelId,
			opacity,
			shadow,
			renderScale,
			qualityMode,
			maxFps);
		_renderSettings = settings;
		UserScale = Math.Clamp(userScale, 0.1f, 4.0f);
		Opacity = settings.Opacity;
		ShadowEnabled = settings.ShadowEnabled;
		RenderScale = settings.RenderScale;
		QualityMode = Live2DRenderSettings.QualityModeToStorage(settings.QualityMode);
		MaxFps = settings.MaxFps;
		lock (_qualityGate) _qualityPolicy.Update(settings, PowerSourceDetector.Detect());

		AutoBlinkEnabled = ParseBoolConfig("l2d_auto_blink", true);
		EyeTrackingEnabled = ParseBoolConfig("l2d_eye_tracking", true);
		IdleEyeAnimationEnabled = ParseBoolConfig("l2d_idle_eye_animation", true);
		IdleAnimationEnabled = ParseBoolConfig("l2d_idle_animation", true);
		ExpressionEnabled = ParseBoolConfig("l2d_expression_enabled", true);
		LipSyncEnabled = ParseBoolConfig("l2d_lip_sync", true);
		BeatSyncEnabled = ParseBoolConfig("l2d_beat_sync", false);
		ClickInteraction = ParseBoolConfig("l2d_click_interaction", true);
		ClickThroughEnabled = ParseBoolConfig("l2d_click_through", false);
		LoadInteractionConfig();
	}

	/// <summary>读取当前模型的自定义互动配置；损坏配置按空配置处理。</summary>
	private void LoadInteractionConfig()
	{
		PetInteractionConfig config = PetInteractionConfig.Empty;
		if (_services.Config.Get(PetInteractionConfig.StorageKey(_currentModelId)) is ConfigValue.Json {Value: JsonNode node})
		{
			try
			{
				config = PetInteractionConfig.Parse(node.ToJsonString(PetInteractionJson.Options));
			}
			catch (Exception exception)
			{
				WriteCubismLog($"互动配置无效 [{_currentModelId}]: {exception.Message}");
			}
		}
		lock (_interactionGate) _interactionConfig = config;
	}

	/// <summary>桥接保存配置后的当前模型热应用。</summary>
	public void SetInteractionConfig(string modelId, PetInteractionConfig config)
	{
		if (!modelId.Equals(_currentModelId, StringComparison.OrdinalIgnoreCase)) return;
		config.Validate();
		lock (_interactionGate) _interactionConfig = config;
	}

	/// <summary>
	/// 读数值配置
	///
	/// ConfigValue 是 record, 直接 ToString() 会得到 "Integer { Value = 30 }" 这种调试字符串,
	/// 必须走 AsStringOr. 另外 ConfigStore 读取时会重新推断类型, 存进去的 "1" / "0" 会变成
	/// 布尔再还原成 "true" / "false", 所以这里要一并接住 (与前端 parseNumber 的处境相同).
	/// </summary>
	private string ModelConfigKey(string baseKey) => $"{baseKey}_{_currentModelId}";

	private string ReadConfig(string key, string fallback) => _services.Config.GetStringOr(key, fallback);

	private string ReadModelConfig(string baseKey, string fallback)
	{
		string modelValue = _services.Config.GetStringOr(ModelConfigKey(baseKey), "");
		return modelValue.Length > 0 ? modelValue : fallback;
	}

	private float ParseFloatConfig(string key, float fallback)
	{
		string raw = _services.Config.GetStringOr(key, "");
		if (raw.Length == 0) return fallback;
		if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return 1.0f;
		if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return 0.0f;
		return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
	}

	/// <summary>
	/// 读布尔配置 (同样要走 AsStringOr, 并接住 "1" / "0")
	/// </summary>
	private bool ParseBoolConfig(string key, bool fallback)
	{
		string raw = _services.Config.GetStringOr(key, "");
		return ParseBool(raw) ?? fallback;
	}

	/// <summary>
	/// 解析布尔文本: 与前端 parseBoolean 同口径
	/// </summary>
	private static bool? ParseBool(string raw) => raw switch
	{
		"1" => true,
		"0" => false,
		_ when raw.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
		_ when raw.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
		_ => null,
	};

	/// <summary>
	/// 解析数值文本: 与 ParseFloatConfig 同口径, 供配置热更新使用
	/// </summary>
	private static float? ParseFloat(string raw)
	{
		if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return 1.0f;
		if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return 0.0f;
		return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : null;
	}

	/// <summary>
	/// 请求切换模型 (线程安全): 递增世代并在后台开始元数据准备.
	///
	/// 上一份准备任务的 CTS 立即取消; 渲染帧只消费当前操作的完成结果。
	/// </summary>
	public void RequestModelLoad(string modelId) => RequestModelLoad(modelId, reloadCurrent: true);

	private void RequestModelLoad(string modelId, bool reloadCurrent)
	{
		string requestedModelId = modelId?.Trim() ?? "";
		string? normalized = SupportedModelIds.Normalize(requestedModelId);
		bool notifyRequested = false;

		lock (_prepareGate)
		{
			bool sameCurrentModel = normalized is not null
				&& _currentModel is not null
				&& string.Equals(normalized, _currentModelId, StringComparison.Ordinal);
			if (sameCurrentModel && _pendingModelLoad is null && !reloadCurrent)
			{
				return;
			}
			if (sameCurrentModel && _pendingModelLoad is not null)
			{
				// 重新选中当前模型只取消待处理切换, 不把已经显示的模型再加载一遍。
				_modelGeneration++;
				CancelPendingModelLoadLocked();
				LastModelLoadError = null;
				notifyRequested = true;
			}
			else
			{
				_modelGeneration++;
				long generation = _modelGeneration;
				string? fallbackModelId = _currentModel is null ? null : _currentModelId;
				CancelPendingModelLoadLocked();
				LastModelLoadError = null;

				CancellationTokenSource cancellation = new();
				Task<ModelLoadOutcome> preparationTask;
				if (normalized is null)
				{
					preparationTask = Task.FromResult(ModelLoadOutcome.Failed(
						new ResourceException($"不支持的 Live2D 模型 ID: {requestedModelId}")));
				}
				else
				{
					string modelDir = _services.Resources.ResourceDir(ResourceType.Live2D, normalized);
					preparationTask = Task.Run(
						() => PrepareModelOutcomeAsync(normalized, modelDir, generation, cancellation.Token),
						CancellationToken.None);
				}

				_pendingModelLoad = new ModelLoadOperation(
					generation,
					normalized ?? requestedModelId,
					fallbackModelId,
					cancellation,
					preparationTask);
				ObservePreparationTask(preparationTask);
				notifyRequested = true;
			}
		}

		if (notifyRequested) NotifyModelLoadRequested();
	}

	private static async Task<ModelLoadOutcome> PrepareModelOutcomeAsync(
		string modelId,
		string modelDir,
		long generation,
		CancellationToken cancellationToken)
	{
		try
		{
			PreparedModel prepared = await ModelPreparation.PrepareAsync(
				modelId,
				modelDir,
				generation,
				cancellationToken).ConfigureAwait(false);
			return ModelLoadOutcome.Succeeded(prepared);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return ModelLoadOutcome.Canceled();
		}
		catch (Exception exception)
		{
			// 后台只返回结果, 不直接触碰配置、运行时状态或事件。
			return ModelLoadOutcome.Failed(exception);
		}
	}

	private static void ObservePreparationTask(Task<ModelLoadOutcome> task)
	{
		_ = task.ContinueWith(
			completed => _ = completed.Exception,
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	/// <summary>
	/// 在渲染帧观察准备任务; 只有当前操作完成后才在 GL 区消费结果。
	/// </summary>
	private void ConsumePreparedIfReady()
	{
		ModelLoadOperation? pending;
		ModelLoadOutcome outcome;
		lock (_prepareGate)
		{
			pending = _pendingModelLoad;
			if (pending is null || !pending.PreparationTask.IsCompleted) return;
			try
			{
				// 无论后台实现是否意外 fault, 这里都显式观察任务异常。
				outcome = pending.PreparationTask.GetAwaiter().GetResult();
			}
			catch (Exception exception)
			{
				outcome = ModelLoadOutcome.Failed(exception);
			}
		}

		ModelLoadOperation operation = pending;
		if (outcome.IsCanceled)
		{
			lock (_prepareGate)
			{
				if (ReferenceEquals(_pendingModelLoad, operation)) CompletePendingModelLoadLocked(operation);
			}
			return;
		}
		if (outcome.Error is { } error)
		{
			CommitModelLoadFailure(operation, error);
			return;
		}
		if (outcome.Prepared is not { } prepared)
		{
			CommitModelLoadFailure(operation, new InvalidOperationException("Live2D 模型准备未返回结果"));
			return;
		}
		if (prepared.Generation != operation.Generation
			|| !string.Equals(prepared.ModelId, operation.ModelId, StringComparison.Ordinal))
		{
			CommitModelLoadFailure(operation, new InvalidOperationException("Live2D 模型准备世代不匹配"));
			return;
		}

		ApplyPreparedOnGlThread(operation, prepared);
	}

	private void CommitModelLoadFailure(ModelLoadOperation operation, Exception error)
	{
		bool committed;
		lock (_prepareGate) committed = CommitModelLoadFailureLocked(operation, error);
		if (!committed) return;

		try
		{
			_services.Logger.Write(LogSource.Backend, "error", $"加载 Live2D 模型失败 [{operation.ModelId}]: {error.Message}");
		}
		catch
		{
			// 日志失败保持静默, 不能掩盖模型加载失败。
		}
		NotifyModelLoadFailed();
	}

	private bool CommitModelLoadFailureLocked(ModelLoadOperation operation, Exception error)
	{
		if (!IsCurrentOperationLocked(operation)) return false;
		CompletePendingModelLoadLocked(operation);

		if (!string.IsNullOrWhiteSpace(operation.FallbackModelId)
			&& !string.Equals(operation.ModelId, operation.FallbackModelId, StringComparison.Ordinal))
		{
			try
			{
				_services.Config.Set(
					ConfigStore.KeySelectedModel,
					new ConfigValue.Text(operation.FallbackModelId));
			}
			catch
			{
				// 退出期间数据库可能已释放, 不掩盖模型加载失败。
			}
		}

		LastModelLoadError = error is ResourceException
			? $"模型 {operation.ModelId} 加载失败: {error.Message}"
			: $"模型 {operation.ModelId} 加载失败, 请重新导入";
		return true;
	}

	private void NotifyModelLoadRequested()
	{
		try { ModelLoadRequested?.Invoke(); }
		catch (Exception exception) { WriteCubismLog($"模型切换取消互动请求失败: {exception.Message}"); }
	}

	private void NotifyModelLoadFailed()
	{
		try { ModelLoadFailed?.Invoke(); }
		catch (Exception exception) { WriteCubismLog($"模型失败事件处理异常: {exception.Message}"); }
	}

	private void CancelPendingModelLoadLocked()
	{
		if (_pendingModelLoad is not { } operation) return;
		_pendingModelLoad = null;
		operation.Invalidate();
		try { operation.Cancellation.Cancel(); }
		catch (ObjectDisposedException) { }
		operation.Cancellation.Dispose();
	}

	private void CompletePendingModelLoadLocked(ModelLoadOperation operation)
	{
		if (!ReferenceEquals(_pendingModelLoad, operation)) return;
		operation.Complete();
		_pendingModelLoad = null;
		operation.Cancellation.Dispose();
	}

	private bool IsCurrentOperationLocked(ModelLoadOperation operation) =>
		ReferenceEquals(_pendingModelLoad, operation) && operation.IsCurrent(_modelGeneration);

	/// <summary>
	/// GL 线程专属: 候选模型可先创建, 但提交前必须再次校验操作世代;
	/// 过期候选只移除, 不得改变当前模型或持久化选择。
	/// </summary>
	private void ApplyPreparedOnGlThread(ModelLoadOperation operation, PreparedModel prepared)
	{
		LAppDelegateOpenGL? app = _app;
		if (app is null || _gl is null) return;
		lock (_prepareGate)
		{
			if (!IsCurrentOperationLocked(operation)) return;
		}

		if (!Directory.Exists(prepared.ModelDir))
		{
			CommitModelLoadFailure(operation, new ResourceException($"模型目录不存在: {prepared.ModelDir}"));
			return;
		}

		LAppModel candidate;
		try
		{
			// LoadModel 仅在构造完全成功后才加入 manager; 旧模型在此期间继续存活。
			candidate = app.Live2dManager.LoadModel(prepared.ModelDir, prepared.Model3FileName);
		}
		catch (Exception exception)
		{
			CommitModelLoadFailure(operation, exception);
			return;
		}

		LAppModel? previousModel = null;
		bool committed = false;
		bool stale = false;
		Exception? failure = null;
		lock (_prepareGate)
		{
			if (!IsCurrentOperationLocked(operation))
			{
				stale = true;
			}
			else
			{
				previousModel = _currentModel;
				string previousModelId = _currentModelId;
				string previousModelDir = _currentModelDir;
				List<MotionGroupInfo> previousMotionGroups = [.. _motionGroups];
				bool firstLoadOfModel = previousModel is null
					|| !string.Equals(prepared.ModelId, previousModelId, StringComparison.Ordinal);
				float previousUserScale = UserScale;
				float previousOpacity = Opacity;
				bool previousAutoBlinkEnabled = AutoBlinkEnabled;
				bool previousEyeTrackingEnabled = EyeTrackingEnabled;
				bool previousIdleEyeAnimationEnabled = IdleEyeAnimationEnabled;
				bool previousIdleAnimationEnabled = IdleAnimationEnabled;
				bool previousExpressionEnabled = ExpressionEnabled;
				bool previousShadowEnabled = ShadowEnabled;
				bool previousLipSyncEnabled = LipSyncEnabled;
				bool previousBeatSyncEnabled = BeatSyncEnabled;
				bool previousClickInteraction = ClickInteraction;
				bool previousClickThroughEnabled = ClickThroughEnabled;
				float previousRenderScale = RenderScale;
				string previousQualityMode = QualityMode;
				int previousMaxFps = MaxFps;
				Live2DRenderSettings previousRenderSettings = _renderSettings;
				PetInteractionConfig previousInteraction;
				lock (_interactionGate) previousInteraction = _interactionConfig;
				string previousExpressionModelId = _expressionStore.ModelId;
				ExpressionEntry[] previousExpressions = [.. _expressionStore.Expressions.Values];
				ExpressionGroupDefinition[] previousExpressionGroups = [.. _expressionStore.ExpressionGroups.Values];

				try
				{
					_currentModel = candidate;
					_currentModelId = prepared.ModelId;
					_currentModelDir = prepared.ModelDir;
					candidate.CustomValueUpdate = true;
					candidate.ValueUpdate = OnModelValueUpdate;
					candidate.FinalValueUpdate = OnModelFinalValueUpdate;

					// UseHighPrecisionMask 必须保持关闭: 打开后 SDK 会对每一个被蒙版裁剪的部件
					// 单独把整张蒙版缓冲清空并重画一遍, 质量策略只调整缓冲尺寸与过滤等级。
					_appliedMaskBufferSize = 0;
					ApplyRenderQualityOnGlThread();
					_expressionBehavior.ApplyPrepared(prepared, candidate.Model);
					_motionGroups = [.. prepared.MotionGroups];
					if (firstLoadOfModel) LoadConfigs();

					// 候选模型已经可用后才释放旧对象; 旧对象清理异常不能反向销毁新模型。
					if (previousModel is not null)
					{
						try { app.Live2dManager.RemoveModel(previousModel); }
						catch (Exception exception) { WriteCubismLog($"释放旧模型失败: {exception.Message}"); }
					}
					lock (_interactionGate) _viewportMapping = null;
					LastModelLoadError = null;
					CompletePendingModelLoadLocked(operation);
					committed = true;
				}
				catch (Exception exception)
				{
					failure = exception;
					_currentModel = previousModel;
					_currentModelId = previousModelId;
					_currentModelDir = previousModelDir;
					_motionGroups = previousMotionGroups;
					UserScale = previousUserScale;
					Opacity = previousOpacity;
					AutoBlinkEnabled = previousAutoBlinkEnabled;
					EyeTrackingEnabled = previousEyeTrackingEnabled;
					IdleEyeAnimationEnabled = previousIdleEyeAnimationEnabled;
					IdleAnimationEnabled = previousIdleAnimationEnabled;
					ExpressionEnabled = previousExpressionEnabled;
					ShadowEnabled = previousShadowEnabled;
					LipSyncEnabled = previousLipSyncEnabled;
					BeatSyncEnabled = previousBeatSyncEnabled;
					ClickInteraction = previousClickInteraction;
					ClickThroughEnabled = previousClickThroughEnabled;
					RenderScale = previousRenderScale;
					QualityMode = previousQualityMode;
					MaxFps = previousMaxFps;
					_renderSettings = previousRenderSettings;
					lock (_qualityGate) _qualityPolicy.Update(previousRenderSettings, PowerSourceDetector.Detect());
					lock (_interactionGate) _interactionConfig = previousInteraction;
					_expressionStore.RegisterExpressions(
						previousExpressionModelId,
						previousExpressionGroups,
						previousExpressions);
					_appliedMaskBufferSize = 0;
					if (previousModel is not null)
					{
						try { ApplyRenderQualityOnGlThread(); } catch { }
					}
				}
			}
		}

		if (stale)
		{
			RemoveCandidateModel(app, candidate);
			return;
		}
		if (!committed)
		{
			RemoveCandidateModel(app, candidate);
			if (failure is not null) CommitModelLoadFailure(operation, failure);
			return;
		}

		try { _services.Logger.Write(LogSource.Backend, "info", $"成功加载 Live2D 模型: {prepared.ModelId}"); } catch { }
		try { ModelChanged?.Invoke(); }
		catch (Exception exception) { WriteCubismLog($"模型变更事件处理异常: {exception.Message}"); }
	}

	private void RemoveCandidateModel(LAppDelegateOpenGL app, LAppModel candidate)
	{
		try { app.Live2dManager.RemoveModel(candidate); }
		catch (Exception exception) { WriteCubismLog($"释放过期 Live2D 候选模型失败: {exception.Message}"); }
	}

	/// <summary>在 GL 上下文中应用质量策略到 Cubism renderer。</summary>
	public void ApplyRenderQualityOnGlThread()
	{
		if (_currentModel?.Renderer is not CubismRenderer_OpenGLES2 renderer) return;
		RenderQualityDecision decision = QualityDecision;
		int maskSize = decision.QualityLevel switch
		{
			>= 2 => 2048,
			1 => 1536,
			_ => 1024,
		};
		maskSize = Math.Clamp((int)Math.Round(maskSize * Math.Min(1.0f, decision.EffectiveRenderScale)), 512, 2048);
		if (_appliedMaskBufferSize != maskSize)
		{
			renderer.SetClippingMaskBufferSize(maskSize, maskSize);
			_appliedMaskBufferSize = maskSize;
		}
		renderer.Anisotropy = decision.QualityLevel >= 2 ? 16.0f : decision.QualityLevel == 1 ? 8.0f : 4.0f;
		renderer.UseHighPrecisionMask = false;
		renderer.SetModelColor(1.0f, 1.0f, 1.0f, Opacity);
	}

	private void OnModelValueUpdate(LAppModel model)
	{
		double now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
		double timeDelta = _lastUpdateTime > 0 ? now - _lastUpdateTime : 0.016;
		_lastUpdateTime = now;

		// "空闲" = 没有动作在播, 或播的是待机组。LAppModel.Update() 会在动作播完时
		// 自动补一个随机 Idle, 所以只判 IsMotionFinished() 会让 isIdleMotion 几乎永远为 false,
		// 自动眨眼 / 空闲眼神微动 / idle-disable 三个行为都不会触发。
		bool isIdleMotion = model.IsMotionFinished()
			|| string.Equals(model.CurrentMotionGroup, LAppDefine.MotionGroupIdle, StringComparison.OrdinalIgnoreCase);

		var ctx = _behaviorContext;
		ctx.ResetFrame();
		ctx.Model = model;
		ctx.Now = now;
		ctx.TimeDelta = timeDelta;
		ctx.IsIdleMotion = isIdleMotion;
		ctx.AutoBlinkEnabled = AutoBlinkEnabled;
		ctx.EyeTrackingEnabled = EyeTrackingEnabled;
		ctx.IdleEyeAnimationEnabled = IdleEyeAnimationEnabled;
		ctx.IdleAnimationEnabled = IdleAnimationEnabled;
		ctx.ForceIdleEyeAnimation = IdleEyeAnimationEnabled;
		ctx.BeatSyncEnabled = BeatSyncEnabled;
		ctx.LipSyncEnabled = LipSyncEnabled;
		ctx.ExpressionEnabled = ExpressionEnabled;
		ctx.ClickInteraction = ClickInteraction;
		ctx.ModelParameters = _modelParams;

		// 运行 pre 插件（如 IdleDisable、BeatSync）
		_pipeline.RunPre(ctx);

		// 如果未短路且开启了眼部追踪，补回 SDK 拖拽角度
		if (EyeTrackingEnabled)
		{
			float dragX = model.DragX;
			float dragY = model.DragY;
			model.Model.AddParameterValue(model.IdParamAngleX, dragX * 30);
			model.Model.AddParameterValue(model.IdParamAngleY, dragY * 30);
			model.Model.AddParameterValue(model.IdParamAngleZ, dragX * dragY * -30);
			model.Model.AddParameterValue(model.IdParamBodyAngleX, dragX * 10);
			model.Model.AddParameterValue(model.IdParamEyeBallX, dragX);
			model.Model.AddParameterValue(model.IdParamEyeBallY, dragY);
		}

		// 运行 post 插件（如 EyeFocus 眼神微动）
		_pipeline.RunPost(ctx);
	}

	/// <summary>在 SDK 的物理、姿势等最终参数处理之后运行桌宠 Final 行为。</summary>
	private void OnModelFinalValueUpdate(LAppModel model)
	{
		if (!ReferenceEquals(model, _behaviorContext.Model)) return;
		_pipeline.RunFinal(_behaviorContext);
	}

	public void RenderFrame(float deltaTime, int viewportWidth, int viewportHeight)
	{
		if (_gl is null) return;

		// 此刻 OpenGL 上下文才是 current 的: 仅消费完成且世代匹配的准备结果,
		// 未完成或失败时继续渲染现有模型
		ConsumePreparedIfReady();

		if (_currentModel is null) return;
		ApplyRenderQualityOnGlThread();

		// LAppModel.Update() 读的是 LAppPal.DeltaTime。这里没有走 LAppDelegate.Run(),
		// 不显式写入的话它永远是 0, 动作队列 / 物理 / 呼吸会全部冻结在首帧。
		LAppPal.DeltaTime = deltaTime;

		// 投影: 先做纵横比校正, 再按需等比缩小到窗口内
		//
		// 模型没有 Layout 时 CubismModelMatrix 的构造函数会 SetHeight(2.0), 也就是把模型高度
		// 规范化成整个 NDC 高度, 宽度则是 2 x 模型宽高比。NDC 的 x 覆盖 viewportWidth、
		// y 覆盖 viewportHeight, 所以校正量只跟窗口有关: scaleX = viewportHeight / viewportWidth。
		// 之前这里乘的是 aspectModel / aspectWindow, 等于多乘了一个模型宽高比,
		// 竖长模型 (宽高比 < 1) 会被按这个比例横向压扁。
		_projectionMatrix.LoadIdentity();
		float canvasPixelW = _currentModel.Model.GetCanvasWidthPixel();
		float canvasPixelH = _currentModel.Model.GetCanvasHeightPixel();
		float canvasUnitW = _currentModel.Model.GetCanvasWidth();
		float canvasUnitH = _currentModel.Model.GetCanvasHeight();

		float aspectWindow = (float)viewportWidth / viewportHeight;
		float aspectModel = canvasPixelW > 0 && canvasPixelH > 0 ? canvasPixelW / canvasPixelH : 1.0f;

		float scaleX = (float)viewportHeight / viewportWidth;
		float scaleY = 1.0f;

		// 此时模型正好占满窗口高度; 只有模型比窗口更宽时才需要整体缩小,
		// 保证任何窗口比例下模型都完整可见 (安全基准尺寸的上下限收口会让两者略有出入)
		if (aspectModel > aspectWindow)
		{
			float fit = aspectWindow / aspectModel;
			scaleX *= fit;
			scaleY *= fit;
		}

		_projectionMatrix.Scale(scaleX, scaleY);
		PetViewportMapping mapping = PetViewportMapping.Create(
			viewportWidth,
			viewportHeight,
			// ModelMatrix 由 Cubism 的 Unit 画布尺寸构造；这里必须使用同一单位，
			// 不能传 Pixel 尺寸，否则 PixelsPerUnit 会把归一化点击压缩到画布中心。
			canvasUnitW,
			canvasUnitH,
			_currentModel.ModelMatrix.GetScaleX(),
			_currentModel.ModelMatrix.GetScaleY(),
			_currentModel.ModelMatrix.GetTranslateX(),
			_currentModel.ModelMatrix.GetTranslateY());
		lock (_interactionGate) _viewportMapping = mapping;

		_currentModel.RandomMotion = IdleAnimationEnabled;
		_currentModel.Update();
		_currentModel.Draw(_projectionMatrix);
		FrameRendered?.Invoke();
	}

	public void LookAt(float clientX, float clientY, float windowW, float windowH)
	{
		if (_currentModel is null || !EyeTrackingEnabled || windowW <= 0 || windowH <= 0) return;
		float normX = Math.Clamp((clientX / windowW) * 2.0f - 1.0f, -1.0f, 1.0f);
		float normY = Math.Clamp(-((clientY / windowH) * 2.0f - 1.0f), -1.0f, 1.0f);
		_currentModel.SetDragging(normX, normY);
	}

	public void HandleTap(float clientX, float clientY, float windowW, float windowH)
	{
		if (_currentModel is null || !ClickInteraction || windowW <= 0 || windowH <= 0) return;

		double now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
		if (now - _lastTapTime < 0.6) return;
		_lastTapTime = now;

		PetViewportMapping? mapping;
		PetInteractionConfig interactions;
		lock (_interactionGate)
		{
			mapping = _viewportMapping;
			interactions = _interactionConfig;
		}

		if (mapping is { } currentMapping
			&& currentMapping.TryMapClientToModel(clientX, clientY, out double modelX, out double modelY)
			&& PetInteractionResolver.TryResolve(interactions, modelX, modelY, out PetInteractionHit? hit)
			&& hit is not null)
		{
			if (hit.Region.ReactionMode == PetInteractionReactionMode.Ai)
			{
				PetInteractionTrigger trigger = new(_currentModelId, ModelGeneration, hit);
				Action<PetInteractionTrigger>? handler = InteractionTriggered;
				if (handler is null)
				{
					ApplyLocalInteraction(hit.Region);
				}
				else
				{
					try { handler(trigger); }
					catch (Exception exception)
					{
						WriteCubismLog($"AI 互动调度失败: {exception.Message}");
						ApplyLocalInteraction(hit.Region);
					}
				}
			}
			else
			{
				ApplyLocalInteraction(hit.Region);
			}
			return;
		}

		// 没有 HitAreas 的模型也会走这里: PetWindow 已经用 alpha 掩码确认点击的是模型像素。
		float normX = (clientX / windowW) * 2.0f - 1.0f;
		float normY = -((clientY / windowH) * 2.0f - 1.0f);
		if (_currentModel.HitTest(LAppDefine.HitAreaNameHead, normX, normY)
			&& ExpressionEnabled
			&& ToggleRandomExpression())
		{
			return;
		}
		PlayTapBodyOrRandomMotion();
	}

	/// <summary>执行一个区域配置的本地 Motion/Expression 反应。</summary>
	public void ApplyLocalInteraction(PetInteractionRegion region)
	{
		switch (region.Motion.Mode)
		{
			case PetInteractionActionMode.Random:
				PlayRandomMotion();
				break;
			case PetInteractionActionMode.Selected:
				PlayMotionExact(region.Motion.Group ?? "", region.Motion.Name ?? "");
				break;
		}

		if (!ExpressionEnabled) return;
		switch (region.Expression.Mode)
		{
			case PetInteractionActionMode.Random:
				ToggleRandomExpression();
				break;
			case PetInteractionActionMode.Selected:
				PlayExpression(region.Expression.Name ?? "");
				break;
		}
	}

	private bool PlayMotionExact(string group, string name)
	{
		MotionGroupInfo? matched = FindMotionGroup(group);
		if (matched is null) return false;
		int index = matched.Names.FindIndex(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
		return index >= 0 && TryStartMotion(matched.Group, index) is not null;
	}

	/// <summary>
	/// 播放点击互动动作。动作组按语义优先级选择，同组从随机位置开始逐项尝试。
	/// </summary>
	public bool PlayTapBodyOrRandomMotion()
	{
		foreach (MotionGroupInfo group in MotionSelector.GetInteractionCandidates(_motionGroups))
		{
			if (TryPlayGroup(group)) return true;
		}
		return false;
	}

	private bool TryPlayGroup(MotionGroupInfo group)
	{
		if (_currentModel is null || group.Names.Count == 0) return false;

		int start = _random.Next(group.Names.Count);
		for (int offset = 0; offset < group.Names.Count; offset++)
		{
			int index = (start + offset) % group.Names.Count;
			if (TryStartMotion(group.Group, index) is not null) return true;
		}
		return false;
	}

	private CubismMotionQueueEntry? TryStartMotion(string group, int index)
	{
		if (_currentModel is null) return null;
		try
		{
			return _currentModel.StartMotion(group, index, MotionPriority.PriorityForce);
		}
		catch (Exception ex)
		{
			WriteCubismLog($"动作播放异常 [{group}_{index}]: {ex.Message}");
			return null;
		}
	}

	private MotionGroupInfo? FindMotionGroup(string group)
	{
		if (string.IsNullOrWhiteSpace(group)) return null;
		return _motionGroups.FirstOrDefault(item => item.Group.Equals(group, StringComparison.OrdinalIgnoreCase));
	}

	public bool PlayMotionByName(string name)
	{
		if (_currentModel is null || string.IsNullOrWhiteSpace(name)) return false;
		string? resolved = PetActionResolver.ResolveMotion(_motionGroups, name);
		if (resolved is null) return false;
		foreach (MotionGroupInfo group in _motionGroups)
		{
			int index = group.Names.FindIndex(item => item.Equals(resolved, StringComparison.OrdinalIgnoreCase));
			if (index >= 0) return TryStartMotion(group.Group, index) is not null;
		}
		return false;
	}

	public bool PlayMotionByIndex(string group, int no)
	{
		MotionGroupInfo? matched = FindMotionGroup(group);
		if (matched is null || no < 0 || no >= matched.Names.Count) return false;
		return TryStartMotion(matched.Group, no) is not null;
	}

	public bool PlayRandomMotion()
	{
		IReadOnlyList<MotionGroupInfo> candidates = MotionSelector.GetInteractionCandidates(_motionGroups);
		if (candidates.Count == 0) return false;

		int start = _random.Next(candidates.Count);
		for (int offset = 0; offset < candidates.Count; offset++)
		{
			MotionGroupInfo group = candidates[(start + offset) % candidates.Count];
			if (TryPlayGroup(group)) return true;
		}
		return false;
	}

	public bool PlayExpression(string name)
	{
		string? resolved = PetActionResolver.ResolveExpression(Expressions, name);
		return resolved is not null && _expressionStore.Play(resolved);
	}
	public void StopExpression() => _expressionStore.Stop();
	public void ToggleExpression(string name) => _expressionStore.Toggle(name);

	public bool ToggleRandomExpression()
	{
		IReadOnlyList<string> names = _expressionStore.AllGroupNames();
		if (names.Count == 0) names = _expressionStore.AllNames();
		if (names.Count == 0) return false;

		string randomName = names[_random.Next(names.Count)];
		return _expressionStore.Toggle(randomName);
	}

	public void SetMouthOpen(float value, bool speaking)
	{
		_lipSync.SetMouthOpen(value);
		_lipSync.SetNowSpeaking(speaking);
	}

	public void TriggerBeat(double? timestamp = null)
	{
		if (BeatSyncEnabled) _beatSync.TriggerBeat(timestamp);
	}

	/// <summary>
	/// 配置删除后的运行时复位: 让桌宠回到该 key 的内置默认值, 与 set/delete 的状态转换对称
	/// </summary>
	public void ApplyConfigDelete(string key)
	{
		if (key == "selected_model")
		{
			RequestModelLoad(ConfigStore.DefaultModel, reloadCurrent: false);
			return;
		}
		if (key is "l2d_opacity" or "l2d_quality_mode" or "l2d_render_scale" or "l2d_max_fps" or "l2d_shadow"
			|| key == ModelConfigKey("l2d_opacity")
			|| key == ModelConfigKey("l2d_quality_mode")
			|| key == ModelConfigKey("l2d_render_scale")
			|| key == ModelConfigKey("l2d_max_fps")
			|| key == ModelConfigKey("l2d_shadow"))
		{
			LoadConfigs();
			return;
		}

		switch (key)
		{
			case "l2d_auto_blink": AutoBlinkEnabled = true; break;
			case "l2d_eye_tracking": EyeTrackingEnabled = true; break;
			case "l2d_idle_eye_animation": IdleEyeAnimationEnabled = true; break;
			case "l2d_idle_animation": IdleAnimationEnabled = true; break;
			case "l2d_expression_enabled": ExpressionEnabled = true; break;
			case "l2d_lip_sync": LipSyncEnabled = true; break;
			case "l2d_beat_sync": BeatSyncEnabled = false; break;
			case "l2d_click_interaction": ClickInteraction = true; break;
			case "l2d_click_through": ClickThroughEnabled = false; break;
			default: break;
		}

		// 缩放按模型存储 (l2d_scale_<modelId>), 兼容旧的全局键
		if (key == $"l2d_scale_{_currentModelId}" || key == "l2d_scale")
		{
			UserScale = 1.0f;
			LayoutChanged?.Invoke();
		}
	}

	/// <summary>
	/// 配置热更新
	///
	/// value 是 ConfigValue.ToStorage() 的结果: 布尔存成 "1" / "0", 所以必须走 ParseBool,
	/// 直接 bool.TryParse 会让所有开关的热更新静默失效.
	/// </summary>
	public void ApplyConfig(string key, string value)
	{
		if (key == "selected_model" && !string.IsNullOrWhiteSpace(value))
		{
			RequestModelLoad(value, reloadCurrent: false);
			return;
		}

		// 缩放按模型存储 (l2d_scale_<modelId>), 兼容旧的全局键
		if (key == $"l2d_scale_{_currentModelId}" || key == "l2d_scale")
		{
			if (ParseFloat(value) is { } scale)
			{
				UserScale = Math.Clamp(scale, 0.1f, 4.0f);
				LayoutChanged?.Invoke();
			}
			return;
		}

		if (key == "l2d_opacity" || key == ModelConfigKey("l2d_opacity"))
		{
			if (ParseFloat(value) is { } opacity)
			{
				Opacity = Math.Clamp(opacity, Live2DRenderSettings.MinOpacity, Live2DRenderSettings.MaxOpacity);
				RefreshRenderSettings();
			}
			return;
		}

		if (key == "l2d_render_scale" || key == ModelConfigKey("l2d_render_scale"))
		{
			if (ParseFloat(value) is { } scale)
			{
				RenderScale = Math.Clamp(scale, Live2DRenderSettings.MinRenderScale, Live2DRenderSettings.MaxRenderScale);
				RefreshRenderSettings();
			}
			return;
		}

		if (key == "l2d_quality_mode" || key == ModelConfigKey("l2d_quality_mode"))
		{
			QualityMode = Live2DRenderSettings.QualityModeToStorage(
				Live2DRenderSettings.ParseQualityMode(value) ?? Live2DQualityMode.Adaptive);
			RefreshRenderSettings();
			return;
		}

		if (key == "l2d_shadow" || key == ModelConfigKey("l2d_shadow"))
		{
			if (ParseBool(value) is { } shadow) ShadowEnabled = shadow;
			RefreshRenderSettings();
			return;
		}

		if (key == "l2d_max_fps" || key == ModelConfigKey("l2d_max_fps"))
		{
			if (ParseFloat(value) is { } fps)
			{
				MaxFps = Math.Clamp((int)fps, 0, Live2DRenderSettings.MaxExplicitFps);
				RefreshRenderSettings();
			}
			return;
		}

		switch (key)
		{
			case "l2d_auto_blink" when ParseBool(value) is { } v: AutoBlinkEnabled = v; break;
			case "l2d_eye_tracking" when ParseBool(value) is { } v: EyeTrackingEnabled = v; break;
			case "l2d_idle_eye_animation" when ParseBool(value) is { } v: IdleEyeAnimationEnabled = v; break;
			case "l2d_idle_animation" when ParseBool(value) is { } v: IdleAnimationEnabled = v; break;
			case "l2d_expression_enabled" when ParseBool(value) is { } v: ExpressionEnabled = v; break;
			case "l2d_lip_sync" when ParseBool(value) is { } v: LipSyncEnabled = v; break;
			case "l2d_beat_sync" when ParseBool(value) is { } v: BeatSyncEnabled = v; break;
			case "l2d_click_interaction" when ParseBool(value) is { } v: ClickInteraction = v; break;
			case "l2d_click_through" when ParseBool(value) is { } v: ClickThroughEnabled = v; break;
			default: break;
		}
	}

	private void RefreshRenderSettings()
	{
		_renderSettings = Live2DRenderSettings.Normalize(
			_currentModelId,
			Opacity,
			ShadowEnabled,
			RenderScale,
			QualityMode,
			MaxFps);
		Opacity = _renderSettings.Opacity;
		ShadowEnabled = _renderSettings.ShadowEnabled;
		RenderScale = _renderSettings.RenderScale;
		QualityMode = Live2DRenderSettings.QualityModeToStorage(_renderSettings.QualityMode);
		MaxFps = _renderSettings.MaxFps;
		lock (_qualityGate) _qualityPolicy.Update(_renderSettings, PowerSourceDetector.Detect());
	}
}
