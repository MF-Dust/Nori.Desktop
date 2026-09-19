using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Appearance;

/// <summary>统一常规窗口的系统背景模糊、实际效果回退及临时窗口生命周期。</summary>
internal sealed class WindowBackdropController : IDisposable
{
	private readonly HashSet<Window> _windows = [];
	private readonly HashSet<Window> _openingWindows = [];
	private readonly IDisposable _openedSubscription;
	private bool _enabled;
	private bool _disposed;
	private int _preferenceRevision;

	internal WindowBackdropController(bool enabled = true)
	{
		_enabled = enabled;
		_openedSubscription = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => ObserveOpening(window));
	}

	private void ObserveOpening(Window window)
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
		if (_disposed || !IsEligible(window) || !_windows.Add(window)) return;
		window.PropertyChanged += OnWindowPropertyChanged;
		window.Closed += OnWindowClosed;
		ApplyRequest(window);
		foreach (Window child in window.OwnedWindows) Register(child);
	}

	internal static bool IsEligible(Window window) => window is not PetWindow and not QuickChatWindow
		&& (window is not NoriWindow web || web.Label != WindowLabels.AudioHost);

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
		bool active = IsBlurActive(_enabled, window.ActualTransparencyLevel);
		IBrush surface = NoriThemeTokens.Brush(active ? "bg-glass" : "bg-base");
		window.Resources["NoriWindowSurfaceBrush"] = surface;
		// WebView 的页面根节点已铺材质色，宿主保持透明以免叠加两层染色。
		window.Background = active && window is NoriWindow ? Brushes.Transparent : surface;
		if (window is NoriWindow web) web.SetBackdropActive(active);
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
		window.Closed -= OnWindowClosed;
		_windows.Remove(window);
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
