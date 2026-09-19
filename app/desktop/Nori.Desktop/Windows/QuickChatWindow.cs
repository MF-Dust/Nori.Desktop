using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.Core.Logging;
using Nori.Core.Platform;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.QuickChat;

namespace Nori.Desktop.Windows;

/// <summary>伴侣的独立原生输入表面；显示和接收消息均不激活窗口。</summary>
public sealed class QuickChatWindow : Window
{
	private readonly NativeChatService _service;
	private readonly AppServices _services;
	private readonly DispatcherTimer _inputTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
	private bool? _through;
	private bool _closed;
	internal QuickChatView Body { get; }
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
		Topmost = true;
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.Manual;
		Width = 308;
		Height = 74;
		_service = new NativeChatService(services, this, NativeChatSurface.QuickChat);
		Body = new QuickChatView(_service, () => PlatformServices.Current.PrefersReducedMotion);
		Content = new Border { Padding = new Thickness(QuickChatLayout.ShadowMargin), Child = Body };
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
			Body.SetHostVisible(IsVisible);
			if (IsVisible) { EnsureTopmost(); _inputTimer.Start(); }
			else { _inputTimer.Stop(); FocusManager?.Focus(null); }
		}
		else if (args.Property == TopmostProperty && !Topmost) Topmost = true;
	}

	internal void EnsureTopmost()
	{
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
		Activate(); Body.FocusComposer();
	}

	internal double MeasureContentHeight(double maximumHeight)
	{
		Body.MaxHeight = Math.Max(46, maximumHeight - QuickChatLayout.ShadowMargin * 2);
		Body.Measure(new Size(Math.Max(1, Width - QuickChatLayout.ShadowMargin * 2), Body.MaxHeight));
		return Body.DesiredSize.Height + QuickChatLayout.ShadowMargin * 2;
	}

	private Rect[] InteractiveRects() => Body.InteractiveControls
		.Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0)
		.Select(control => control.TranslatePoint(default, this) is { } origin
			? new Rect(origin, control.Bounds.Size) : default)
		.Where(rect => rect.Width > 0).ToArray();

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
				linux.SetInputShape(handle, regions.Select(rect => ((int)(rect.X * scale), (int)(rect.Y * scale),
					(int)Math.Ceiling(rect.Width * scale), (int)Math.Ceiling(rect.Height * scale))).ToArray());
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
			_services.Logger.Write(LogSource.Backend, "warn", $"快捷聊天区域穿透不可用，使用紧凑窗口: {exception.GetType().Name}");
		}
	}

	internal async Task PrepareShutdownAsync()
	{
		try { await Body.PrepareShutdownAsync(); }
		finally { DisposeSurface(); }
	}

	private void DisposeSurface()
	{
		if (_closed) return;
		_closed = true; _inputTimer.Stop(); _inputTimer.Tick -= UpdateInputRegion;
		Body.DesiredLayoutChanged -= OnDesiredLayoutChanged;
		PropertyChanged -= OnWindowPropertyChanged;
		Body.Dispose(); _service.Dispose();
	}
}
