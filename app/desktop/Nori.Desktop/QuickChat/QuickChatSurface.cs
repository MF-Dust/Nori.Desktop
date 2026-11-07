using Avalonia;
using Avalonia.Controls;
using Nori.Desktop.Appearance;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.Vision;

namespace Nori.Desktop.QuickChat;

/// <summary>
/// 快捷聊天的内容与来源生命周期；可放入独立浮窗，也可与桌宠共用原生窗口。
/// 释放只由创建者负责（浮窗关闭时、控制器收尾时）：宿主窗口关闭不会提前释放，
/// 否则正在进行的收尾（拒绝待审批、取消会话）会在等待中途失去聊天服务。
/// </summary>
internal sealed class QuickChatSurface : Border, IDisposable
{
	private readonly Window _owner;
	private readonly NativeChatService _service;
	private readonly WindowScanController _scan;
	private bool _disposed;
	internal QuickChatView Body { get; }

	internal QuickChatSurface(AppServices services, Window owner)
	{
		_owner = owner;
		_service = new NativeChatService(services, owner, NativeChatSurface.QuickChat);
		Body = new QuickChatView(_service, () => MotionPreference.PrefersReducedMotion);
		_scan = new WindowScanController(services, owner, () => Body.ScanEnglish, Body.ShowScanReadout, Body.SetScanPicking);
		Body.AttachScan(services.ScanChip, _scan.ToggleAsync);
		Padding = new Thickness(QuickChatLayout.ShadowMargin);
		Child = Body;
		_owner.PropertyChanged += OnOwnerPropertyChanged;
		Body.SetHostVisible(owner.IsVisible);
	}

	internal void FocusComposer()
	{
		if (_disposed || !_owner.IsVisible) return;
		_owner.Activate();
		Body.FocusComposer();
	}

	internal double MeasureContentHeight(double width, double maximumHeight)
	{
		Body.MaxHeight = Math.Max(QuickChatLayout.ComposerHeight, maximumHeight - Padding.Top - Padding.Bottom);
		Body.Measure(new Size(Math.Max(1, width - Padding.Left - Padding.Right), Body.MaxHeight));
		return Body.DesiredSize.Height + Padding.Top + Padding.Bottom;
	}

	private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
	{
		if (_disposed || args.Property != Visual.IsVisibleProperty) return;
		Body.SetHostVisible(_owner.IsVisible);
		if (!_owner.IsVisible)
		{
			_scan.Cancel();
			_owner.FocusManager?.Focus(null);
		}
	}

	internal async Task PrepareShutdownAsync()
	{
		_scan.Dispose();
		try { await Body.PrepareShutdownAsync(); }
		finally { Dispose(); }
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_owner.PropertyChanged -= OnOwnerPropertyChanged;
		_scan.Dispose();
		Body.Dispose();
		_service.Dispose();
	}
}
