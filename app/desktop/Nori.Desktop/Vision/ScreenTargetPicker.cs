using Nori.Desktop.Automation.Windows;

namespace Nori.Desktop.Vision;

/// <summary>
/// 从候选窗口里挑出要截的那一个。
///
/// 单独成类且**不带平台特性**：这段是纯粹的选择逻辑，与任何 Win32 调用无关。放在
/// <see cref="WindowsScreenCapture"/> 里的话它会继承那个类的 <c>SupportedOSPlatform</c>，
/// 测试跟着只能在 Windows 上跑 —— 而「不能截到自己」恰恰是这里最容易写错、最该每个平台
/// 都跑到的一条。上一版就是这么错的：本地全绿，linux 与 macOS 的 CI 全红。
/// </summary>
internal static class ScreenTargetPicker
{
	/// <summary>
	/// z 序上第一个不属于本进程、且有标题的窗口。
	///
	/// 传入顺序即 z 序（<c>EnumWindows</c> 从上到下回调，服务层的过滤保持了这个顺序），
	/// 因此第一个符合条件的就是最靠前的那个。
	///
	/// 排除本进程是必需的：用户在聊天窗口里说「看看我的屏幕」时，前台窗口正是 Nori 自己，
	/// 取前台会让她对着自己的聊天记录作答。要的是被她挡住的那一个。
	///
	/// 无标题窗口跳过：多是工具提示、输入法候选框一类的浮层，截了没有意义。
	/// </summary>
	internal static WindowsTopLevelWindow? Pick(IReadOnlyList<WindowsTopLevelWindow> windows, int selfProcessId)
	{
		ArgumentNullException.ThrowIfNull(windows);
		return windows.FirstOrDefault(
			window => window.ProcessId != selfProcessId && window.Title.Trim().Length > 0);
	}
}
