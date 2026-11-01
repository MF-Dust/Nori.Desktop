using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.Core.Logging;
using Nori.Core.Platform;
using Nori.Desktop.Bridge;
using Nori.Desktop.QuickChat;

namespace Nori.Desktop.Windows;

/// <summary>伴侣的独立原生输入表面；显示和接收消息均不激活窗口。</summary>
public sealed class QuickChatWindow : Window
{
	private readonly QuickChatSurface _surface;
	private readonly AppServices _services;
	private readonly DispatcherTimer _inputTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
	private bool? _through;
	private bool _closed;
	internal QuickChatView Body => _surface.Body;
	internal bool AllowClose { get; set; }
	internal event Action? DesiredLayoutChanged;

	public QuickChatWindow(AppServices services)
	{
		_services = services;
		Title = "Nori Quick Chat";
		WindowDecorations = WindowDecorations.None;
		Background = Brushes.Transparent;
		TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
		ShowInTaskbar = false;
		ShowActivated = false;
		Topmost = PlatformServices.Current.Capabilities.SupportsTopmost;
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.Manual;
		Width = 308;
		Height = 74;
		_surface = new QuickChatSurface(services, this);
		Content = _surface;
		Body.DesiredLayoutChanged += OnDesiredLayoutChanged;
		PropertyChanged += OnWindowPropertyChanged;
		_inputTimer.Tick += UpdateInputRegion;
		Opened += (_, _) => EnsureTopmost();
		Closing += (_, args) => { if (!AllowClose) { args.Cancel = true; Hide(); } };
		Closed += (_, _) => DisposeSurface();
	}

	private void OnDesiredLayoutChanged() => DesiredLayoutChanged?.Invoke();
	private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (_closed) return;
		if (args.Property == IsVisibleProperty)
		{
			if (IsVisible) { EnsureTopmost(); _inputTimer.Start(); }
			else _inputTimer.Stop();
		}
		else if (args.Property == TopmostProperty && !Topmost && PlatformServices.Current.Capabilities.SupportsTopmost) Topmost = true;
	}

	internal void EnsureTopmost()
	{
		if (!PlatformServices.Current.Capabilities.SupportsTopmost) return;
		Topmost = true;
		if (PlatformServices.Current.Capabilities.SupportsTopmost
			&& OperatingSystem.IsMacOS() && PlatformServices.Current is MacPlatformServices mac)
			mac.SetFloatingLevel(TryGetPlatformHandle()?.Handle ?? 0);
	}

	internal void FocusComposer()
	{
		if (!IsVisible || _closed) return;
		if (PlatformServices.Current.Capabilities.SupportsHitThrough)
			PlatformServices.Current.SetClickThrough(TryGetPlatformHandle()?.Handle ?? 0, false);
		_through = false;
		_surface.FocusComposer();
	}

	internal double MeasureContentHeight(double maximumHeight)
	{
		return _surface.MeasureContentHeight(Width, maximumHeight);
	}

	internal Rect[] InteractiveRects() => Body.InteractiveControls
		.Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0)
		.Select(control => control.TranslatePoint(default, this) is { } origin
			&& control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), this) is { } end
			? new Rect(origin, end) : default)
		.Where(rect => rect.Width > 0).ToArray();

	/// <summary>四边向外取整；分数缩放时不能分别取整起点与尺寸，否则控件边缘会穿透。</summary>
	internal static (int X, int Y, int Width, int Height) PhysicalInputRegion(Rect rect, double scale)
	{
		int left = (int)Math.Floor(rect.Left * scale);
		int top = (int)Math.Floor(rect.Top * scale);
		int right = (int)Math.Ceiling(rect.Right * scale);
		int bottom = (int)Math.Ceiling(rect.Bottom * scale);
		return (left, top, right - left, bottom - top);
	}

	private void UpdateInputRegion(object? sender, EventArgs args)
	{
		IPlatformServices platform = PlatformServices.Current;
		if (!platform.Capabilities.SupportsHitThrough) return;
		nint handle = TryGetPlatformHandle()?.Handle ?? 0;
		if (handle == 0) return;
		try
		{
			double scale = RenderScaling > 0 ? RenderScaling : 1;
			Rect[] regions = InteractiveRects();
			if (OperatingSystem.IsLinux() && platform is LinuxPlatformServices linux)
			{
				linux.SetInputShape(handle, regions.Select(rect => PhysicalInputRegion(rect, scale)).ToArray());
				return;
			}
			if (!platform.Capabilities.SupportsGlobalCursor) return;
			var (x, y) = platform.GetCursorPosition();
			Point local = new((x - Position.X) / scale, (y - Position.Y) / scale);
			bool through = !regions.Any(rect => rect.Contains(local));
			if (_through == through) return;
			platform.SetClickThrough(handle, through); _through = through;
		}
		catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
		{
			_inputTimer.Stop();
			_services.Logger.Write(LogSource.Backend, "warn", "快捷聊天区域穿透不可用，使用紧凑窗口", exception: exception);
		}
	}

	internal async Task PrepareShutdownAsync()
	{
		try { await _surface.PrepareShutdownAsync(); }
		finally { DisposeSurface(); }
	}

	private void DisposeSurface()
	{
		if (_closed) return;
		_closed = true; _inputTimer.Stop(); _inputTimer.Tick -= UpdateInputRegion;
		Body.DesiredLayoutChanged -= OnDesiredLayoutChanged;
		PropertyChanged -= OnWindowPropertyChanged;
		_surface.Dispose();
	}
}
