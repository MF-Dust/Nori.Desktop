using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL.Controls;
using Avalonia.OpenGL;
using Avalonia.Threading;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Live2D.Gl;
using Nori.Core.Live2D;

namespace Nori.Desktop.Live2D;

/// <summary>
/// Avalonia 原生 OpenGL 渲染控件
///
/// 承载宿主直接组合的原生模型渲染管线：
/// - 基于 OpenGlControlBase，以物理像素（Bounds x RenderScaling）渲染
/// - 开启 2048x2048 裁剪蒙版缓冲与各向异性过滤
/// - 后台定时驱动 RequestNextFrameRendering，按 l2d_max_fps 限帧
/// - 约 10Hz 采样全视口 alpha 缓冲，生成贴近可见模型尺寸的连续交互矩形
/// - GPU 设备丢失或交换链纹理创建失败时暂停帧请求，按退避重建渲染表面
/// </summary>
public sealed class PetGlControl : OpenGlControlBase
{
	private const double MaskSampleIntervalSeconds = 0.150;

	private readonly PetRuntime _runtime;
	private readonly Action<RenderFrameArgs> _renderFrame;
	private readonly record struct RenderFrameArgs(GlInterface Gl, int Framebuffer);
	private AvaloniaGlApi? _glApi;
	private DateTime _lastRenderTime;
	private OpenGLTextureQuad? _textureQuad;
	private NativeGlSurface? _sceneSurface;
	private NativeGlSurface? _hitMaskSurface;
	private int _sceneWidth;
	private int _sceneHeight;
	private bool _offscreenAvailable;

	// Alpha 命中边界缓存
	private readonly object _maskLock = new();
	private PetHitMask.Bounds _maskBounds = PetHitMask.Bounds.Empty;
	private double _lastMaskSampleTime;
	private int _lastViewportW;
	private int _lastViewportH;
	private byte[] _pixelBuffer = [];

	// 帧驱动
	private CancellationTokenSource? _fpsCts;
	private Thread? _renderThread;
	/// <summary>渲染线程是否在运行 (窗口隐藏时暂停, 避免不可见空转)</summary>
	private volatile bool _renderLoopRunning;
	/// <summary>已排队但尚未渲染的帧请求, 用于避免 Dispatcher 队列堆积</summary>
	private int _framePending;
	private volatile bool _renderActive;
	private readonly Action _requestFrameFromLoop;

	// GPU 故障恢复 (NORI-31 / NORI-32)
	private readonly GpuRecoveryBackoff _gpuBackoff = new();
	private IDisposable? _gpuRecovery;
	/// <summary>GPU 故障后到重建表面前不再请求帧，避免反复撞上失败的交换链纹理创建。</summary>
	private volatile bool _gpuSuspended;
	/// <summary>正在为 GPU 恢复摘下并放回控件；此时的 Deinit 需要在重建后恢复模型。</summary>
	private bool _rebuildingSurface;
	private bool _attached;

	public PetGlControl(PetRuntime runtime)
	{
		_runtime = runtime;
		_renderFrame = RenderFrame;
		_requestFrameFromLoop = RequestFrameFromLoop;
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		if (_attached) return;
		_attached = true;
		CrashReporter.GpuRenderFailureHandled += OnGpuRenderFailure;
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		// 基类在这里释放 Avalonia 的 GL 资源，并在上下文可用时回调 OnOpenGlDeinit。
		base.OnDetachedFromVisualTree(e);
		if (!_attached) return;
		_attached = false;
		CrashReporter.GpuRenderFailureHandled -= OnGpuRenderFailure;
		if (_rebuildingSurface) return;
		_gpuRecovery?.Dispose();
		_gpuRecovery = null;
		_gpuSuspended = false;
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "GL 初始化失败后的资源回滚不能覆盖原始异常。")]
	protected override void OnOpenGlInit(GlInterface gl)
	{
		base.OnOpenGlInit(gl);

		_runtime.RunSynchronized(() =>
		{
			// 上下文丢失后 Avalonia 可能跳过 Deinit；旧句柄属于已失效的上下文，只能丢弃。
			if (_glApi is not null) AbandonGlState();
			try
			{
				_glApi = new AvaloniaGlApi(gl);
				_textureQuad = new OpenGLTextureQuad(_glApi);
				_sceneSurface = new NativeGlSurface(_glApi);
				_hitMaskSurface = new NativeGlSurface(_glApi);
				_runtime.OnGlInit(_glApi);
				if (_runtime.RenderRecovery is PetRenderRecoveryPhase.ContextLost or PetRenderRecoveryPhase.Rebuilding or PetRenderRecoveryPhase.Failed)
					_runtime.NoteRenderRecovery(PetRenderRecoveryPhase.Restored);
				_runtime.SetRenderSurfaceState(false, false, _renderActive);
				StartRenderLoop();
			}
			catch
			{
				try { _runtime.OnGlDeinit(); } catch { }
				DisposeRenderTargets();
				try { _textureQuad?.Dispose(); } catch { }
				_textureQuad = null;
				_glApi = null;
				// 初始化失败后 Avalonia 不会再请求帧；设备刚出过故障时按退避重建，否则保持原有失败语义。
				if (_gpuBackoff.IsRecovering(Now())) Dispatcher.UIThread.Post(() => ScheduleGpuRecovery("gpu.init_failed"));
				else _runtime.NoteRenderRecovery(PetRenderRecoveryPhase.Failed);
				throw;
			}
		});
	}

	/// <summary>Avalonia 检测到上下文丢失后、重新初始化前调用；此时不能再发出 GL 调用。</summary>
	protected override void OnOpenGlLost()
	{
		StopRenderLoop();
		_runtime.RunSynchronized(AbandonGlState);
		_runtime.NoteRenderRecovery(PetRenderRecoveryPhase.ContextLost);
		_runtime.WriteRenderSurfaceLog("warn", "伴侣 OpenGL 上下文丢失, 已丢弃失效资源并等待重建", "gl.context_lost");
		base.OnOpenGlLost();
	}

	/// <summary>放弃属于已丢失上下文的全部 GL 句柄，不调用任何 GL 删除。</summary>
	private void AbandonGlState()
	{
		try { _runtime.OnGlLost(); }
		catch (Exception exception) { _runtime.WriteLive2DLog($"丢弃失效 Live2D 资源失败: {exception.Message}"); }
		_sceneSurface = null;
		_hitMaskSurface = null;
		_sceneWidth = 0;
		_sceneHeight = 0;
		_offscreenAvailable = false;
		_textureQuad = null;
		_glApi = null;
	}

	protected override void OnOpenGlDeinit(GlInterface gl)
	{
		StopRenderLoop();
		try
		{
			bool restoreModel = _rebuildingSurface;
			_runtime.RunSynchronized(() =>
			{
				try { _runtime.OnGlDeinit(restoreModel); }
				catch (Exception exception) { _runtime.WriteLive2DLog($"释放 Live2D 运行时失败: {exception.Message}"); }
				DisposeRenderTargets();
				try { _textureQuad?.Dispose(); }
				catch (Exception exception) { _runtime.WriteLive2DLog($"释放 Live2D 合成器失败: {exception.Message}"); }
				_textureQuad = null;
				_glApi = null;

			});
		}
		finally
		{
			base.OnOpenGlDeinit(gl);
		}
	}

	protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
	{
		_runtime.RunSynchronized(new RenderFrameArgs(gl, fb), _renderFrame);
	}

	private unsafe void RenderFrame(RenderFrameArgs args)
	{
		try
		{
			RenderCore(args.Gl, args.Framebuffer);
		}
		catch (Exception exception)
		{
			// 渲染回调跑在合成器提交路径上, 抛出去就是进程级崩溃
			_runtime.WriteLive2DLog($"伴侣渲染帧异常: {exception}");
		}
	}

	private unsafe void RenderCore(GlInterface gl, int fb)
	{
		Interlocked.Exchange(ref _framePending, 0);
		if (!_renderActive || _glApi is null) return;

		long frameStart = Stopwatch.GetTimestamp();
		double scale = 1.0;
		if (VisualRoot is Avalonia.Controls.TopLevel topLevel)
		{
			scale = topLevel.RenderScaling;
		}

		int viewportW = (int)Math.Max(1, Math.Round(Bounds.Width * scale));
		int viewportH = (int)Math.Max(1, Math.Round(Bounds.Height * scale));
		_lastViewportW = viewportW;
		_lastViewportH = viewportH;

		DateTime now = DateTime.UtcNow;
		float span = _lastRenderTime.Ticks == 0 ? 0.016f : (float)(now - _lastRenderTime).TotalSeconds;
		_lastRenderTime = now;
		span = Math.Clamp(span, 0.001f, 0.1f);

		bool offscreen = EnsureRenderTargets(viewportW, viewportH, _runtime.EffectiveRenderScale);
		bool shadowApplied = false;
		if (offscreen && _sceneSurface is { } scene && scene.IsValid() && _textureQuad is {IsAvailable: true} quad)
		{
			scene.BeginDraw();
			try
			{
				gl.Viewport(0, 0, _sceneWidth, _sceneHeight);
				gl.ClearColor(0, 0, 0, 0);
				gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT | GlConsts.GL_DEPTH_BUFFER_BIT);
				_runtime.RenderFrame(span, viewportW, viewportH, Bounds.Width, Bounds.Height);
			}
			finally
			{
				scene.EndDraw();
			}

			gl.BindFramebuffer(_glApi.GL_FRAMEBUFFER, fb);
			gl.Viewport(0, 0, viewportW, viewportH);
			gl.ClearColor(0, 0, 0, 0);
			gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT | GlConsts.GL_DEPTH_BUFFER_BIT);
			if (_runtime.ShadowEnabled) shadowApplied = quad.DrawShadow(scene.ColorBuffer);
			quad.Draw(scene.ColorBuffer, 1, 1, 1, 1);
		}
		else
		{
			gl.BindFramebuffer(_glApi.GL_FRAMEBUFFER, fb);
			gl.Viewport(0, 0, viewportW, viewportH);
			gl.ClearColor(0, 0, 0, 0);
			gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT | GlConsts.GL_DEPTH_BUFFER_BIT);
			_runtime.RenderFrame(span, viewportW, viewportH, Bounds.Width, Bounds.Height);
		}

		_runtime.SetRenderSurfaceState(offscreen, shadowApplied, true);

		// 命中掩码只在可见窗口采样；阴影从未写入模型纹理，因此不会扩大可点击区域。
		double nowSec = (now - DateTime.UnixEpoch).TotalSeconds;
		if (nowSec - _lastMaskSampleTime >= MaskSampleIntervalSeconds)
		{
			_lastMaskSampleTime = nowSec;
			long maskStart = Stopwatch.GetTimestamp();
			SampleAlphaMask(viewportW, viewportH, fb);
			_runtime.RecordRenderMetrics(ElapsedMilliseconds(frameStart), ElapsedMilliseconds(maskStart));
		}
		else
		{
			_runtime.RecordRenderMetrics(ElapsedMilliseconds(frameStart), 0);
		}
	}

	private unsafe void SampleAlphaMask(int viewportW, int viewportH, int defaultFramebuffer)
	{
		var glApi = _glApi;
		if (!_renderActive || glApi is null || viewportW <= 0 || viewportH <= 0) return;

		int readWidth = viewportW;
		int readHeight = viewportH;
		bool hitMaskTarget = false;
		try
		{
			if (_offscreenAvailable && _sceneSurface is { } scene && scene.IsValid())
			{
				readWidth = _sceneWidth;
				readHeight = _sceneHeight;
				if (_hitMaskSurface is { } hit
					&& hit.IsValid()
					&& _textureQuad is {IsHitMaskAvailable: true} quad)
				{
					hit.BeginDraw();
					try
					{
						glApi.Viewport(0, 0, PetHitMask.Width, PetHitMask.Height);
						hit.Clear(0, 0, 0, 0);
						if (quad.DrawHitMask(scene.ColorBuffer))
						{
							readWidth = PetHitMask.Width;
							readHeight = PetHitMask.Height;
							hitMaskTarget = true;
							ReadPixels();
						}
					}
					finally
					{
						hit.EndDraw();
					}
				}
				if (!hitMaskTarget)
				{
					// 命中目标已在 finally 结束；场景回读也不能把绘制快照带到下一帧。
					scene.BeginDraw();
					try
					{
						glApi.Viewport(0, 0, readWidth, readHeight);
						ReadPixels();
					}
					finally
					{
						scene.EndDraw();
					}
				}
			}
			else
			{
				glApi.BindFramebuffer(glApi.GL_FRAMEBUFFER, defaultFramebuffer);
				glApi.Viewport(0, 0, readWidth, readHeight);
				ReadPixels();
			}
		}
		finally
		{
			glApi.BindFramebuffer(glApi.GL_FRAMEBUFFER, defaultFramebuffer);
			glApi.Viewport(0, 0, viewportW, viewportH);
		}
		PublishAlphaMask(_pixelBuffer, readWidth, readHeight, hitMaskTarget);

		void ReadPixels()
		{
			int bufferLength = checked(readWidth * readHeight * 4);
			if (_pixelBuffer.Length != bufferLength) _pixelBuffer = new byte[bufferLength];
			fixed (byte* pointer = _pixelBuffer)
			{
				// GLES2 允许的最小实现使用一次同步回读；低分辨率 FBO 路径固定为 96x128。
				glApi.GLReadPixels(0, 0, readWidth, readHeight, glApi.GL_RGBA, glApi.GL_UNSIGNED_BYTE, (nint)pointer);
			}
		}
	}

	private void PublishAlphaMask(byte[] pixels, int width, int height, bool reduced)
	{
		PetHitMask.Bounds bounds = reduced
			? PetHitMask.BuildFromReducedPixels(pixels, width, height)
			: PetHitMask.BuildFromSourcePixels(pixels, width, height);

		lock (_maskLock)
		{
			_maskBounds = bounds;
		}
	}

	private bool EnsureRenderTargets(int viewportW, int viewportH, float renderScale)
	{
		if (_glApi is null || _textureQuad is not {IsAvailable: true})
		{
			_offscreenAvailable = false;
			_runtime.SetRenderSurfaceState(false, false, _renderActive);
			return false;
		}

		float scale = Math.Clamp(renderScale, Live2DRenderSettings.MinRenderScale, Live2DRenderSettings.MaxRenderScale);
		int targetWidth = Math.Max(1, (int)Math.Round(viewportW * scale));
		int targetHeight = Math.Max(1, (int)Math.Round(viewportH * scale));
		const int maxDimension = 4096;
		if (targetWidth > maxDimension || targetHeight > maxDimension)
		{
			float dimensionScale = Math.Min((float)maxDimension / targetWidth, (float)maxDimension / targetHeight);
			targetWidth = Math.Max(1, (int)Math.Round(targetWidth * dimensionScale));
			targetHeight = Math.Max(1, (int)Math.Round(targetHeight * dimensionScale));
		}

		if (_offscreenAvailable && targetWidth == _sceneWidth && targetHeight == _sceneHeight) return true;

		_offscreenAvailable = TryResizeRenderTargets(_glApi, ref _sceneSurface, ref _hitMaskSurface,
			targetWidth, targetHeight, _runtime.WriteLive2DLog);
		// 回滚时仍按旧场景的实际尺寸绘制和回读，而不是使用失败的请求尺寸。
		_sceneWidth = _sceneSurface?.BufferWidth ?? 0;
		_sceneHeight = _sceneSurface?.BufferHeight ?? 0;
		return _offscreenAvailable;
	}

	/// <summary>候选场景就绪后才交换；命中目标失败只降级采样，不撤销场景。</summary>
	internal static bool TryResizeRenderTargets(OpenGLApi gl, ref NativeGlSurface? scene, ref NativeGlSurface? hit,
		int width, int height, Action<string> writeLog)
	{
		bool previousAvailable = scene?.IsValid() == true;
		if (previousAvailable && scene!.BufferWidth == width && scene.BufferHeight == height) return true;

		NativeGlSurface? nextScene = new(gl);
		NativeGlSurface? nextHit = null;
		bool hitCreated = false;
		try
		{
			if (!nextScene.CreateOffscreenSurface(width, height))
			{
				writeLog(previousAvailable
					? "创建伴侣场景 FBO 失败, 保留旧场景继续绘制"
					: "创建伴侣场景 FBO 失败, 已降级为直接渲染");
				return previousAvailable;
			}

			// 命中目标尺寸固定，不随场景缩放重建。
			if (hit?.IsValid() != true)
			{
				nextHit = new NativeGlSurface(gl);
				try
				{
					hitCreated = nextHit.CreateOffscreenSurface(PetHitMask.Width, PetHitMask.Height);
					if (!hitCreated)
						writeLog("低分辨率命中掩码 FBO 不可用, 已降级为场景纹理单次回读");
				}
				catch (Exception exception)
				{
					writeLog($"创建命中掩码 FBO 失败, 已降级为场景纹理单次回读: {exception.Message}");
				}
			}

			var previousScene = scene;
			scene = nextScene;
			nextScene = null;
			if (hitCreated)
			{
				var previousHit = hit;
				hit = nextHit;
				nextHit = null;
				DisposeRenderTarget(previousHit, "命中掩码", writeLog);
			}
			DisposeRenderTarget(previousScene, "场景", writeLog);
			return true;
		}
		catch (Exception exception)
		{
			writeLog($"创建伴侣场景 FBO 失败, {(previousAvailable ? "保留旧场景继续绘制" : "已降级为直接渲染")}: {exception.Message}");
			return previousAvailable;
		}
		finally
		{
			DisposeRenderTarget(nextScene, "候选场景", writeLog);
			DisposeRenderTarget(nextHit, "候选命中掩码", writeLog);
		}
	}

	private static void DisposeRenderTarget(NativeGlSurface? surface, string name, Action<string> writeLog)
	{
		try { surface?.DestroyOffscreenSurface(); }
		catch (Exception exception) { writeLog($"释放伴侣{name} FBO 失败: {exception.Message}"); }
	}

	private void DisposeRenderTargets()
	{
		DisposeRenderTarget(_sceneSurface, "场景", _runtime.WriteLive2DLog);
		DisposeRenderTarget(_hitMaskSurface, "命中掩码", _runtime.WriteLive2DLog);
		_sceneWidth = 0;
		_sceneHeight = 0;
		_offscreenAvailable = false;
	}

	/// <summary>
	/// 同步检查给定客户端坐标（DIP）是否落在模型非透明像素上
	/// </summary>
	public bool IsPointOnModel(double clientX, double clientY)
	{
		lock (_maskLock)
		{
			return PetHitMask.IsPointOnModel(_maskBounds, clientX, clientY, Bounds.Width, Bounds.Height);
		}
	}

	internal PetHitMask.Bounds MaskBounds
	{
		get
		{
			lock (_maskLock) return _maskBounds;
		}
	}

	private static double ElapsedMilliseconds(long startedAt) =>
		Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

	private void StartRenderLoop()
	{
		if (_renderLoopRunning || !_renderActive || _gpuSuspended) return;
		_fpsCts = new CancellationTokenSource();
		_renderLoopRunning = true;
		CancellationToken token = _fpsCts.Token;

		_renderThread = new Thread(() =>
		{
			Stopwatch clock = Stopwatch.StartNew();
			long nextDeadline = clock.ElapsedTicks;
			while (!token.IsCancellationRequested && _renderActive)
			{
				int targetFps = Math.Max(1, _runtime.EffectiveFps);
				long intervalTicks = Math.Max(1, Stopwatch.Frequency / targetFps);
				long now = clock.ElapsedTicks;
				if (now < nextDeadline)
				{
					long waitTicks = nextDeadline - now;
					int waitMs = Math.Max(1, (int)Math.Min(20, Math.Ceiling(waitTicks * 1000.0 / Stopwatch.Frequency)));
					try
					{
						if (token.WaitHandle.WaitOne(waitMs)) break;
					}
					catch (ObjectDisposedException)
					{
						break;
					}
					continue;
				}

				// 迟到时跳过旧 deadline, 避免渲染线程在恢复后连续补发过期帧。
				now = clock.ElapsedTicks;
				nextDeadline = now + intervalTicks;
				if (!_renderActive) break;
				if (Interlocked.CompareExchange(ref _framePending, 1, 0) == 0)
				{
					Dispatcher.UIThread.Post(_requestFrameFromLoop, DispatcherPriority.Render);
				}
				else
				{
					_runtime.RecordDroppedFrame();
				}
			}
		})
		{
			Name = "Nori_Pet_GlRenderLoop",
			IsBackground = true,
		};
		_renderThread.Start();
	}

	private void StopRenderLoop()
	{
		if (!_renderLoopRunning) return;
		_renderLoopRunning = false;

		try
		{
			Interlocked.Exchange(ref _framePending, 0);
			_fpsCts?.Cancel();
			// deadline 等待可被 CancellationToken 唤醒, 正常情况下无需等满一个帧间隔。
			_renderThread?.Join(500);
		}
		finally
		{
			_fpsCts?.Dispose();
			_fpsCts = null;
			_renderThread = null;
		}
	}

	/// <summary>
	/// 暂停帧驱动 (窗口隐藏时调用)
	///
	/// 窗口隐藏后合成器对帧请求的处理不可控: 要么继续全速渲染 + 采样 alpha (白烧 CPU/GPU),
	/// 要么帧请求滞留导致重新显示后停帧。显式停掉渲染线程可以同时避免这两种问题,
	/// GL 上下文保持存活, 重新显示后无缝恢复。
	/// </summary>
	public void PauseRenderLoop()
	{
		_renderActive = false;
		Interlocked.Exchange(ref _framePending, 0);
		_runtime.SetRenderSurfaceState(_offscreenAvailable, false, false);
		StopRenderLoop();
	}

	/// <summary>
	/// 恢复帧驱动 (窗口重新显示时调用)
	///
	/// GL 尚未初始化时是空操作 —— 首次显示会走 OnOpenGlInit 自行启动渲染循环。
	/// </summary>
	public void ResumeRenderLoop()
	{
		_renderActive = true;
		_lastRenderTime = default;
		_runtime.SetRenderSurfaceState(_offscreenAvailable, false, true);
		if (_glApi is null) return;
		// 清掉可能滞留的旧帧请求标记, 最坏情况多画一帧
		Interlocked.Exchange(ref _framePending, 0);
		StartRenderLoop();
	}

	/// <summary>帧循环投递到 UI 线程的请求；GPU 故障挂起期间丢弃，已排队的旧请求也不再触发合成更新。</summary>
	private void RequestFrameFromLoop()
	{
		if (_gpuSuspended)
		{
			Interlocked.Exchange(ref _framePending, 0);
			return;
		}
		RequestNextFrameRendering();
	}

	private static TimeSpan Now() => Stopwatch.GetElapsedTime(0);

	/// <summary>
	/// GPU 渲染失败已由崩溃兜底处理 (UI 线程)。
	///
	/// 失败发生在 Avalonia 合成提交里 (OpenGlControlBase 借交换链纹理)，每次帧请求都会再撞一次，
	/// 并让同一批合成提交中断；先停掉帧请求，再按退避重建。失败来源未必是本控件，
	/// 所有已挂到界面的 OpenGL 控件都会重建一次。
	/// </summary>
	private void OnGpuRenderFailure(GpuFailure failure)
	{
		if (!_attached) return;
		ScheduleGpuRecovery(failure.Kind == GpuFailure.DeviceLost ? "gpu.device_lost" : "gpu.interop_failure");
	}

	private void ScheduleGpuRecovery(string eventId)
	{
		if (!_attached) return;
		_runtime.NoteRenderRecovery(PetRenderRecoveryPhase.Rebuilding);
		_gpuSuspended = true;
		Interlocked.Exchange(ref _framePending, 0);
		StopRenderLoop();
		if (_gpuRecovery is not null) return;
		TimeSpan delay = _gpuBackoff.NextDelay(Now());
		_runtime.WriteRenderSurfaceLog("warn", $"伴侣渲染表面暂停, {delay.TotalSeconds:0} 秒后重建", eventId);
		_gpuRecovery = DispatcherTimer.RunOnce(RecoverFromGpuFailure, delay, DispatcherPriority.Background);
	}

	/// <summary>测试观测缝：是否已安排 GPU 故障后的表面重建。</summary>
	internal bool GpuRecoveryPending => _gpuRecovery is not null;

	/// <summary>测试观测缝：不等待退避计时，立即执行已安排的表面重建。</summary>
	internal void RecoverFromGpuFailureNow()
	{
		if (_gpuRecovery is null) return;
		_gpuRecovery.Dispose();
		RecoverFromGpuFailure();
	}

	private void RecoverFromGpuFailure()
	{
		_gpuRecovery = null;
		if (!_attached) return;
		_gpuSuspended = false;
		_runtime.WriteRenderSurfaceLog("info", "重建伴侣渲染表面", "gl.surface_rebuild");
		if (RebuildSurface()) return;
		// 无法重挂时交给 Avalonia 的丢失检测：恢复帧请求，上下文已丢失会在下一帧自动重建。
		if (_glApi is not null) StartRenderLoop();
		RequestNextFrameRendering();
	}

	/// <summary>
	/// 从父容器摘下再原位放回，强制 Avalonia 释放并重新创建 OpenGL 上下文、交换链与 FBO。
	/// 摘下与放回在同一次 UI 调度内完成，宿主不会观察到控件缺席。
	/// </summary>
	private bool RebuildSurface()
	{
		_rebuildingSurface = true;
		try
		{
			switch (Parent)
			{
				case Panel panel when panel.Children.IndexOf(this) is var index and >= 0:
					panel.Children.RemoveAt(index);
					panel.Children.Insert(index, this);
					return true;
				case Decorator decorator when ReferenceEquals(decorator.Child, this):
					decorator.Child = null;
					decorator.Child = this;
					return true;
				default:
					return false;
			}
		}
		finally
		{
			_rebuildingSurface = false;
		}
	}

	/// <summary>由窗口显隐驱动渲染；隐藏状态不产生调度请求，也不采样命中掩码。</summary>
	public void SetRenderActive(bool active)
	{
		if (active) ResumeRenderLoop();
		else PauseRenderLoop();
	}
}
