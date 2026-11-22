using Nori.Core.Vision;

namespace Nori.Desktop.Vision;

/// <summary>
/// 从按 z 序由前到后排列的可见窗口中选择目标；排除本进程，避免读到 Nori 自己的聊天窗口。
/// </summary>
internal static class ScreenTargetPicker
{
	/// <summary>
	/// z 序上第一个符合读屏条件的窗口。
	///
	/// 传入顺序即 z 序（<c>EnumWindows</c> 从上到下回调，服务层的过滤保持了这个顺序），
	/// 因此第一个符合条件的就是最靠前的那个。
	///
	/// 排除本进程是必需的：用户在聊天窗口里说「看看我的屏幕」时，前台窗口正是 Nori 自己，
	/// 取前台会让她对着自己的聊天记录作答。要的是被她挡住的那一个。
	/// </summary>
	internal static VisionWindow? Pick(IReadOnlyList<VisionWindow> windows, int selfProcessId) =>
		windows.FirstOrDefault(window => IsCandidate(window, selfProcessId));

	internal static VisionWindow? PickAt(IReadOnlyList<VisionWindow> windows, int x, int y, int selfProcessId) =>
		windows.FirstOrDefault(window => IsCandidate(window, selfProcessId) && window.Bounds.Contains(x, y));

	// 无标题浮层、单像素窗口和 Win32 标记为可见的 SOUI 占位窗口都不是读屏目标。
	private static bool IsCandidate(VisionWindow window, int selfProcessId) =>
		window.ProcessId != selfProcessId
		&& window.Bounds.Width > 1 && window.Bounds.Height > 1
		&& !string.IsNullOrWhiteSpace(window.Title)
		&& !window.Title.Equals("SOUI_DUMMY_WND", StringComparison.OrdinalIgnoreCase);
}
