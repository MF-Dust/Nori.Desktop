using System.Diagnostics;
using Avalonia;
using Avalonia.OpenGL.Controls;
using Avalonia.OpenGL;
using Avalonia.Threading;
using Live2DCSharpSDK.App;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.Framework.Rendering;
using Live2DCSharpSDK.OpenGL;
using Nori.Core.Live2D;

namespace Nori.Desktop.Live2D;

/// <summary>
/// Avalonia 原生 OpenGL 渲染控件
///
/// 承载 Live2DCSharpSDK 原生渲染管线：
/// - 基于 OpenGlControlBase，以物理像素（Bounds x RenderScaling）渲染
/// - 开启 2048x2048 高精度裁剪蒙版缓冲与各向异性过滤
/// - 后台定时驱动 RequestNextFrameRendering，按 l2d_max_fps 限帧
/// - 约 10Hz 采样全视口 alpha 缓冲，生成贴近可见模型尺寸的连续交互矩形
/// </summary>
public sealed class PetGlControl : OpenGlControlBase
{
	private const double MaskSampleIntervalSeconds = 0.100;

	private readonly PetRuntime _runtime;
	private LAppDelegateOpenGL? _lapp;
	private AvaloniaGlApi? _glApi;
	private bool _sdkLeaseAcquired;
	private DateTime _lastRenderTime;
	private OpenGLTextureQuad? _textureQuad;
	private CubismOffscreenSurface_OpenGLES2? _sceneSurface;
	private CubismOffscreenSurface_OpenGLES2? _hitMaskSurface;
	private int _sceneWidth;
	private int _sceneHeight;
	private bool _offscreenAvailable;

	// Alpha 命中掩码缓存
	private readonly object _maskLock = new();
	private readonly byte[] _maskBits = new byte[PetHitMask.ByteLength];
	private readonly byte[] _maskScratch = new byte[PetHitMask.ByteLength];
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

	public PetGlControl(PetRuntime runtime)
	{
		_runtime = runtime;
	}

	protected override void OnOpenGlInit(GlInterface gl)
	{
		base.OnOpenGlInit(gl);

		// CubismIdManager 与 LAppPal.DeltaTime 是进程级静态状态；双 GL 控件必须
		// 在各自上下文仍为 current 的回调内串行进入 SDK，而不是把 GL 工作搬到别的线程。
		CubismFramework.RunSynchronized(() =>
		{
			// Cubism 的日志绝不能走 Console.WriteLine: Nori.Desktop 是 WinExe, 没有控制台,
			// Console.WriteLine 会抛 IOException(句柄无效), 而它是在渲染回调里被调用的,
			// 未捕获会直接把整个进程带走。统一转到应用自己的文件日志。
			var cubismAllocator = new LAppAllocator();
			var cubismOption = new CubismOption
			{
				LogFunction = _runtime.WriteCubismLog,
				LoggingLevel = LogLevel.Warning,
			};
			if (!CubismFramework.StartUp(cubismAllocator, cubismOption))
			{
				throw new InvalidOperationException("Live2D Cubism Framework 初始化失败");
			}
			_sdkLeaseAcquired = true;

			try
			{
				_glApi = new AvaloniaGlApi(gl);
				_lapp = new LAppDelegateOpenGL(
					_glApi,
					_ => throw new InvalidOperationException("GL 线程禁止同步解码模型纹理"))
				{
					BGColor = new(0, 0, 0, 0),
				};

				_textureQuad = new OpenGLTextureQuad(_glApi);
				_sceneSurface = new CubismOffscreenSurface_OpenGLES2(_glApi);
				_hitMaskSurface = new CubismOffscreenSurface_OpenGLES2(_glApi);
				_runtime.OnGlInit(_lapp, _glApi);
				_runtime.SetRenderSurfaceState(false, false, _renderActive);
				StartRenderLoop();
			}
			catch
			{
				try { _runtime.OnGlDeinit(); } catch { }
				try { _lapp?.Dispose(); } catch { }
				_lapp = null;
				DisposeRenderTargets();
				try { _textureQuad?.Dispose(); } catch { }
				_textureQuad = null;
				_glApi = null;
				CubismFramework.CleanUp();
				_sdkLeaseAcquired = false;
				throw;
			}
		});
	}

	protected override void OnOpenGlDeinit(GlInterface gl)
	{
		StopRenderLoop();
		try
		{
			CubismFramework.RunSynchronized(() =>
			{
				try { _runtime.OnGlDeinit(); }
				catch (Exception exception) { _runtime.WriteCubismLog($"释放 Live2D 运行时失败: {exception.Message}"); }
				try { _lapp?.Dispose(); }
				catch (Exception exception) { _runtime.WriteCubismLog($"释放 Live2D 模型失败: {exception.Message}"); }
				_lapp = null;
				DisposeRenderTargets();
				try { _textureQuad?.Dispose(); }
				catch (Exception exception) { _runtime.WriteCubismLog($"释放 Live2D 合成器失败: {exception.Message}"); }
				_textureQuad = null;
				_glApi = null;
				if (_sdkLeaseAcquired)
				{
					CubismFramework.CleanUp();
					_sdkLeaseAcquired = false;
				}
			});
		}
		finally
		{
			base.OnOpenGlDeinit(gl);
		}
	}

	protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
	{
		CubismFramework.RunSynchronized(() =>
		{
			try
			{
				RenderCore(gl, fb);
			}
			catch (Exception exception)
			{
				// 渲染回调跑在合成器提交路径上, 抛出去就是进程级崩溃
				_runtime.WriteCubismLog($"伴侣渲染帧异常: {exception}");
			}
		});
	}

	private unsafe void RenderCore(GlInterface gl, int fb)
	{
		Interlocked.Exchange(ref _framePending, 0);
		if (!_renderActive || _glApi is null || _lapp is null) return;

		Stopwatch frameTimer = Stopwatch.StartNew();
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
			scene.BeginDraw(fb);
			gl.Viewport(0, 0, _sceneWidth, _sceneHeight);
			gl.ClearColor(0, 0, 0, 0);
			gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT | GlConsts.GL_DEPTH_BUFFER_BIT);
			_runtime.RenderFrame(span, viewportW, viewportH, Bounds.Width, Bounds.Height);
			scene.EndDraw();

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
			Stopwatch maskTimer = Stopwatch.StartNew();
			SampleAlphaMask(viewportW, viewportH, fb);
			_runtime.RecordRenderMetrics(frameTimer.Elapsed.TotalMilliseconds, maskTimer.Elapsed.TotalMilliseconds);
		}
		else
		{
			_runtime.RecordRenderMetrics(frameTimer.Elapsed.TotalMilliseconds, 0);
		}
	}

	private unsafe void SampleAlphaMask(int viewportW, int viewportH, int defaultFramebuffer)
	{
		if (!_renderActive || _glApi is null || viewportW <= 0 || viewportH <= 0) return;

		int readWidth = viewportW;
		int readHeight = viewportH;
		bool hitMaskTarget = false;
		if (_offscreenAvailable && _sceneSurface is { } scene && scene.IsValid())
		{
			readWidth = _sceneWidth;
			readHeight = _sceneHeight;
			if (_hitMaskSurface is { } hit
				&& hit.IsValid()
				&& _textureQuad is {IsHitMaskAvailable: true} quad)
			{
				hit.BeginDraw(defaultFramebuffer);
				_glApi.Viewport(0, 0, PetHitMask.Width, PetHitMask.Height);
				hit.Clear(0, 0, 0, 0);
				if (quad.DrawHitMask(scene.ColorBuffer))
				{
					readWidth = PetHitMask.Width;
					readHeight = PetHitMask.Height;
					hitMaskTarget = true;
				}
				else
				{
					hit.EndDraw();
					scene.BeginDraw(defaultFramebuffer);
					_glApi.Viewport(0, 0, readWidth, readHeight);
				}
			}
			else
			{
				scene.BeginDraw(defaultFramebuffer);
				_glApi.Viewport(0, 0, readWidth, readHeight);
			}
		}
		else
		{
			_glApi.BindFramebuffer(_glApi.GL_FRAMEBUFFER, defaultFramebuffer);
			_glApi.Viewport(0, 0, readWidth, readHeight);
		}

		int bufferLength = checked(readWidth * readHeight * 4);
		if (_pixelBuffer.Length != bufferLength) _pixelBuffer = new byte[bufferLength];
		fixed (byte* pointer = _pixelBuffer)
		{
			// GLES2 允许的最小实现使用一次同步回读；低分辨率 FBO 路径固定为 96x128。
			_glApi.GLReadPixels(0, 0, readWidth, readHeight, _glApi.GL_RGBA, _glApi.GL_UNSIGNED_BYTE, (nint)pointer);
		}

		if (hitMaskTarget)
		{
			_hitMaskSurface!.EndDraw();
		}
		else if (_offscreenAvailable && _sceneSurface is { } fallbackScene && fallbackScene.IsValid())
		{
			fallbackScene.EndDraw();
		}

		_glApi.BindFramebuffer(_glApi.GL_FRAMEBUFFER, defaultFramebuffer);
		_glApi.Viewport(0, 0, viewportW, viewportH);
		PublishAlphaMask(_pixelBuffer, readWidth, readHeight, hitMaskTarget);
	}

	private void PublishAlphaMask(byte[] pixels, int width, int height, bool reduced)
	{
		if (reduced)
		{
			PetHitMask.BuildFromReducedPixels(pixels, width, height, _maskScratch);
		}
		else
		{
			PetHitMask.BuildFromSourcePixels(pixels, width, height, _maskScratch);
		}

		lock (_maskLock)
		{
			Buffer.BlockCopy(_maskScratch, 0, _maskBits, 0, _maskBits.Length);
		}
	}

	private bool EnsureRenderTargets(int viewportW, int viewportH, float renderScale)
	{
		if (_glApi is null || _sceneSurface is null || _hitMaskSurface is null || _textureQuad is not {IsAvailable: true})
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

		DisposeRenderTargets();
		try
		{
			bool sceneCreated = _sceneSurface.CreateOffscreenSurface(targetWidth, targetHeight);
			bool hitCreated = _hitMaskSurface.CreateOffscreenSurface(PetHitMask.Width, PetHitMask.Height);
			if (!sceneCreated)
			{
				DisposeRenderTargets();
				return false;
			}
			_sceneWidth = targetWidth;
			_sceneHeight = targetHeight;
			_offscreenAvailable = true;
			if (!hitCreated)
			{
				// 场景纹理仍可用于合成与一次整图回读；命中掩码不会读到阴影。
				_runtime.WriteCubismLog("低分辨率命中掩码 FBO 不可用, 已降级为场景纹理单次回读");
			}
			return true;
		}
		catch (Exception exception)
		{
			DisposeRenderTargets();
			_runtime.WriteCubismLog($"创建伴侣离屏渲染目标失败, 已降级为直接渲染: {exception.Message}");
			return false;
		}
	}

	private void DisposeRenderTargets()
	{
		try { _sceneSurface?.DestroyOffscreenSurface(); }
		catch (Exception exception) { _runtime.WriteCubismLog($"释放伴侣场景 FBO 失败: {exception.Message}"); }
		try { _hitMaskSurface?.DestroyOffscreenSurface(); }
		catch (Exception exception) { _runtime.WriteCubismLog($"释放伴侣命中掩码 FBO 失败: {exception.Message}"); }
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
			return PetHitMask.IsPointOnModel(_maskBits, clientX, clientY, Bounds.Width, Bounds.Height);
		}
	}

	/// <summary>
	/// 把当前模型外接边界转成单个可点击矩形 (客户端逻辑像素)
	///
	/// Windows 走 WM_NCHITTEST 查询同一矩形; Linux X11 把矩形交给输入形状,
	/// macOS 则按光标是否位于矩形内切换整窗穿透。
	/// </summary>
	public List<(int X, int Y, int Width, int Height)> BuildHitRegions(double clientWidth, double clientHeight)
	{
		lock (_maskLock)
		{
			return PetHitMask.BuildHitRegions(_maskBits, clientWidth, clientHeight);
		}
	}

	private void StartRenderLoop()
	{
		if (_renderLoopRunning || !_renderActive) return;
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
					Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
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
		if (_lapp is null || _glApi is null) return;
		// 清掉可能滞留的旧帧请求标记, 最坏情况多画一帧
		Interlocked.Exchange(ref _framePending, 0);
		StartRenderLoop();
	}

	/// <summary>由窗口显隐驱动渲染；隐藏状态不产生调度请求，也不采样命中掩码。</summary>
	public void SetRenderActive(bool active)
	{
		if (active) ResumeRenderLoop();
		else PauseRenderLoop();
	}
}
