using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Desktop.Bridge;

namespace Nori.Desktop.Windows;

/// <summary>原生窗口的通用上下文基类,缓存可见性以供后台命令检查。</summary>
internal abstract class WindowContext : IBridgeSource, IDisposable
{
	private readonly Window _owner;
	private int _visible;
	private int _disposed;

	protected WindowContext(Window owner, string label)
	{
		_owner = owner ?? throw new ArgumentNullException(nameof(owner));
		Label = label ?? throw new ArgumentNullException(nameof(label));
		_visible = Dispatcher.UIThread.CheckAccess() && owner.IsVisible ? 1 : 0;
		_owner.PropertyChanged += OnOwnerPropertyChanged;
	}

	public string Label { get; }
	public bool IsVisible => Volatile.Read(ref _visible) != 0;
	public Window? Self => _owner;

	private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (args.Property == Visual.IsVisibleProperty)
			Volatile.Write(ref _visible, _owner.IsVisible ? 1 : 0);
	}

	public void PostEvent(string name, object? payload) { }
	public void PostResult(long id, object? value, string? error) { }

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_owner.PropertyChanged -= OnOwnerPropertyChanged;
	}
}
