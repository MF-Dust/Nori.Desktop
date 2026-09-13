using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.Core.Live2D;
using Nori.Desktop.Bridge;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Models;

/// <summary>模型预览完成事件。</summary>
public sealed class ModelPreviewReadyEventArgs : EventArgs
{
	public ModelPreviewReadyEventArgs(string modelId)
	{
		ModelId = modelId;
	}

	public string ModelId { get; }
}

/// <summary>模型预览加载失败事件。</summary>
public sealed class ModelPreviewErrorEventArgs : EventArgs
{
	public ModelPreviewErrorEventArgs(string modelId, string message)
	{
		ModelId = modelId;
		Message = message;
	}

	public string ModelId { get; }
	public string Message { get; }
}

/// <summary>
/// 原生 Live2D 模型预览控件。
///
/// 每个控件持有独立的 PetRuntime 与 OpenGL 控件，不创建 PetWindow，
/// 不接入全局桌宠、音频或 AI 回调，也不会读写 selected_model。
/// </summary>
public sealed class ModelPreviewControl : UserControl, IDisposable
{
	private readonly PetRuntime _runtime;
	private readonly PetGlControl _glControl;
	private readonly TimeSpan _loadTimeout;
	private readonly Func<string, Task> _loadModel;
	private readonly Lock _stateGate = new();
	private CancellationTokenSource? _loadCancellation;
	private TopLevel? _topLevel;
	private PetViewportRect? _lastViewport;
	private long _loadRequest;
	private int _disposed;
	private bool _attached;
	private bool _manualPaused;
	private bool _cleared = true;
	private bool _isReady;
	private string? _loadedModelId;
	private string? _errorMessage;
	private float _previewScale = 1.0f;
	private Live2DQualityMode _qualityMode = Live2DQualityMode.Adaptive;
	private float _renderScale = Live2DRenderSettings.DefaultRenderScale;
	private bool _shadowEnabled = true;
	private int _maxFps;

	/// <summary>创建独立的原生模型预览。</summary>
	public ModelPreviewControl(AppServices services) : this(services, TimeSpan.FromSeconds(30))
	{
	}

	internal ModelPreviewControl(AppServices services, TimeSpan loadTimeout, Func<string, Task>? loadModel = null)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(loadTimeout, TimeSpan.Zero);
		_loadTimeout = loadTimeout;
		_runtime = new PetRuntime(services, previewMode: true);
		_loadModel = loadModel ?? _runtime.LoadPreviewModelAsync;
		_glControl = new PetGlControl(_runtime) {IsVisible = false};

		var root = new Grid
		{
			Background = Brushes.Transparent,
			ClipToBounds = true,
		};
		root.Children.Add(_glControl);
		Content = root;
		Background = Brushes.Transparent;
		ClipToBounds = true;
		Focusable = true;

		_runtime.FrameRendered += OnFrameRendered;
		AttachedToVisualTree += (_, _) =>
		{
			_attached = true;
			_topLevel = TopLevel.GetTopLevel(this);
			if (_topLevel is not null) _topLevel.PropertyChanged += OnTopLevelPropertyChanged;
			UpdateRenderState();
		};
		DetachedFromVisualTree += (_, _) =>
		{
			_attached = false;
			if (_topLevel is not null) _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
			_topLevel = null;
			CancelCurrentLoad();
			_glControl.PauseRenderLoop();
		};
		PointerMoved += OnPointerMoved;
		PointerReleased += OnPointerReleased;
	}

	/// <summary>模型已在 GL 上下文中提交并可渲染。</summary>
	public event EventHandler<ModelPreviewReadyEventArgs>? ModelReady;

	/// <summary>模型加载失败；消息可直接显示给用户。</summary>
	public event EventHandler<ModelPreviewErrorEventArgs>? ModelLoadFailed;

	/// <summary>模型画布在预览中的逻辑像素矩形发生变化。</summary>
	public event EventHandler? ViewportChanged;

	/// <summary>当前请求是否已经完成 GL 提交。</summary>
	public bool IsReady
	{
		get { lock (_stateGate) return _isReady; }
	}

	/// <summary>最后成功加载的模型 ID。</summary>
	public string? LoadedModelId
	{
		get { lock (_stateGate) return _loadedModelId; }
	}

	/// <summary>最后一次加载失败的中文消息。</summary>
	public string? ErrorMessage
	{
		get { lock (_stateGate) return _errorMessage; }
	}

	/// <summary>是否允许预览控件自身响应本地指针点击。</summary>
	public bool IsPointerInteractionEnabled { get; set; } = true;

	/// <summary>固定舞台内的模型显示倍率。</summary>
	public float PreviewScale
	{
		get { lock (_stateGate) return _previewScale; }
		set
		{
			ThrowIfDisposed();
			float normalized = NormalizeScale(value);
			lock (_stateGate)
			{
				if (_previewScale == normalized) return;
				_previewScale = normalized;
			}
			ApplyPreviewSettings();
		}
	}

	/// <summary>预览渲染质量策略。</summary>
	public Live2DQualityMode QualityMode
	{
		get { lock (_stateGate) return _qualityMode; }
		set
		{
			ThrowIfDisposed();
			Live2DQualityMode normalized = Enum.IsDefined(value) ? value : Live2DQualityMode.Adaptive;
			lock (_stateGate)
			{
				if (_qualityMode == normalized) return;
				_qualityMode = normalized;
			}
			ApplyPreviewSettings();
		}
	}

	/// <summary>预览离屏渲染倍率。</summary>
	public float RenderScale
	{
		get { lock (_stateGate) return _renderScale; }
		set
		{
			ThrowIfDisposed();
			float normalized = NormalizeRenderScale(value);
			lock (_stateGate)
			{
				if (_renderScale == normalized) return;
				_renderScale = normalized;
			}
			ApplyPreviewSettings();
		}
	}

	/// <summary>预览是否合成模型阴影。</summary>
	public bool ShadowEnabled
	{
		get { lock (_stateGate) return _shadowEnabled; }
		set
		{
			ThrowIfDisposed();
			lock (_stateGate)
			{
				if (_shadowEnabled == value) return;
				_shadowEnabled = value;
			}
			ApplyPreviewSettings();
		}
	}

	/// <summary>预览最大帧率；零表示由质量策略决定。</summary>
	public int MaxFps
	{
		get { lock (_stateGate) return _maxFps; }
		set
		{
			ThrowIfDisposed();
			int normalized = Math.Clamp(value, 0, Live2DRenderSettings.MaxExplicitFps);
			lock (_stateGate)
			{
				if (_maxFps == normalized) return;
				_maxFps = normalized;
			}
			ApplyPreviewSettings();
		}
	}

	/// <summary>
	/// 显式加载本地模型。新请求会取消旧请求；任务在模型完成 GL 提交后返回。
	/// </summary>
	public async Task LoadModelAsync(string modelId, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		string normalized = SupportedModelIds.Normalize(modelId)
			?? throw new InvalidOperationException($"不支持的 Live2D 模型 ID: {modelId}");

		CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		CancellationTokenSource? previous;
		long request;
		lock (_stateGate)
		{
			previous = _loadCancellation;
			_loadCancellation = cancellation;
			request = ++_loadRequest;
			_isReady = false;
			_errorMessage = null;
			_cleared = false;
		}
		try { previous?.Cancel(); }
		catch (ObjectDisposedException) { }

		try
		{
			await OnUiThreadAsync(() =>
			{
				if (!IsCurrentRequest(request)) return;
				if (_manualPaused || (_attached && !IsEffectivelyVisible)) cancellation.Cancel();
				cancellation.Token.ThrowIfCancellationRequested();
				_glControl.IsVisible = true;
				UpdateRenderState();
			}).ConfigureAwait(false);

			Task load;
			lock (_stateGate)
			{
				cancellation.Token.ThrowIfCancellationRequested();
				if (!IsCurrentRequest(request)) throw new OperationCanceledException(cancellation.Token);
				load = _loadModel(normalized);
			}
			// 无 GL 上下文时运行时不会提交模型；有界等待后必须同时作废后台准备。
			await load.WaitAsync(_loadTimeout, cancellation.Token).ConfigureAwait(false);
			if (!IsCurrentRequest(request)) throw new OperationCanceledException(cancellation.Token);

			await OnUiThreadAsync(() =>
			{
				lock (_stateGate)
				{
					cancellation.Token.ThrowIfCancellationRequested();
					if (!IsCurrentRequest(request)) throw new OperationCanceledException(cancellation.Token);
					ApplyPreviewSettings();
					_isReady = true;
					_loadedModelId = normalized;
					_errorMessage = null;
				}
				ModelReady?.Invoke(this, new ModelPreviewReadyEventArgs(normalized));
			}).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			bool canceled;
			lock (_stateGate)
			{
				canceled = cancellation.IsCancellationRequested || !IsCurrentRequest(request);
				if (IsCurrentRequest(request))
				{
					_runtime.CancelPendingModelLoad();
					_isReady = false;
				}
			}
			// 隐藏、暂停和替换请求是正常取消；运行时自行取消（如 GL 初始化失败）必须报错。
			if (canceled) throw new OperationCanceledException("模型预览加载已取消", exception, cancellation.Token);
			string message = exception is TimeoutException
				? $"模型 {normalized} 预览加载超时，请检查 OpenGL 支持后重试"
				: _runtime.LastModelLoadError ?? (exception is OperationCanceledException
					? $"模型 {normalized} 预览初始化中断，请检查 OpenGL 支持后重试"
					: $"模型 {normalized} 加载失败，请重新导入");
			await OnUiThreadAsync(() =>
			{
				lock (_stateGate)
				{
					cancellation.Token.ThrowIfCancellationRequested();
					if (!IsCurrentRequest(request)) throw new OperationCanceledException(cancellation.Token);
					_cleared = true;
					_errorMessage = message;
				}
				_glControl.IsVisible = false;
				UpdateRenderState();
				ModelLoadFailed?.Invoke(this, new ModelPreviewErrorEventArgs(normalized, message));
			}).ConfigureAwait(false);
			throw new InvalidOperationException(message, exception);
		}
		finally
		{
			lock (_stateGate)
			{
				if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
			}
			cancellation.Dispose();
		}
	}

	/// <summary>清空预览显示并取消尚未提交的加载。</summary>
	public void ClearModel()
	{
		ThrowIfDisposed();
		if (!Dispatcher.UIThread.CheckAccess())
		{
			Dispatcher.UIThread.Post(() =>
			{
				if (Volatile.Read(ref _disposed) == 0) ClearModelCore();
			});
			return;
		}
		ClearModelCore();
	}

	private void ClearModelCore()
	{
		CancelCurrentLoad();
		lock (_stateGate)
		{
			_cleared = true;
			_isReady = false;
			_loadedModelId = null;
			_errorMessage = null;
			_lastViewport = null;
		}
		_glControl.IsVisible = false;
		UpdateRenderState();
	}

	/// <summary>获取完整模型画布在当前预览中的逻辑像素矩形。</summary>
	public bool TryGetModelViewport(out Rect viewportRect)
	{
		viewportRect = default;
		if (!IsReady || !_runtime.TryGetModelViewport(out PetViewportRect mapped)) return false;
		viewportRect = new Rect(mapped.Left, mapped.Top, mapped.Width, mapped.Height);
		return true;
	}

	/// <summary>把一个归一化互动区域转换为当前预览逻辑像素矩形。</summary>
	public bool TryMapNormalizedRegion(PetInteractionRegion region, out Rect viewportRect)
	{
		ArgumentNullException.ThrowIfNull(region);
		viewportRect = default;
		if (!IsReady || !_runtime.TryMapNormalizedRegion(region, out PetViewportRect mapped)) return false;
		viewportRect = new Rect(mapped.Left, mapped.Top, mapped.Width, mapped.Height);
		return true;
	}

	/// <summary>只热应用本地视觉行为；不接入鼠标穿透、AI 或外部音频。</summary>
	internal void ApplyLocalBehavior(string behavior, bool enabled)
	{
		ThrowIfDisposed();
		string? key = behavior switch
		{
			"autoBlink" => "l2d_auto_blink",
			"eyeTracking" => "l2d_eye_tracking",
			"idleEyeAnimation" => "l2d_idle_eye_animation",
			"idleAnimation" => "l2d_idle_animation",
			"expressionEnabled" => "l2d_expression_enabled",
			"lipSync" => "l2d_lip_sync",
			"beatSync" => "l2d_beat_sync",
			"clickInteraction" => "l2d_click_interaction",
			_ => null,
		};
		if (key is not null) _runtime.ApplyConfig(key, enabled ? "1" : "0");
	}

	/// <summary>仅在预览运行时播放区域的本地动作与表情，不调度 AI。</summary>
	public bool TestRegion(PetInteractionRegion region)
	{
		ArgumentNullException.ThrowIfNull(region);
		if (!IsReady) return false;
		_runtime.ApplyLocalInteraction(region);
		return true;
	}

	/// <summary>在预览中播放指定动作。</summary>
	public bool PlayMotion(string name) => IsReady && _runtime.PlayMotionByName(name);

	/// <summary>在预览中播放指定表情。</summary>
	public bool PlayExpression(string name) => IsReady && _runtime.PlayExpression(name);

	/// <summary>停止预览表情。</summary>
	public void StopExpression()
	{
		if (IsReady) _runtime.StopExpression();
	}

	/// <summary>按预览逻辑像素坐标执行一次本地点击。</summary>
	public bool TapAt(Point point)
	{
		if (!IsReady || !IsPointerInteractionEnabled || !_runtime.ClickInteraction || Bounds.Width <= 0 || Bounds.Height <= 0
			|| !_glControl.IsPointOnModel(point.X, point.Y)) return false;
		_runtime.HandleTap((float)point.X, (float)point.Y, (float)Bounds.Width, (float)Bounds.Height);
		return true;
	}

	/// <summary>暂停预览并取消尚未提交的加载。</summary>
	public void Pause()
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		_manualPaused = true;
		CancelCurrentLoad();
		_glControl.PauseRenderLoop();
	}

	/// <summary>在控件可见且已挂载时恢复预览。</summary>
	public void Resume()
	{
		ThrowIfDisposed();
		_manualPaused = false;
		UpdateRenderState();
	}

	/// <summary>取消加载并让 Avalonia 在 GL 反初始化回调中释放资源。</summary>
	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		CancelCurrentLoad();
		lock (_stateGate)
		{
			_isReady = false;
			_cleared = true;
			_lastViewport = null;
		}
		_runtime.FrameRendered -= OnFrameRendered;
		if (_topLevel is not null) _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
		_topLevel = null;
		_glControl.PauseRenderLoop();
		void Detach()
		{
			_glControl.IsVisible = false;
			Content = null;
		}
		if (Dispatcher.UIThread.CheckAccess()) Detach();
		else Dispatcher.UIThread.Post(Detach);
	}

	private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (args.Property != Visual.IsVisibleProperty) return;
		if (_topLevel is {IsVisible: false}) CancelCurrentLoad();
		UpdateRenderState();
	}

	private void OnPointerMoved(object? sender, PointerEventArgs args)
	{
		if (!IsReady || !IsPointerInteractionEnabled) return;
		Point point = args.GetPosition(this);
		_runtime.LookAt((float)point.X, (float)point.Y, (float)Bounds.Width, (float)Bounds.Height);
	}

	private void OnPointerReleased(object? sender, PointerReleasedEventArgs args)
	{
		if (args.InitialPressMouseButton == MouseButton.Left) TapAt(args.GetPosition(this));
	}

	private void OnFrameRendered()
	{
		if (!_runtime.TryGetModelViewport(out PetViewportRect viewport)) return;
		lock (_stateGate)
		{
			if (_lastViewport == viewport) return;
			_lastViewport = viewport;
		}
		Dispatcher.UIThread.Post(() =>
		{
			if (Volatile.Read(ref _disposed) == 0) ViewportChanged?.Invoke(this, EventArgs.Empty);
		}, DispatcherPriority.Render);
	}

	private void ApplyPreviewSettings()
	{
		float scale;
		Live2DQualityMode qualityMode;
		float renderScale;
		bool shadowEnabled;
		int maxFps;
		lock (_stateGate)
		{
			scale = _previewScale;
			qualityMode = _qualityMode;
			renderScale = _renderScale;
			shadowEnabled = _shadowEnabled;
			maxFps = _maxFps;
		}
		_runtime.SetPreviewRenderSettings(scale, qualityMode, renderScale, shadowEnabled, maxFps);
	}

	private void UpdateRenderState()
	{
		bool cleared;
		lock (_stateGate) cleared = _cleared;
		_glControl.SetRenderActive(
			Volatile.Read(ref _disposed) == 0
			&& _attached
			&& IsEffectivelyVisible
			&& !_manualPaused
			&& !cleared);
	}

	private void CancelCurrentLoad()
	{
		CancellationTokenSource? cancellation;
		lock (_stateGate)
		{
			cancellation = _loadCancellation;
			if (cancellation is null) return;
			_loadCancellation = null;
			_loadRequest++;
			_isReady = false;
			_runtime.CancelPendingModelLoad();
		}
		try { cancellation.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	private bool IsCurrentRequest(long request)
	{
		lock (_stateGate) return request == _loadRequest && Volatile.Read(ref _disposed) == 0;
	}

	private static Task OnUiThreadAsync(Action action)
	{
		if (Dispatcher.UIThread.CheckAccess())
		{
			action();
			return Task.CompletedTask;
		}
		return Dispatcher.UIThread.InvokeAsync(action).GetTask();
	}

	private static float NormalizeScale(float value) =>
		float.IsFinite(value) ? Math.Clamp(value, 0.1f, 4.0f) : 1.0f;

	private static float NormalizeRenderScale(float value) =>
		float.IsFinite(value)
			? Math.Clamp(value, Live2DRenderSettings.MinRenderScale, Live2DRenderSettings.MaxRenderScale)
			: Live2DRenderSettings.DefaultRenderScale;

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
