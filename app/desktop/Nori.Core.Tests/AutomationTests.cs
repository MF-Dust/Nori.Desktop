using Nori.Core.Automation;

namespace Nori.Core.Tests;

/// <summary>自动化 Core 的策略、动作和任务生命周期测试。</summary>
public sealed class AutomationTests
{
	[Fact]
	public void 结构化动作校验白名单坐标和文本长度()
	{
		AutomationPolicy policy = new(AutomationCapability.Pointer | AutomationCapability.Keyboard, new AutomationBounds(10, 20, 100, 80), 4);
		Assert.Equal(new ClickAction(10, 99), AutomationAction.Parse("{\"type\":\"click\",\"x\":10,\"y\":99}", policy));
		Assert.False(AutomationAction.TryParse("{\"type\":\"click\",\"x\":110,\"y\":20}", policy, out _, out string? coordinateError));
		Assert.Contains("坐标", coordinateError, StringComparison.Ordinal);
		Assert.False(AutomationAction.TryParse("{\"type\":\"type_text\",\"text\":\"12345\"}", policy, out _, out string? textError));
		Assert.Contains("长度", textError, StringComparison.Ordinal);
		Assert.False(AutomationAction.TryParse("{\"type\":\"launch_process\"}", policy, out _, out string? actionError));
		Assert.Contains("白名单", actionError, StringComparison.Ordinal);
	}

	[Fact]
	public void 不具备能力或键不在白名单时拒绝()
	{
		AutomationPolicy pointerOnly = new(AutomationCapability.Pointer, new AutomationBounds(0, 0, 100, 100));
		Assert.False(AutomationAction.TryParse("{\"type\":\"type_text\",\"text\":\"ok\"}", pointerOnly, out _, out string? error));
		Assert.Contains("不允许", error, StringComparison.Ordinal);
		AutomationPolicy keyboard = new(AutomationCapability.Keyboard, new AutomationBounds(0, 0, 100, 100));
		Assert.False(AutomationAction.TryParse("{\"type\":\"key_press\",\"key\":\"F13\"}", keyboard, out _, out error));
		Assert.Contains("白名单", error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 取消终止状态且迟到完成不能覆盖取消()
	{
		await using AutomationTaskManager manager = new();
		TaskCompletionSource<bool> started = Signal();
		TaskCompletionSource<bool> release = Signal();
		AutomationTask task = manager.Enqueue(async _ => { started.SetResult(true); await release.Task; });
		try
		{
			await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.True(manager.Cancel(task.Id));
			Assert.Equal(AutomationTaskState.Cancelled, (await task.Completion.WaitAsync(TimeSpan.FromSeconds(5))).State);
			AutomationTask sentinel = manager.Enqueue(_ => Task.CompletedTask);
			release.SetResult(true);
			Assert.Equal(AutomationTaskState.Completed, (await sentinel.Completion.WaitAsync(TimeSpan.FromSeconds(5))).State);
			Assert.Equal(AutomationTaskState.Cancelled, task.State);
		}
		finally
		{
			release.TrySetResult(true);
		}
	}

	[Fact]
	public async Task 活动任务运行时可取消排队任务()
	{
		await using AutomationTaskManager manager = new();
		TaskCompletionSource<bool> started = Signal();
		TaskCompletionSource<bool> release = Signal();
		AutomationTask first = manager.Enqueue(async _ => { started.SetResult(true); await release.Task; });
		try
		{
			await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
			AutomationTask second = manager.Enqueue(_ => Task.CompletedTask);
			Assert.Equal(1, manager.QueuedCount);
			Assert.True(manager.Cancel(second));
			Assert.Equal(AutomationTaskState.Cancelled, (await second.Completion).State);
			release.SetResult(true);
			Assert.Equal(AutomationTaskState.Completed, (await first.Completion.WaitAsync(TimeSpan.FromSeconds(5))).State);
		}
		finally
		{
			release.TrySetResult(true);
		}
	}

	[Fact]
	public async Task 并发取消保持任务终态()
	{
		await using AutomationTaskManager manager = new(64);
		TaskCompletionSource<bool> started = Signal();
		TaskCompletionSource<bool> release = Signal();
		AutomationTask active = manager.Enqueue(async _ => { started.SetResult(true); await release.Task; });
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		AutomationTask[] queued = Enumerable.Range(0, 20).Select(_ => manager.Enqueue(_ => Task.CompletedTask)).ToArray();
		Parallel.ForEach(queued, task => manager.Cancel(task.Id));
		release.SetResult(true);
		await active.Completion.WaitAsync(TimeSpan.FromSeconds(5));
		AutomationTaskSnapshot[] snapshots = await Task.WhenAll(queued.Select(task => task.Completion));
		Assert.All(snapshots, snapshot => Assert.Equal(AutomationTaskState.Cancelled, snapshot.State));
	}

	[Fact]
	public async Task 安全暂停复用任务管理器且可取消()
	{
		await using AutomationTaskManager manager = new();
		AutomationTask task = manager.Enqueue(_ => throw new AutomationTaskPausedException("safe_page", TimeSpan.FromSeconds(5)));
		for (int attempt = 0; attempt < 100 && task.State != AutomationTaskState.Paused; attempt++) await Task.Delay(10);

		Assert.Equal(AutomationTaskState.Paused, task.State);
		Assert.Equal("safe_page", task.Snapshot.PauseReason);
		Assert.True(manager.Cancel(task.Id));
		Assert.Equal(AutomationTaskState.Cancelled, (await task.Completion.WaitAsync(TimeSpan.FromSeconds(5))).State);
	}

	[Fact]
	public void 审批DTO仅包含脱敏标识和动作种类()
	{
		Guid taskId = Guid.NewGuid();
		AutomationApprovalRequest request = new(Guid.NewGuid(), taskId, [AutomationActionKind.Click, AutomationActionKind.TypeText], DateTimeOffset.UtcNow);
		AutomationApprovalDecision decision = AutomationApprovalDecision.Create(request, AutomationApprovalOutcome.Approved, DateTimeOffset.UtcNow);
		Assert.Equal(taskId, request.TaskId);
		Assert.Equal(2, request.ActionKinds.Count);
		Assert.Equal(request.RequestId, decision.RequestId);
	}

	private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
