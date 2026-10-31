namespace Nori.Core.Platform;

/// <summary>原生 Wayland 会话：拖动由 Avalonia 的输入事件发起，不调用 X11 或读取全局坐标。</summary>
public sealed class WaylandPlatformServices : IPlatformServices
{
	public SessionType Session => SessionType.Wayland;
	public PlatformCapabilities Capabilities { get; } = new()
	{
		SupportsGlobalCursor = false,
		SupportsWindowDrag = true,
		SupportsHitThrough = false,
		SupportsTopmost = false,
		SupportsTray = true,
	};
	public bool PrefersReducedMotion => true;
	public Nori.Core.Notifications.INativeNotifier CreateApprovalNotifier(Func<bool> english, Action<string> activated, Action<Exception> failure) =>
		new LinuxNativeNotifier(english, activated, failure);

	public Task<bool> IsTrayAvailableAsync(CancellationToken cancellationToken = default) =>
		LinuxTrayAvailability.CheckAsync(cancellationToken);
	public (double X, double Y) GetCursorPosition() =>
		throw new PlatformNotSupportedException("Wayland 不提供窗口外的全局光标位置");
	public void StartWindowDrag(nint windowHandle) =>
		throw new PlatformNotSupportedException("Wayland 窗口拖动必须通过当前指针事件发起");
	public void SetClickThrough(nint windowHandle, bool through) { }
}
