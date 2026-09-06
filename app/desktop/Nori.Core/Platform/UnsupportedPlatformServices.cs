namespace Nori.Core.Platform;

/// <summary>
/// 未知平台的占位实现
///
/// 能力标志全部为 false —— 前端据此禁用相关交互并给出说明, 不会走到这些方法里。
/// 万一被调用则明确抛错, 便于定位而不是静默失效。
/// </summary>
public sealed class UnsupportedPlatformServices : IPlatformServices
{
	/// <inheritdoc />
	public SessionType Session => SessionType.Unknown;

	/// <inheritdoc />
	public PlatformCapabilities Capabilities { get; } = new()
	{
		SupportsGlobalCursor = false,
		SupportsWindowDrag = false,
		SupportsHitThrough = false,
		SupportsTopmost = false,
		SupportsTray = false,
	};

	/// <inheritdoc />
	public (double X, double Y) GetCursorPosition() =>
		throw new PlatformNotSupportedException("当前平台不支持读取全局光标位置");

	/// <inheritdoc />
	public void StartWindowDrag(nint windowHandle) =>
		throw new PlatformNotSupportedException("当前平台不支持原生窗口拖动");

	/// <inheritdoc />
	public void SetClickThrough(nint windowHandle, bool through)
	{
		// 不支持时静默忽略: 伴侣视窗已按能力标志降级为整窗可点
	}
}
