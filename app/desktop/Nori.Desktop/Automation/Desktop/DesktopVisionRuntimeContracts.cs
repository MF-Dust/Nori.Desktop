using Nori.Core.Automation;
using Nori.Desktop.Automation.Windows;

namespace Nori.Desktop.Automation.Desktop;

/// <summary>桌面视觉运行器工厂收到的脱敏装配参数。</summary>
public sealed record DesktopVisionRunnerRequest(
	string TaskTitle,
	string Goal,
	nint TargetWindow,
	IDesktopVisionScreenshotSource ScreenshotSource,
	IDesktopVisionActionExecutor ActionExecutor,
	IDesktopVisionPlanner Planner,
	DesktopVisionApprovalCallback? ApprovalCallback,
	AutomationPolicy Policy,
	Action<DesktopVisionProgress>? Progress);

/// <summary>本地可见顶层窗口元数据。</summary>
public sealed record DesktopVisionWindow(nint Handle, string Title, int ProcessId, AutomationBounds Bounds, bool IsForeground);

/// <summary>桌面顶层窗口枚举入口；生产实现只在运行时读取窗口元数据。</summary>
public interface IDesktopVisionWindowCatalog
{
	/// <summary>读取当前可见顶层窗口；调用方不得把标题或进程信息带出本地边界。</summary>
	IReadOnlyList<DesktopVisionWindow> Enumerate();
}

/// <summary>现有 Windows 窗口 adapter 的桌面视觉枚举适配。</summary>
public sealed class WindowsDesktopVisionWindowCatalog : IDesktopVisionWindowCatalog
{
	private readonly WindowsWindowService _windows;

	/// <summary>创建窗口枚举适配。</summary>
	public WindowsDesktopVisionWindowCatalog(WindowsWindowService? windows = null) => _windows = windows ?? new WindowsWindowService();

	/// <inheritdoc />
	public IReadOnlyList<DesktopVisionWindow> Enumerate() => _windows.EnumerateTopLevelWindows()
		.Select(window => new DesktopVisionWindow(window.Handle, window.Title, window.ProcessId, window.Bounds, window.IsForeground))
		.ToArray();
}

/// <summary>桥接层允许读取的脱敏桌面窗口信息。</summary>
public sealed record AutomationDesktopWindowSnapshot(string Token, int Width, int Height, bool IsForeground);

/// <summary>桌面视觉任务启动结果；不包含任务正文或目标窗口句柄。</summary>
public sealed record AutomationDesktopTaskStartSnapshot(Guid TaskId, AutomationTaskStatusSnapshot Status);

/// <summary>公开给界面的脱敏桌面审批请求。</summary>
public sealed record AutomationDesktopApprovalSnapshot(
	Guid RequestId,
	Guid TaskId,
	IReadOnlyList<AutomationActionKind> ActionKinds);
