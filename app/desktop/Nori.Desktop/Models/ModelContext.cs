using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Models;

/// <summary>原生模型窗口的独立可信来源；缓存可见性以供后台命令检查。</summary>
internal sealed class ModelContext : INativeModelSource, IDisposable
{
	private readonly Window _owner;
	private int _visible;
	private int _disposed;

	public ModelContext(Window owner)
	{
		_owner = owner ?? throw new ArgumentNullException(nameof(owner));
		_visible = Dispatcher.UIThread.CheckAccess() && owner.IsVisible ? 1 : 0;
		_owner.PropertyChanged += OnOwnerPropertyChanged;
	}

	public string Label => WindowLabels.Models;
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
