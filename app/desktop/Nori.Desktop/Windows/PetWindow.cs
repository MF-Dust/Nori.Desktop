using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Nori.Core.Live2D;
using Nori.Core.Logging;
using Nori.Core.Platform;
using Nori.Desktop.Bridge;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Windows;

/// <summary>
/// 原生桌宠窗口
///
/// 承载 PetGlControl 并接管桌宠的全部交互：
/// - 模型外接矩形穿透: 约 10Hz 从 alpha 画面更新连续交互范围; Windows 走 WM_NCHITTEST
///   逐点判定, 并用 WS_EX_LAYERED|WS_EX_TRANSPARENT 分层样式实现跨进程穿透, 窗口始终置顶;
///   macOS/Linux(X11) 设置穿透开关/输入形状; Wayland 无此能力, 降级为整窗可点
/// - 左键拖拽移动窗口 + 坐标持久化（阈值 4px）
/// - 左键点击触发动作与表情判定
/// - 深海微光配色原生右键菜单（打开主界面 / 随机动作 / 重置位置 / 隐藏桌宠 / 退出）
/// - 全局光标追踪
/// </summary>
public sealed class PetWindow : Window
{
	private const int DragThreshold = 4;
	/// <summary>窗口至少要留在屏幕内的像素数</summary>
	private const int MinVisiblePixels = 40;
	private const uint WmNcHitTest = 0x0084;
	private static readonly IntPtr HtTransparent = new(-1);
	private static readonly IntPtr HtClient = new(1);

	private readonly AppServices _services;
	private readonly PetRuntime _runtime;
	private readonly PetGlControl _glControl;
	private readonly PetSpeechOverlay _speechOverlay;
	private readonly Win32Properties.CustomWndProcHookCallback _wndProcHook;
	private readonly DispatcherTimer _cursorTrackingTimer;
	/// <summary>非 Windows 平台的穿透同步 (Windows 由 WM_NCHITTEST 逐点判定, 不需要)</summary>
	private readonly DispatcherTimer? _hitShapeTimer;
	private ContextMenu? _contextMenu;
	/// <summary>上一次推给系统的穿透状态, 避免重复调用</summary>
	private bool? _lastClickThrough;

	// 拖拽状态
	private bool _isDragPending;
	private bool _isDragging;
	private bool _hasDragged;
	/// <summary>按下时光标的屏幕坐标 (物理像素)</summary>
	private PixelPoint _dragStartScreenPos;
	private PixelPoint _dragStartWinPos;
	private bool _isNativeDragPending;
	private PixelPoint _nativeDragStartWinPos;

	public bool AllowClose { get; set; }

	public PetWindow(WindowDefinition definition, AppServices services)
	{
		_services = services;
		_runtime = new PetRuntime(services);
		_services.PetRuntime = _runtime;

		Title = definition.Title;
		Width = definition.Width;
		Height = definition.Height;
		CanResize = false;
		Topmost = true;
		ShowActivated = false;
		ShowInTaskbar = false;
		WindowDecorations = WindowDecorations.None;
		Background = Brushes.Transparent;
		TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
		WindowStartupLocation = WindowStartupLocation.Manual;
		Icon = LoadIcon();

		_glControl = new PetGlControl(_runtime);
		_speechOverlay = new PetSpeechOverlay();
		Grid root = new();
		root.Children.Add(_glControl);
		root.Children.Add(_speechOverlay);
		Content = root;

		_wndProcHook = OnWndProc;

		BuildContextMenu();

		// 指针事件必须挂在窗口上而不是 GL 控件上:
		// OpenGlControlBase 只提交一个自定义绘制操作, 自身没有可命中的背景,
		// Avalonia 的输入命中测试不会选中它, 挂在它上面的 Pointer* 事件永远不会触发。
		// 窗口本身 Background = Transparent, 是可命中的, 而 GL 控件铺满整个窗口,
		// 所以 GetPosition(this) 与控件坐标一致。
		PointerPressed += OnPointerPressed;
		PointerMoved += OnPointerMoved;
		PointerReleased += OnPointerReleased;
		PointerCaptureLost += (_, _) => FinishDrag();

		_runtime.ModelChanged += OnRuntimeModelChanged;
		_runtime.LayoutChanged += OnRuntimeLayoutChanged;

		_cursorTrackingTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(33),
		};
		_cursorTrackingTimer.Tick += OnCursorTrackingTick;

		// 非 Windows: 按模型交互矩形 ~10Hz 同步一次输入形状 (与 alpha 采样频率同量级)
		if (!OperatingSystem.IsWindows() && PlatformServices.Current.Capabilities.SupportsHitThrough)
		{
			_hitShapeTimer = new DispatcherTimer {Interval = TimeSpan.FromMilliseconds(100)};
			_hitShapeTimer.Tick += OnHitShapeTick;
		}

		Opened += OnOpened;
		Closed += OnClosed;
	}

	/// <summary>
	/// 窗口可见性变化: 隐藏时暂停渲染循环与光标追踪, 显示时恢复
	/// </summary>
	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property != Visual.IsVisibleProperty) return;

		if (change.GetNewValue<bool>())
		{
			_glControl.ResumeRenderLoop();
			if (PlatformServices.Current.Capabilities.SupportsGlobalCursor) _cursorTrackingTimer.Start();
			_hitShapeTimer?.Start();
			RefreshInputState();
		}
		else
		{
			_glControl.PauseRenderLoop();
			_cursorTrackingTimer.Stop();
			_hitShapeTimer?.Stop();
			_speechOverlay.ClearText();
		}
	}

	/// <summary>显示桌宠短句气泡。</summary>
	public void ShowSpeech(string text)
	{
		if (!Dispatcher.UIThread.CheckAccess())
		{
			Dispatcher.UIThread.Post(() => ShowSpeech(text));
			return;
		}
		_speechOverlay.ShowText(text);
	}

	/// <summary>清除桌宠短句气泡。</summary>
	public void ClearSpeech() => _speechOverlay.ClearText();

	private void OnRuntimeModelChanged() => Dispatcher.UIThread.Post(ApplyWindowSize);

	private void OnRuntimeLayoutChanged() => Dispatcher.UIThread.Post(ApplyWindowSize);

	private void OnOpened(object? sender, EventArgs e)
	{
		if (OperatingSystem.IsWindows()) Win32Properties.AddWndProcHookCallback(this, _wndProcHook);
		RestoreWindowPosition();
		ApplyTopmost();
		_glControl.ResumeRenderLoop();
		if (PlatformServices.Current.Capabilities.SupportsGlobalCursor) _cursorTrackingTimer.Start();
		_hitShapeTimer?.Start();
		ReapplyInputState();
	}

	/// <summary>
	/// 应用置顶策略
	///
	/// Avalonia 的 Topmost 在 Windows/X11 上够用; macOS 需要额外把窗口提到
	/// NSFloatingWindowLevel, 否则会被全屏应用盖住。
	/// </summary>
	private void ApplyTopmost()
	{
		if (!PlatformServices.Current.Capabilities.SupportsTopmost) return;
		Topmost = true;
		if (OperatingSystem.IsMacOS() && PlatformServices.Current is MacPlatformServices mac)
		{
			try
			{
				mac.SetFloatingLevel(TryGetPlatformHandle()?.Handle ?? 0);
			}
			catch (Exception exception) when (exception is PlatformNotSupportedException or InvalidOperationException or EntryPointNotFoundException)
			{
				_services.Logger.Write(LogSource.Backend, "warn", $"设置桌宠置顶层级失败: {exception.Message}");
			}
		}
	}

	/// <summary>
	/// 非 Windows 的穿透同步: 用模型外接矩形更新输入形状
	///
	/// X11 直接设置矩形输入形状; 其他平台退化为
	/// 「光标在模型矩形内就接收事件, 否则整窗穿透」。
	/// </summary>
	private void OnHitShapeTick(object? sender, EventArgs e)
	{
		nint handle = TryGetPlatformHandle()?.Handle ?? 0;
		if (handle == 0) return;

		try
		{
			if (OperatingSystem.IsLinux() && PlatformServices.Current is LinuxPlatformServices linux)
			{
				linux.SetInputShape(handle, _runtime.ClickThroughEnabled ? [] : _glControl.BuildHitRegions(Bounds.Width, Bounds.Height));
				return;
			}

			// macOS: 按当前光标位置决定整窗是否接收事件
			if (!PlatformServices.Current.Capabilities.SupportsGlobalCursor) return;
			var (cursorX, cursorY) = PlatformServices.Current.GetCursorPosition();
			double scale = RenderScaling > 0 ? RenderScaling : 1.0;
			double clientX = (cursorX - Position.X) / scale;
			double clientY = (cursorY - Position.Y) / scale;
			bool inside = clientX >= 0 && clientX < Bounds.Width && clientY >= 0 && clientY < Bounds.Height;
			bool through = _runtime.ClickThroughEnabled || !(inside && _glControl.IsPointOnModel(clientX, clientY));
			if (!_runtime.ClickThroughEnabled && _contextMenu is {IsOpen: true}) through = false;

			if (_lastClickThrough == through) return;
			_lastClickThrough = through;
			PlatformServices.Current.SetClickThrough(handle, through);
		}
		catch (Exception exception) when (exception is PlatformNotSupportedException or InvalidOperationException or EntryPointNotFoundException or DllNotFoundException)
		{
			// 穿透是增强项: 失败就停掉同步并保持整窗可点, 绝不打断渲染
			_hitShapeTimer?.Stop();
			_services.Logger.Write(LogSource.Backend, "warn", $"桌宠穿透同步失败, 已降级为整窗可点: {exception.Message}");
		}
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		_glControl.PauseRenderLoop();
		_speechOverlay.ClearText();
		_cursorTrackingTimer.Stop();
		_cursorTrackingTimer.Tick -= OnCursorTrackingTick;
		if (_hitShapeTimer is not null)
		{
			_hitShapeTimer.Stop();
			_hitShapeTimer.Tick -= OnHitShapeTick;
		}
		_runtime.ModelChanged -= OnRuntimeModelChanged;
		_runtime.LayoutChanged -= OnRuntimeLayoutChanged;
		if (OperatingSystem.IsWindows()) Win32Properties.RemoveWndProcHookCallback(this, _wndProcHook);
	}

	/// <summary>显示桌宠后重新应用点击穿透与 Z 序状态。</summary>
	public void ReapplyInputState()
	{
		_lastClickThrough = null;
		RefreshInputState();
	}

	/// <summary>
	/// 按模型画布与用户缩放重算窗口尺寸, 保持窗口中心不动
	/// </summary>
	public void ApplyWindowSize()
	{
		var model = _runtime.CurrentModel;
		// GetCanvasWidth() 返回的是 Unit (通常 2.0), 尺寸计算要的是像素画布
		double rawW = model?.Model.GetCanvasWidthPixel() ?? PetSizing.DefaultPetWidth;
		double rawH = model?.Model.GetCanvasHeightPixel() ?? PetSizing.DefaultPetHeight;
		if (rawW <= 0 || rawH <= 0)
		{
			rawW = PetSizing.DefaultPetWidth;
			rawH = PetSizing.DefaultPetHeight;
		}

		double scale = RenderScaling > 0 ? RenderScaling : 1.0;
		// PetSizing 收的是 DIP 屏幕尺寸 (内部再乘 scaleFactor), 而 Screen.WorkingArea 是物理像素
		var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
		double screenScale = screen is { Scaling: > 0 } ? screen.Scaling : scale;
		double screenDipW = screen is not null ? screen.WorkingArea.Width / screenScale : 1920;
		double screenDipH = screen is not null ? screen.WorkingArea.Height / screenScale : 1080;

		var (targetPhysW, targetPhysH) = PetSizing.CalculateWindowSize(rawW, rawH, _runtime.UserScale, screenDipW, screenDipH, scale);

		// 首次显示时 Bounds 还是 0, 此时不做居中换算, 否则窗口会整体偏移半个身位
		double oldPhysW = Bounds.Width > 0 ? Bounds.Width * scale : targetPhysW;
		double oldPhysH = Bounds.Height > 0 ? Bounds.Height * scale : targetPhysH;
		double oldCenterX = Position.X + oldPhysW / 2.0;
		double oldCenterY = Position.Y + oldPhysH / 2.0;

		Width = targetPhysW / scale;
		Height = targetPhysH / scale;

		Position = new PixelPoint(
			(int)Math.Round(oldCenterX - targetPhysW / 2.0),
			(int)Math.Round(oldCenterY - targetPhysH / 2.0));
	}

	private void RestoreWindowPosition()
	{
		// ConfigValue 是 record, 直接 ToString() 会得到 "Integer { Value = 320 }", 必须走 GetStringOr
		string posXVal = _services.Config.GetStringOr("pet_window_x", "");
		string posYVal = _services.Config.GetStringOr("pet_window_y", "");

		if (int.TryParse(posXVal, out int x) && int.TryParse(posYVal, out int y))
		{
			Position = ClampToScreens(new PixelPoint(x, y));
			return;
		}

		var screen = Screens.Primary;
		if (screen is not null)
		{
			double scale = RenderScaling > 0 ? RenderScaling : 1.0;
			int w = (int)Math.Round(Width * scale);
			int h = (int)Math.Round(Height * scale);
			var area = screen.WorkingArea;
			Position = ClampToScreens(new PixelPoint(area.X + area.Width - w - 80, area.Y + area.Height - h - 120));
		}
	}

	/// <summary>
	/// 把窗口位置收进某块屏幕的工作区
	///
	/// 保存的坐标可能因为换分辨率、拔掉显示器或被拖到边缘而落在屏幕外,
	/// 不收口的话桌宠会彻底看不见也点不到。至少保留一部分可见以便拖回来。
	/// </summary>
	private PixelPoint ClampToScreens(PixelPoint position)
	{
		double scale = RenderScaling > 0 ? RenderScaling : 1.0;
		int w = Math.Max(1, (int)Math.Round(Width * scale));
		int h = Math.Max(1, (int)Math.Round(Height * scale));

		// 落在任何一块屏幕上都算有效
		foreach (var candidate in Screens.All)
		{
			var area = candidate.WorkingArea;
			bool visible = position.X + w > area.X + MinVisiblePixels
				&& position.X < area.X + area.Width - MinVisiblePixels
				&& position.Y + h > area.Y + MinVisiblePixels
				&& position.Y < area.Y + area.Height - MinVisiblePixels;
			if (visible) return position;
		}

		var fallback = (Screens.Primary ?? Screens.All.FirstOrDefault())?.WorkingArea;
		if (fallback is not { } target) return position;
		return new PixelPoint(
			Math.Clamp(position.X, target.X, Math.Max(target.X, target.X + target.Width - w)),
			Math.Clamp(position.Y, target.Y, Math.Max(target.Y, target.Y + target.Height - h)));
	}

	private void SaveWindowPosition()
	{
		int x = Position.X;
		int y = Position.Y;
		// SQLite 写入含 fsync, 拖拽收尾时别堵在 UI 线程上; Config.Set 自身有锁, 线程安全
		Task.Run(() =>
		{
			try
			{
				_services.Config.Set("pet_window_x", new Nori.Core.Configuration.ConfigValue.Text(x.ToString()));
				_services.Config.Set("pet_window_y", new Nori.Core.Configuration.ConfigValue.Text(y.ToString()));
			}
			catch
			{
				// 落盘失败只影响下次启动的位置恢复
			}
		});
	}

	private void OnCursorTrackingTick(object? sender, EventArgs e)
	{
		if (_runtime.EyeTrackingEnabled)
		{
			try
			{
				if (PlatformServices.Current.Capabilities.SupportsGlobalCursor)
				{
					var (screenCursorX, screenCursorY) = PlatformServices.Current.GetCursorPosition();
					double scale = RenderScaling > 0 ? RenderScaling : 1.0;
					double clientX = (screenCursorX - Position.X) / scale;
					double clientY = (screenCursorY - Position.Y) / scale;

					_runtime.LookAt((float)clientX, (float)clientY, (float)Bounds.Width, (float)Bounds.Height);
				}
			}
			catch
			{
				// 忽略追踪异常
			}
		}

		RefreshInputState();
	}

	/// <summary>刷新 Windows 桌宠的点击穿透状态 (窗口保持置顶)。</summary>
	public void RefreshInputState()
	{
		if (!OperatingSystem.IsWindows() || PlatformServices.Current is not WindowsPlatformServices windows) return;
		nint handle = TryGetPlatformHandle()?.Handle ?? 0;
		if (handle == 0) return;

		bool through = _runtime.ClickThroughEnabled;
		if (!through && !_isDragPending && !_isDragging && _contextMenu is not {IsOpen: true})
		{
			try
			{
				if (!PlatformServices.Current.Capabilities.SupportsGlobalCursor) return;
				var (cursorX, cursorY) = PlatformServices.Current.GetCursorPosition();
				double scale = RenderScaling > 0 ? RenderScaling : 1.0;
				double clientX = (cursorX - Position.X) / scale;
				double clientY = (cursorY - Position.Y) / scale;
				bool inside = clientX >= 0 && clientX < Bounds.Width && clientY >= 0 && clientY < Bounds.Height;
				through = !(inside && _glControl.IsPointOnModel(clientX, clientY));
			}
			catch (Exception exception) when (exception is PlatformNotSupportedException or InvalidOperationException)
			{
				_services.Logger.Write(LogSource.Backend, "warn", $"读取桌宠穿透状态失败: {exception.Message}");
				return;
			}
		}

		if (_contextMenu is {IsOpen: true} || _isDragPending || _isDragging) through = false;
		if (_lastClickThrough == through) return;

		try
		{
			windows.SetClickThrough(handle, through);
			_lastClickThrough = through;
		}
		catch (Exception exception) when (exception is InvalidOperationException or EntryPointNotFoundException or DllNotFoundException)
		{
			_services.Logger.Write(LogSource.Backend, "warn", $"同步桌宠穿透状态失败: {exception.Message}");
		}
	}

	private IntPtr OnWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg == WmNcHitTest)
		{
			if (_isDragPending || _isDragging || _contextMenu is {IsOpen: true})
			{
				handled = true;
				return HtClient;
			}

			if (_runtime.ClickThroughEnabled)
			{
				handled = true;
				return HtTransparent;
			}

			long lp = lParam.ToInt64();
			int screenX = (short)(lp & 0xFFFF);
			int screenY = (short)((lp >> 16) & 0xFFFF);

			double scale = RenderScaling > 0 ? RenderScaling : 1.0;
			double clientX = (screenX - Position.X) / scale;
			double clientY = (screenY - Position.Y) / scale;

			if (clientX < 0 || clientX >= Bounds.Width || clientY < 0 || clientY >= Bounds.Height)
			{
				handled = true;
				return HtTransparent;
			}

			bool hit = _glControl.IsPointOnModel(clientX, clientY);
			handled = true;
			return hit ? HtClient : HtTransparent;
		}

		return IntPtr.Zero;
	}

	private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		var props = e.GetCurrentPoint(this).Properties;
		if (props.IsLeftButtonPressed)
		{
			var pos = e.GetPosition(this);
			if (!_glControl.IsPointOnModel(pos.X, pos.Y)) return;

			// 桌宠是原生 Avalonia 窗口, 在 Linux/macOS 上优先让窗口管理器接管移动。
			// 这条路径也覆盖 Wayland: WebView 的标题栏拖动能力不可用时, 原生窗口仍可拖动。
			if (!OperatingSystem.IsWindows())
			{
				_isNativeDragPending = true;
				_nativeDragStartWinPos = Position;
				try
				{
					BeginMoveDrag(e);
					e.Handled = true;
					return;
				}
				catch (InvalidOperationException exception)
				{
					_isNativeDragPending = false;
					_services.Logger.Write(LogSource.Backend, "warn", $"原生桌宠拖动不可用, 改用手动拖动: {exception.Message}");
				}
			}

			_isDragPending = true;
			_isDragging = false;
			_hasDragged = false;
			// 位移必须在屏幕坐标系里算: 窗口会跟着光标移动, 用 GetPosition(this) 的话
			// 参考系自己也在动, 算出来的 dx/dy 是错的。
			// 这里直接读平台光标坐标而不用 PointToScreen: 无边框窗口的客户区原点与
			// Position 返回的窗口原点并不一致, 两者混用会让纵向位移多出一个标题栏高度。
			_dragStartScreenPos = CursorScreenPosition() ?? Position;
			_dragStartWinPos = Position;
			e.Pointer.Capture(this);
			e.Handled = true;
		}
		else if (props.IsRightButtonPressed)
		{
			// 菜单已经挂在 Window.ContextMenu 上, 由 Avalonia 自己在 PointerReleased 时打开;
			// 这里再 Open 一次会打架 (打开后立刻被关掉)
			var pos = e.GetPosition(this);
			if (!_glControl.IsPointOnModel(pos.X, pos.Y)) e.Handled = true;
		}
	}

	private void OnPointerMoved(object? sender, PointerEventArgs e)
	{
		if (_isDragPending && CursorScreenPosition() is { } screenPos)
		{
			int dx = screenPos.X - _dragStartScreenPos.X;
			int dy = screenPos.Y - _dragStartScreenPos.Y;

			if (_isDragging || HasExceededDragThreshold(_dragStartScreenPos, screenPos, RenderScaling))
			{
				_isDragging = true;
				_hasDragged = true;
				Position = new PixelPoint(_dragStartWinPos.X + dx, _dragStartWinPos.Y + dy);
			}
		}

		var clientPos = e.GetPosition(this);
		_runtime.LookAt((float)clientPos.X, (float)clientPos.Y, (float)Bounds.Width, (float)Bounds.Height);
	}

	private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		var clientPos = e.GetPosition(this);
		bool wasDragging = _isDragging;
		bool didDrag = _hasDragged;
		bool didNativeDrag = _isNativeDragPending
			&& HasExceededDragThreshold(_nativeDragStartWinPos, Position, RenderScaling);

		if (didNativeDrag) SaveWindowPosition();
		_isNativeDragPending = false;
		FinishDrag();
		e.Pointer.Capture(null);

		if (!wasDragging && !didDrag && !didNativeDrag && e.InitialPressMouseButton == MouseButton.Left)
		{
			if (_glControl.IsPointOnModel(clientPos.X, clientPos.Y))
			{
				_runtime.HandleTap((float)clientPos.X, (float)clientPos.Y, (float)Bounds.Width, (float)Bounds.Height);
			}
		}
	}

	/// <summary>判断两个物理像素坐标是否达到桌宠拖动阈值。</summary>
	internal static bool HasExceededDragThreshold(PixelPoint start, PixelPoint end, double renderScaling)
	{
		double scale = renderScaling > 0 ? renderScaling : 1.0;
		double threshold = DragThreshold * scale;
		double dx = (double)end.X - start.X;
		double dy = (double)end.Y - start.Y;
		return dx * dx + dy * dy >= threshold * threshold;
	}

	/// <summary>
	/// 当前光标的屏幕坐标 (物理像素), 非 Windows 平台返回 null
	/// </summary>
	private static PixelPoint? CursorScreenPosition()
	{
		try
		{
			var (x, y) = PlatformServices.Current.GetCursorPosition();
			return new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
		}
		catch (PlatformNotSupportedException)
		{
			return null;
		}
	}

	private void FinishDrag()
	{
		if (_isDragging && _hasDragged)
		{
			SaveWindowPosition();
		}
		_isDragPending = false;
		_isDragging = false;
		_hasDragged = false;
	}

	private void BuildContextMenu()
	{
		var menu = new ContextMenu
		{
			Background = new SolidColorBrush(Color.FromArgb(245, 10, 26, 40)),
			BorderBrush = new SolidColorBrush(Color.FromArgb(120, 125, 227, 255)),
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(8),
			Padding = new Thickness(4),
		};

		var openMainItem = CreateMenuItem("打开主界面", () => _services.Windows.Show(WindowLabels.Main));
		var randomMotionItem = CreateMenuItem("随机动作", () => _runtime.PlayRandomMotion());
		var resetPosItem = CreateMenuItem("重置位置", () =>
		{
			Position = new PixelPoint(120, 120);
			SaveWindowPosition();
		});
		var hidePetItem = CreateMenuItem("隐藏桌宠", () => _services.Windows.Hide(WindowLabels.Pet));
		var exitItem = CreateMenuItem("退出应用", () => _services.Windows.Shutdown(), isDanger: true);

		menu.Items.Add(openMainItem);
		menu.Items.Add(randomMotionItem);
		menu.Items.Add(resetPosItem);
		menu.Items.Add(new Separator { Background = new SolidColorBrush(Color.FromArgb(60, 125, 227, 255)), Margin = new Thickness(4, 2) });
		menu.Items.Add(hidePetItem);
		menu.Items.Add(exitItem);

		_contextMenu = menu;
		ContextMenu = menu;
	}

	private static MenuItem CreateMenuItem(string header, Action onClick, bool isDanger = false)
	{
		var item = new MenuItem
		{
			Header = header,
			Foreground = isDanger
				? new SolidColorBrush(Color.FromRgb(255, 120, 120))
				: new SolidColorBrush(Color.FromRgb(220, 240, 255)),
			FontSize = 13,
			Padding = new Thickness(12, 6),
			CornerRadius = new CornerRadius(4),
		};
		item.Click += (_, _) => onClick();
		return item;
	}

	private static WindowIcon? LoadIcon()
	{
		try
		{
			return new WindowIcon(AssetLoader.Open(new Uri("avares://Nori.Desktop/Assets/icon.ico")));
		}
		catch
		{
			return null;
		}
	}
}
