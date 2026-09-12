using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Settings;

/// <summary>
/// 原生设置窗口的桥接上下文。
///
/// 该上下文以 settings 标签标识自身，不冒充 main WebView；可见性通过窗口属性
/// 缓存，避免后台命令直接读取 Avalonia 对象。
/// </summary>
internal sealed class SettingsContext : INativeSettingsSource, IDisposable
{
	private readonly Window _owner;
	private int _visible;
	private int _disposed;

	public SettingsContext(Window owner)
	{
		_owner = owner ?? throw new ArgumentNullException(nameof(owner));
		// 原生窗口通常在 UI 线程创建；测试或关闭竞态下若从后台线程构造上下文，
		// 不直接读取 Avalonia 属性，避免跨线程访问窗口对象。
		_visible = Dispatcher.UIThread.CheckAccess() && owner.IsVisible ? 1 : 0;
		_owner.PropertyChanged += OnOwnerPropertyChanged;
	}

	public string Label => WindowLabels.Settings;

	public bool IsVisible => Volatile.Read(ref _visible) != 0;

	public Window? Self => _owner;

	private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (args.Property == Visual.IsVisibleProperty)
			Volatile.Write(ref _visible, _owner.IsVisible ? 1 : 0);
	}

	public void PostEvent(string name, object? payload)
	{
		_ = name;
		_ = payload;
	}

	public void PostResult(long id, object? value, string? error)
	{
		_ = id;
		_ = value;
		_ = error;
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_owner.PropertyChanged -= OnOwnerPropertyChanged;
	}
}
