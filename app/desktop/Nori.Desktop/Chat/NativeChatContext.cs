using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Chat;

/// <summary>宿主内部固定的聊天表面，不接受外部传入任意来源标签。</summary>
internal enum NativeChatSurface { Full, QuickChat }

/// <summary>原生对话来源；后台只读取缓存的可见性和激活状态。</summary>
internal sealed class NativeChatContext : IBridgeSource, IDisposable
{
	private readonly Window _owner;
	public event Action<string, object?>? EventReceived;
	private readonly CancellationTokenSource _lifetime = new();
	private int _visible;
	private int _active;
	private int _disposed;

	public NativeChatContext(Window owner, NativeChatSurface surface = NativeChatSurface.Full)
	{
		Surface = surface;
		_owner = owner;
		LifetimeToken = _lifetime.Token;
		_visible = Dispatcher.UIThread.CheckAccess() && owner.IsVisible ? 1 : 0;
		_active = Dispatcher.UIThread.CheckAccess() && owner.IsActive ? 1 : 0;
		_owner.PropertyChanged += OnOwnerPropertyChanged;
	}

	public NativeChatSurface Surface { get; }
	public string Label => Surface == NativeChatSurface.QuickChat ? WindowLabels.QuickChat : WindowLabels.Chat;
	public bool IsVisible => Volatile.Read(ref _visible) != 0;
	public bool IsActive => Volatile.Read(ref _active) != 0;
	public Window? Self => _owner;
	public CancellationToken LifetimeToken { get; }

	private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		if (args.Property == Visual.IsVisibleProperty)
			Volatile.Write(ref _visible, _owner.IsVisible ? 1 : 0);
		else if (args.Property == Window.IsActiveProperty)
			Volatile.Write(ref _active, _owner.IsActive ? 1 : 0);
	}

	public void PostEvent(string name, object? payload)
	{
		if (Volatile.Read(ref _disposed) == 0) EventReceived?.Invoke(name, payload);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		Volatile.Write(ref _visible, 0);
		Volatile.Write(ref _active, 0);
		_owner.PropertyChanged -= OnOwnerPropertyChanged;
		_lifetime.Cancel();
		_lifetime.Dispose();
	}
}
