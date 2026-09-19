using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Core.Logging;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.QuickChat;

/// <summary>将独立聊天表面锚定到伴侣窗口；配置、显隐和会话释放各有明确边界。</summary>
internal sealed class QuickChatController
{
	private readonly AppServices _services;
	private readonly PetWindow _pet;
	private readonly Action<QuickChatWindow> _register;
	private readonly Action<QuickChatWindow> _unregister;
	private readonly SemaphoreSlim _transition = new(1, 1);
	private QuickChatWindow? _window;
	private bool _disposed;
	private bool _layoutQueued;
	private bool _refreshQueued;
	private bool _refreshAgain;
	private string _draft = "";

	internal QuickChatController(AppServices services, PetWindow pet, Action<QuickChatWindow> register, Action<QuickChatWindow> unregister)
	{
		_services = services; _pet = pet; _register = register; _unregister = unregister;
		_pet.PropertyChanged += OnPetPropertyChanged;
		_pet.PositionChanged += OnPetPositionChanged;
		_pet.Closed += OnPetClosed;
		_pet.ScalingChanged += OnGeometryChanged;
		_pet.Screens.Changed += OnGeometryChanged;
		_services.PetRuntime.ModelChanged += QueueLayout;
		_services.PetRuntime.LayoutChanged += QueueLayout;
		_services.Runtime!.StateChanged += QueueRefresh;
		QueueRefresh();
	}

	internal bool Enabled { get; private set; }
	internal QuickChatWindow? Window => _window;

	private void QueueRefresh()
	{
		Dispatcher.UIThread.Post(async () =>
		{
			if (_disposed) return;
			if (_refreshQueued) { _refreshAgain = true; return; }
			_refreshQueued = true;
			try
			{
				do { _refreshAgain = false; await RefreshAsync(); }
				while (_refreshAgain && !_disposed);
			}
			catch (Exception exception) { LogFailure(exception); }
			finally { _refreshQueued = false; }
		});
	}

	internal async Task RefreshAsync()
	{
		await _transition.WaitAsync();
		try
		{
			if (_disposed) return;
			bool enabled = await Task.Run(() => QuickChatSettings.IsEnabled(_services));
			if (_disposed) return;
			Enabled = enabled;
			_pet.SetQuickChatPresentation(enabled);
			if (!enabled) { await CloseSurfaceAsync(); return; }
			if (!_pet.IsVisible) { _window?.Hide(); return; }
			if (_window is null)
			{
				_window = new QuickChatWindow(_services);
				_window.Body.State.Draft = _draft;
				_window.DesiredLayoutChanged += QueueLayout;
				_register(_window);
			}
			Reposition();
			if (!_window.IsVisible) _window.Show();
			QueueLayout();
		}
		finally { _transition.Release(); }
	}

	internal bool FocusComposer()
	{
		if (!Enabled || !_pet.IsVisible || _window is not { IsVisible: true }) return false;
		_window.FocusComposer(); return true;
	}

	private void OnPetPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (args.Property == Visual.IsVisibleProperty)
		{
			if (!_pet.IsVisible) _window?.Hide();
			QueueRefresh();
		}
		else if (args.Property == Visual.BoundsProperty) QueueLayout();
	}
	private void OnPetPositionChanged(object? sender, PixelPointEventArgs args) => QueueLayout();
	private async void OnPetClosed(object? sender, EventArgs args)
	{
		try { await ShutdownAsync(); }
		catch (Exception exception) { LogFailure(exception); }
	}
	private void OnGeometryChanged(object? sender, EventArgs args) => QueueLayout();
	private void QueueLayout()
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (_disposed || _layoutQueued) return;
			_layoutQueued = true;
			Dispatcher.UIThread.Post(() =>
			{
				_layoutQueued = false;
				if (!_disposed) Reposition();
			}, DispatcherPriority.Loaded);
		});
	}

	internal void Reposition()
	{
		if (_window is null || !_pet.IsVisible || !Enabled) return;
		var screen = _pet.Screens.ScreenFromWindow(_pet) ?? _pet.Screens.Primary;
		PixelRect work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
		double scale = _pet.RenderScaling > 0 ? _pet.RenderScaling : 1;
		Size size = new(_pet.Width, _pet.Height);
		int petWidth = (int)Math.Ceiling(size.Width * scale);
		int petHeight = (int)Math.Ceiling(size.Height * scale);
		int composerWidth = (int)Math.Ceiling(308 * scale);
		int side = Math.Max(0, (composerWidth - petWidth) / 2);
		int reserve = (int)Math.Ceiling((QuickChatLayout.ComposerHeight + QuickChatLayout.ShadowMargin - 2) * scale);
		PixelPoint position = new(
			Math.Clamp(_pet.Position.X, work.X + side, Math.Max(work.X + side, work.Right - petWidth - side)),
			Math.Clamp(_pet.Position.Y, work.Y, Math.Max(work.Y, work.Bottom - petHeight - reserve)));
		if (position != _pet.Position) _pet.Position = position;
		double availableHeight = Math.Max(74, (position.Y + petHeight + reserve - work.Y) / scale);
		QuickChatPlacement layout = QuickChatLayout.Calculate(position, size, scale, work,
			_window.MeasureContentHeight(Math.Min(availableHeight, work.Height / scale)));
		_window.Width = layout.Width; _window.Height = layout.Height;
		_window.Position = layout.Position;
		_window.EnsureTopmost();
	}

	private async Task CloseSurfaceAsync()
	{
		if (_window is not { } window) return;
		_window = null;
		_draft = window.Body.State.Draft;
		window.DesiredLayoutChanged -= QueueLayout;
		window.Hide();
		try { await window.PrepareShutdownAsync(); }
		finally { window.AllowClose = true; window.Close(); _unregister(window); }
	}

	internal async Task ShutdownAsync()
	{
		if (_disposed) return;
		_disposed = true;
		_pet.PropertyChanged -= OnPetPropertyChanged;
		_pet.PositionChanged -= OnPetPositionChanged;
		_pet.Closed -= OnPetClosed;
		_pet.ScalingChanged -= OnGeometryChanged;
		_pet.Screens.Changed -= OnGeometryChanged;
		_services.PetRuntime.ModelChanged -= QueueLayout;
		_services.PetRuntime.LayoutChanged -= QueueLayout;
		_services.Runtime!.StateChanged -= QueueRefresh;
		await _transition.WaitAsync();
		try { await CloseSurfaceAsync(); }
		finally { _transition.Release(); }
	}

	private void LogFailure(Exception exception) => _services.Logger.Write(LogSource.Backend, "warn", $"快捷聊天生命周期更新失败: {exception.GetType().Name}");
}
