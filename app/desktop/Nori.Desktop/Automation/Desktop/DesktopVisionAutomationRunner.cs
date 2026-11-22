using Nori.Core.Automation;
using Nori.Core.Chat;

namespace Nori.Desktop.Automation.Desktop;

/// <summary>
/// 独立的桌面视觉执行器。
/// 每一步只在内存中保留当前截图和模型结果，所有对外状态均为脱敏稳定类别。
/// </summary>
public sealed class DesktopVisionAutomationRunner : IAutomationTaskRunner
{
	private readonly string _taskTitle;
	private readonly string _goal;
	private readonly nint _targetWindow;
	private readonly IDesktopVisionScreenshotSource _screenshotSource;
	private readonly IDesktopVisionActionExecutor _actionExecutor;
	private readonly IDesktopVisionPlanner _planner;
	private readonly DesktopVisionApprovalCallback? _approvalCallback;
	private readonly AutomationPolicy _policy;
	private readonly DesktopVisionAutomationOptions _options;
	private readonly Action<DesktopVisionProgress>? _progress;

	/// <summary>创建可注入的桌面视觉执行器。</summary>
	public DesktopVisionAutomationRunner(
		string taskTitle,
		string goal,
		nint targetWindow,
		IDesktopVisionScreenshotSource screenshotSource,
		IDesktopVisionActionExecutor actionExecutor,
		IDesktopVisionPlanner planner,
		DesktopVisionApprovalCallback? approvalCallback = null,
		AutomationPolicy? policy = null,
		DesktopVisionAutomationOptions? options = null,
		Action<DesktopVisionProgress>? progress = null)
	{
		_options = options ?? new DesktopVisionAutomationOptions();
		_options.Validate();
		_taskTitle = taskTitle.Trim();
		_goal = goal.Trim();
		_targetWindow = targetWindow;
		_screenshotSource = screenshotSource;
		_actionExecutor = actionExecutor;
		_planner = planner;
		_approvalCallback = approvalCallback;
		_policy = policy ?? AutomationPolicy.Default;
		_progress = progress;
	}

	/// <summary>
	/// 执行任务并返回不含敏感内容的结果。
	/// 该方法是测试和未来宿主接线使用的详细入口。
	/// </summary>
	public async Task<DesktopVisionAutomationResult> ExecuteAsync(AutomationTaskContext context, CancellationToken cancellationToken = default)
	{
		Report(0, DesktopVisionAutomationCategory.Running);
		if (context.TaskId == Guid.Empty || _targetWindow == 0 || string.IsNullOrWhiteSpace(_taskTitle) || string.IsNullOrWhiteSpace(_goal))
			return Finish(0, DesktopVisionAutomationCategory.InvalidInput);

		using CancellationTokenSource timeout = new();
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
		timeout.CancelAfter(_options.Timeout);
		string plannerPrompt = BuildPlannerPrompt();

		for (int step = 1; step <= _options.MaxSteps; step++)
		{
			if (GetCancellationCategory(cancellationToken, timeout.Token) is { } beforeCapture)
				return Finish(step, beforeCapture);

			DesktopVisionScreenshotResult? capture;
			try
			{
				capture = await _screenshotSource.CaptureAsync(_targetWindow, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return Finish(step, GetCancellationCategory(cancellationToken, timeout.Token) ?? DesktopVisionAutomationCategory.Cancelled);
			}
			catch (Exception)
			{
				return Finish(step, DesktopVisionAutomationCategory.ScreenshotFailed);
			}

			if (GetCancellationCategory(cancellationToken, timeout.Token) is { } afterCapture)
				return Finish(step, afterCapture);
			if (capture is null) return Finish(step, DesktopVisionAutomationCategory.ScreenshotFailed);
			if (capture.Status == DesktopVisionScreenshotStatus.TargetNotForeground)
				return Finish(step, DesktopVisionAutomationCategory.TargetNotForeground);
			if (capture.Status != DesktopVisionScreenshotStatus.Succeeded || capture.Screenshot is null)
				return Finish(step, DesktopVisionAutomationCategory.ScreenshotFailed);

			ChatMessageInput message;
			try
			{
				ChatImagePart image = new(capture.Screenshot.Data, capture.Screenshot.MimeType);
				message = new ChatMessageInput
				{
					Role = "user",
					Content = plannerPrompt,
					ImageParts = [image],
				};
			}
			catch (Exception)
			{
				return Finish(step, DesktopVisionAutomationCategory.ScreenshotFailed);
			}

			string? rawPlan;
			try
			{
				rawPlan = await _planner.PlanAsync([message], linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return Finish(step, GetCancellationCategory(cancellationToken, timeout.Token) ?? DesktopVisionAutomationCategory.Cancelled);
			}
			catch (Exception)
			{
				return Finish(step, DesktopVisionAutomationCategory.PlannerFailed);
			}

			if (GetCancellationCategory(cancellationToken, timeout.Token) is { } afterPlan)
				return Finish(step, afterPlan);
			if (!DesktopVisionPlanParser.TryParse(rawPlan, _options.MaxPlanCharacters, out bool completed, out AutomationAction? action))
				return Finish(step, DesktopVisionAutomationCategory.InvalidAction);
			if (completed) return Finish(step, DesktopVisionAutomationCategory.Completed);
			if (action is null) return Finish(step, DesktopVisionAutomationCategory.InvalidAction);
			if (!_policy.TryValidate(action, out _)) return Finish(step, DesktopVisionAutomationCategory.PolicyRejected);

			if (RequiresApproval(action))
			{
				DesktopVisionAutomationCategory? approvalResult = await ApproveAsync(context.TaskId, action.Kind, linked.Token, cancellationToken, timeout.Token).ConfigureAwait(false);
				if (approvalResult is not null) return Finish(step, approvalResult.Value);
			}

			if (GetCancellationCategory(cancellationToken, timeout.Token) is { } beforeExecution)
				return Finish(step, beforeExecution);

			DesktopVisionActionResult? execution;
			try
			{
				execution = await _actionExecutor.ExecuteAsync(_targetWindow, action, _policy, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return Finish(step, GetCancellationCategory(cancellationToken, timeout.Token) ?? DesktopVisionAutomationCategory.Cancelled);
			}
			catch (Exception)
			{
				return Finish(step, DesktopVisionAutomationCategory.ExecutionFailed);
			}

			if (GetCancellationCategory(cancellationToken, timeout.Token) is { } afterExecution)
				return Finish(step, afterExecution);
			if (execution is null || execution.Status == DesktopVisionActionStatus.Failed)
				return Finish(step, DesktopVisionAutomationCategory.ExecutionFailed);
			if (execution.Status == DesktopVisionActionStatus.TargetNotForeground)
				return Finish(step, DesktopVisionAutomationCategory.TargetNotForeground);

			Report(step, DesktopVisionAutomationCategory.StepSucceeded);
			if (step == _options.MaxSteps) return Finish(step, DesktopVisionAutomationCategory.StepLimitExceeded);
		}

		return Finish(_options.MaxSteps, DesktopVisionAutomationCategory.StepLimitExceeded);
	}

	/// <summary>公开的详细运行入口；只返回稳定类别和步数。</summary>
	public Task<DesktopVisionAutomationResult> RunAsync(AutomationTaskContext context, CancellationToken cancellationToken = default) =>
		ExecuteAsync(context, cancellationToken);

	/// <inheritdoc />
	async Task IAutomationTaskRunner.RunAsync(AutomationTaskContext context, CancellationToken cancellationToken)
	{
		await ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
	}

	private async Task<DesktopVisionAutomationCategory?> ApproveAsync(
		Guid taskId,
		AutomationActionKind actionKind,
		CancellationToken linkedToken,
		CancellationToken callerToken,
		CancellationToken timeoutToken)
	{
		if (GetCancellationCategory(callerToken, timeoutToken) is { } beforeApproval)
			return beforeApproval;
		if (_approvalCallback is null) return DesktopVisionAutomationCategory.ApprovalDenied;
		AutomationApprovalRequest request = new(Guid.NewGuid(), taskId, [actionKind], DateTimeOffset.UtcNow);
		AutomationApprovalDecision? decision;
		try
		{
			decision = await _approvalCallback(request, linkedToken).WaitAsync(linkedToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return GetCancellationCategory(callerToken, timeoutToken) ?? DesktopVisionAutomationCategory.Cancelled;
		}
		catch (Exception)
		{
			return DesktopVisionAutomationCategory.ApprovalFailed;
		}

		if (GetCancellationCategory(callerToken, timeoutToken) is { } cancellation)
			return cancellation;
		if (decision is null || decision.RequestId != request.RequestId || decision.Outcome != AutomationApprovalOutcome.Approved)
			return DesktopVisionAutomationCategory.ApprovalDenied;
		return null;
	}

	private string BuildPlannerPrompt() =>
		$"任务标题：{_taskTitle}\n任务目标：{_goal}\n" +
		"请观察附带的当前窗口截图。每次只能返回一个 JSON 对象：" +
		"需要动作时返回白名单动作；任务完成时只返回 {\"status\":\"completed\"}。" +
		"禁止 Markdown、代码、数组和额外字段。";

	private static bool RequiresApproval(AutomationAction action) =>
		// 当前 Core 白名单尚无拖拽动作；未来新增拖拽类型时必须在此处归入高风险审批。
		action is TypeTextAction or KeyPressAction;

	private static DesktopVisionAutomationCategory? GetCancellationCategory(CancellationToken callerToken, CancellationToken timeoutToken)
	{
		if (callerToken.IsCancellationRequested) return DesktopVisionAutomationCategory.Cancelled;
		if (timeoutToken.IsCancellationRequested) return DesktopVisionAutomationCategory.Timeout;
		return null;
	}

	private DesktopVisionAutomationResult Finish(int step, DesktopVisionAutomationCategory category)
	{
		Report(step, category);
		return new(category, step);
	}

	private void Report(int step, DesktopVisionAutomationCategory category)
	{
		try { _progress?.Invoke(new DesktopVisionProgress(step, category)); }
		catch { /* 状态回调不能改变执行结果，也不能把敏感异常带出执行器。 */ }
	}
}
