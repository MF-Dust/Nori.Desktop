using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.Core.Platform;
using Nori.Desktop.Platform;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Appearance;

/// <summary>统一常规窗口的系统背景模糊、实际效果回退及临时窗口生命周期。</summary>
internal sealed class WindowBackdropController : IDisposable
{
	private readonly HashSet<Window> _windows = [];
	private readonly HashSet<Window> _openingWindows = [];
	private readonly IDisposable _openedSubscription;
	private readonly Action<Exception>? _onFailure;
	private bool _enabled;
	private bool _disposed;
	private int _preferenceRevision;

	/// <summary>测试可让资格判断抛异常，确认打开窗口不会被材质回调打断。</summary>
	internal static Func<Window, bool>? EligibilityOverride { get; set; }

	internal WindowBackdropController(bool enabled = true, Action<Exception>? onFailure = null)
	{
		_enabled = enabled;
		_onFailure = onFailure;
		_openedSubscription = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => ObserveOpening(window));
	}

	private void ObserveOpening(Window window)
	{
		if (_disposed) return;
		try { ObserveOpeningCore(window); }
		catch (Exception exception) when (exception is not OutOfMemoryException) { _onFailure?.Invoke(exception); }
	}

	private void ObserveOpeningCore(Window window)
	{
		if (_disposed || !IsEligible(window) || _windows.Contains(window)) return;
		if (window.Owner is Window owner && _windows.Contains(owner)) { Register(window); return; }
		// Avalonia 的全局打开事件先于 Owner 赋值；监听赋值可在平台窗口显示前接入材质。
		if (!_openingWindows.Add(window)) return;
		window.PropertyChanged += OnOpeningPropertyChanged;
		window.Opened += OnOpeningCompleted;
		window.Closed += OnOpeningCompleted;
	}

	private void OnOpeningPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (args.Property == WindowBase.OwnerProperty && sender is Window window
			&& window.Owner is Window owner && _windows.Contains(owner))
		{
			StopObservingOpening(window);
			Register(window);
		}
	}

	private void OnOpeningCompleted(object? sender, EventArgs args)
	{
		if (sender is Window window) StopObservingOpening(window);
	}

	private void StopObservingOpening(Window window)
	{
		window.PropertyChanged -= OnOpeningPropertyChanged;
		window.Opened -= OnOpeningCompleted;
		window.Closed -= OnOpeningCompleted;
		_openingWindows.Remove(window);
	}

	/// <summary>后台读取配置，期间用户的新选择优先于启动时读取结果。</summary>
	internal async Task InitializeAsync(Func<bool> readPreference)
	{
		int revision = _preferenceRevision;
		bool enabled = await Task.Run(readPreference).ConfigureAwait(false);
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			if (!_disposed && revision == _preferenceRevision) SetEnabled(enabled);
		});
	}

	internal void Register(Window window)
	{
		Dispatcher.UIThread.VerifyAccess();
		try { RegisterCore(window); }
		catch (Exception exception) when (exception is not OutOfMemoryException) { _onFailure?.Invoke(exception); }
	}

	private void RegisterCore(Window window)
	{
		if (_disposed || !IsEligible(window) || !_windows.Add(window)) return;
		window.PropertyChanged += OnWindowPropertyChanged;
		window.Opened += OnWindowOpened;
		window.Closed += OnWindowClosed;
		ApplyRequest(window);
		foreach (Window child in window.OwnedWindows) Register(child);
	}

	internal static bool IsEligible(Window window) =>
		EligibilityOverride is { } hook ? hook(window) : window is not PetWindow and not QuickChatWindow;

	internal void SetEnabled(bool enabled)
	{
		Dispatcher.UIThread.VerifyAccess();
		if (_disposed) return;
		_preferenceRevision++;
		_enabled = enabled;
		foreach (Window window in _windows.ToArray()) ApplyRequest(window);
	}

	private void ApplyRequest(Window window)
	{
		window.TransparencyLevelHint = _enabled
			? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent]
			: [WindowTransparencyLevel.Transparent];
		ApplySurface(window);
	}

	internal static bool IsBlurActive(bool enabled, WindowTransparencyLevel actual) => enabled
		&& (actual == WindowTransparencyLevel.AcrylicBlur || actual == WindowTransparencyLevel.Blur);

	private void ApplySurface(Window window)
	{
		bool active = IsBlurActive(_enabled, window.ActualTransparencyLevel) || WaylandBlurActive(window);
		Paint(window, active);
	}

	/// <summary>测试可替换 Wayland 模糊结果。返回 null 时走真实协议。</summary>
	internal static Func<Window, bool, bool?>? WaylandBlurOverride { get; set; }

	private bool WaylandBlurActive(Window window)
	{
		if (WaylandBlurOverride is { } hook && hook(window, _enabled) is { } forced) return forced;
		if (!OperatingSystem.IsLinux() || PlatformServices.Current.Session != SessionType.Wayland) return false;
		WaylandBlur.Request(window, _enabled, active => NotifyWaylandBlur(window, active));
		return false;
	}

	private void NotifyWaylandBlur(Window window, bool active)
	{
		if (_disposed || !_enabled || !_windows.Contains(window)) return;
		Paint(window, active || IsBlurActive(true, window.ActualTransparencyLevel));
	}

	private static void Paint(Window window, bool active)
	{
		IBrush surface = NoriThemeTokens.Brush(active ? "bg-glass" : "bg-base");
		window.Resources["NoriWindowSurfaceBrush"] = surface;
		window.Background = surface;
	}

	private void OnWindowOpened(object? sender, EventArgs args)
	{
		if (sender is Window window && _windows.Contains(window)) ApplySurface(window);
	}

	private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (!_disposed && sender is Window window && args.Property == TopLevel.ActualTransparencyLevelProperty)
			ApplySurface(window);
	}

	private void OnWindowClosed(object? sender, EventArgs args)
	{
		if (sender is Window window) Unregister(window);
	}

	private void Unregister(Window window)
	{
		window.PropertyChanged -= OnWindowPropertyChanged;
		window.Opened -= OnWindowOpened;
		window.Closed -= OnWindowClosed;
		_windows.Remove(window);
		if (WaylandBlurOverride is null && OperatingSystem.IsLinux() && PlatformServices.Current.Session == SessionType.Wayland)
			WaylandBlur.Request(window, false, static _ => { });
	}

	public void Dispose()
	{
		Dispatcher.UIThread.VerifyAccess();
		if (_disposed) return;
		SetEnabled(false);
		_disposed = true;
		_openedSubscription.Dispose();
		foreach (Window window in _openingWindows.ToArray()) StopObservingOpening(window);
		foreach (Window window in _windows.ToArray()) Unregister(window);
	}
}
