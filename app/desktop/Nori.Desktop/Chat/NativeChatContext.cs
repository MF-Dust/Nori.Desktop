using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Chat;

/// <summary>原生对话来源，不冒充主 WebView；后台只读取缓存的可见性。</summary>
internal sealed class NativeChatContext : INativeChatSource, IDisposable
{
	private readonly Window _owner;
	private readonly Action<string, object?> _onEvent;
	private readonly CancellationTokenSource _lifetime = new();
	private int _visible;
	private int _disposed;

	public NativeChatContext(Window owner, Action<string, object?> onEvent)
	{
		_owner = owner;
		_onEvent = onEvent;
		LifetimeToken = _lifetime.Token;
		_visible = Dispatcher.UIThread.CheckAccess() && owner.IsVisible ? 1 : 0;
		_owner.PropertyChanged += OnOwnerPropertyChanged;
	}

	public string Label => WindowLabels.Chat;
	public bool IsVisible => Volatile.Read(ref _visible) != 0;
	public Window? Self => _owner;
	public CancellationToken LifetimeToken { get; }

	private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (args.Property == Visual.IsVisibleProperty && Volatile.Read(ref _disposed) == 0)
			Volatile.Write(ref _visible, _owner.IsVisible ? 1 : 0);
	}

	public void PostEvent(string name, object? payload)
	{
		if (Volatile.Read(ref _disposed) == 0) _onEvent(name, payload);
	}

	public void PostResult(long id, object? value, string? error) { }

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		Volatile.Write(ref _visible, 0);
		_owner.PropertyChanged -= OnOwnerPropertyChanged;
		_lifetime.Cancel();
		_lifetime.Dispose();
	}
}
