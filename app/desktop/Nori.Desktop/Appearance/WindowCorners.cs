using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Appearance;

/// <summary>
/// 无系统装饰的窗口在 Windows 11 上默认是直角；请求 DWM 圆角，与系统窗口及界面圆角令牌保持一致。
/// 桌宠与快捷对话是透明异形窗口，自行绘制形状，不参与。Windows 10 及其他平台不支持该属性，保持原样。
/// </summary>
internal sealed partial class WindowCorners : IDisposable
{
	private const int DwmwaWindowCornerPreference = 33;
	private const int DwmwcpRound = 2;
	private readonly IDisposable _openedSubscription;

	internal WindowCorners()
	{
		_openedSubscription = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Apply(window));
	}

	internal static bool IsEligible(Window window) => window is not PetWindow and not QuickChatWindow;

	internal static void Apply(Window window)
	{
		if (!IsEligible(window) || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
		if (window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND", Handle: not 0 } handle) return;
		int preference = DwmwcpRound;
		// 失败只意味着保持直角，不影响窗口功能。
		_ = DwmSetWindowAttribute(handle.Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
	}

	public void Dispose() => _openedSubscription.Dispose();

	[LibraryImport("dwmapi.dll")]
	[SupportedOSPlatform("windows10.0.22000")]
	private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
