using Nori.Core.Automation;
using Nori.Core.Chat;

namespace Nori.Desktop.Automation.Desktop;

/// <summary>截图源的脱敏返回类别。</summary>
public enum DesktopVisionScreenshotStatus
{
	/// <summary>截图已成功捕获。</summary>
	Succeeded,
	/// <summary>目标窗口已经失去前台。</summary>
	TargetNotForeground,
	/// <summary>截图失败。</summary>
	Failed,
}

/// <summary>截图源返回的内存截图。</summary>
public sealed record DesktopVisionScreenshot(byte[] Data, string MimeType);

/// <summary>截图源结果；失败结果不携带底层错误文本。</summary>
public sealed record DesktopVisionScreenshotResult(DesktopVisionScreenshotStatus Status, DesktopVisionScreenshot? Screenshot)
{
	/// <summary>创建成功结果。</summary>
	public static DesktopVisionScreenshotResult Succeeded(DesktopVisionScreenshot screenshot)
	{
		return new(DesktopVisionScreenshotStatus.Succeeded, screenshot);
	}

	/// <summary>目标失去前台的结果。</summary>
	public static DesktopVisionScreenshotResult TargetNotForeground { get; } = new(DesktopVisionScreenshotStatus.TargetNotForeground, null);

	/// <summary>截图失败的结果。</summary>
	public static DesktopVisionScreenshotResult Failed { get; } = new(DesktopVisionScreenshotStatus.Failed, null);
}

/// <summary>桌面截图源；实现负责在每次捕获时重新校验目标窗口。</summary>
public interface IDesktopVisionScreenshotSource
{
	/// <summary>捕获目标窗口的当前截图，不写入文件。</summary>
	Task<DesktopVisionScreenshotResult> CaptureAsync(nint targetWindow, CancellationToken cancellationToken = default);
}

/// <summary>动作执行器的脱敏返回类别。</summary>
public enum DesktopVisionActionStatus
{
	/// <summary>动作已执行。</summary>
	Succeeded,
	/// <summary>执行前发现目标失去前台。</summary>
	TargetNotForeground,
	/// <summary>动作执行失败。</summary>
	Failed,
}

/// <summary>动作执行结果；失败结果不携带动作正文或底层错误文本。</summary>
public sealed record DesktopVisionActionResult(DesktopVisionActionStatus Status)
{
	/// <summary>成功结果。</summary>
	public static DesktopVisionActionResult Succeeded { get; } = new(DesktopVisionActionStatus.Succeeded);

	/// <summary>目标失去前台的结果。</summary>
	public static DesktopVisionActionResult TargetNotForeground { get; } = new(DesktopVisionActionStatus.TargetNotForeground);

	/// <summary>执行失败的结果。</summary>
	public static DesktopVisionActionResult Failed { get; } = new(DesktopVisionActionStatus.Failed);
}

/// <summary>桌面动作执行器；调用方只传入已经通过策略校验的动作。</summary>
public interface IDesktopVisionActionExecutor
{
	/// <summary>在指定目标窗口上执行一个白名单动作。</summary>
	Task<DesktopVisionActionResult> ExecuteAsync(
		nint targetWindow,
		AutomationAction action,
		AutomationPolicy policy,
		CancellationToken cancellationToken = default);
}

/// <summary>当前聊天模型的视觉规划器。</summary>
public interface IDesktopVisionPlanner
{
	/// <summary>根据单轮脱敏任务消息和截图返回严格 JSON 文本。</summary>
	Task<string> PlanAsync(IReadOnlyList<ChatMessageInput> messages, CancellationToken cancellationToken = default);
}

/// <summary>桌面视觉高风险动作审批回调。</summary>
public delegate Task<AutomationApprovalDecision> DesktopVisionApprovalCallback(
	AutomationApprovalRequest request,
	CancellationToken cancellationToken);

/// <summary>执行器对外报告的稳定类别。</summary>
public enum DesktopVisionAutomationCategory
{
	/// <summary>正在开始或等待下一步。</summary>
	Running,
	/// <summary>一个动作步骤已成功执行。</summary>
	StepSucceeded,
	/// <summary>任务已完成。</summary>
	Completed,
	/// <summary>调用方取消了任务。</summary>
	Cancelled,
	/// <summary>任务超过时间上限。</summary>
	Timeout,
	/// <summary>目标窗口不是当前前台窗口。</summary>
	TargetNotForeground,
	/// <summary>截图失败或图片超出消息边界。</summary>
	ScreenshotFailed,
	/// <summary>规划器调用失败。</summary>
	PlannerFailed,
	/// <summary>规划器返回了非法动作或非法结构化结果。</summary>
	InvalidAction,
	/// <summary>动作未通过自动化策略。</summary>
	PolicyRejected,
	/// <summary>用户拒绝或审批决定无效。</summary>
	ApprovalDenied,
	/// <summary>审批回调调用失败。</summary>
	ApprovalFailed,
	/// <summary>动作执行失败。</summary>
	ExecutionFailed,
	/// <summary>达到最大步骤数仍未完成。</summary>
	StepLimitExceeded,
	/// <summary>任务输入或任务上下文无效。</summary>
	InvalidInput,
}

/// <summary>脱敏执行进度；不包含提示词、截图、动作或模型原文。</summary>
public sealed record DesktopVisionProgress(int Step, DesktopVisionAutomationCategory Category);

/// <summary>桌面视觉执行最终结果；只包含稳定类别和步数。</summary>
public sealed record DesktopVisionAutomationResult(DesktopVisionAutomationCategory Category, int Steps)
{
	/// <summary>任务是否成功完成。</summary>
	public bool Succeeded => Category == DesktopVisionAutomationCategory.Completed;

	/// <summary>供状态层使用的稳定英文类别，不包含任何输入内容。</summary>
	public string StableCategory => Category switch
	{
		DesktopVisionAutomationCategory.Running => "running",
		DesktopVisionAutomationCategory.StepSucceeded => "step_succeeded",
		DesktopVisionAutomationCategory.Completed => "completed",
		DesktopVisionAutomationCategory.Cancelled => "cancelled",
		DesktopVisionAutomationCategory.Timeout => "timeout",
		DesktopVisionAutomationCategory.TargetNotForeground => "target_not_foreground",
		DesktopVisionAutomationCategory.ScreenshotFailed => "screenshot_failed",
		DesktopVisionAutomationCategory.PlannerFailed => "planner_failed",
		DesktopVisionAutomationCategory.InvalidAction => "invalid_action",
		DesktopVisionAutomationCategory.PolicyRejected => "policy_rejected",
		DesktopVisionAutomationCategory.ApprovalDenied => "approval_denied",
		DesktopVisionAutomationCategory.ApprovalFailed => "approval_failed",
		DesktopVisionAutomationCategory.ExecutionFailed => "execution_failed",
		DesktopVisionAutomationCategory.StepLimitExceeded => "step_limit_exceeded",
		DesktopVisionAutomationCategory.InvalidInput => "invalid_input",
		_ => "unknown",
	};
}

/// <summary>桌面视觉执行上限；任何实例都不能放宽产品安全边界。</summary>
public sealed record DesktopVisionAutomationOptions
{
	/// <summary>单次任务允许的最大步骤数。</summary>
	public const int MaximumSteps = 20;

	/// <summary>单次任务允许的最大持续时间。</summary>
	public static TimeSpan MaximumDuration { get; } = TimeSpan.FromSeconds(120);

	/// <summary>本次任务的步骤上限，可为测试设置更小的值。</summary>
	public int MaxSteps { get; init; } = MaximumSteps;

	/// <summary>本次任务的超时时间，可为测试设置更短的值。</summary>
	public TimeSpan Timeout { get; init; } = MaximumDuration;

	/// <summary>单次规划器返回文本的字符上限。</summary>
	public int MaxPlanCharacters { get; init; } = 16 * 1024;

	internal void Validate()
	{
		if (MaxSteps is < 1 or > MaximumSteps) throw new ArgumentOutOfRangeException(nameof(MaxSteps));
		if (Timeout <= TimeSpan.Zero || Timeout > MaximumDuration) throw new ArgumentOutOfRangeException(nameof(Timeout));
		if (MaxPlanCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(MaxPlanCharacters));
	}
}
