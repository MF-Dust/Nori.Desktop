using System.Text;
using System.Text.Json;
using Nori.Core.Agent;

namespace Nori.Core.Tests;

/// <summary>执行记录的归属、终态、容量和隐私边界。</summary>
public sealed class AgentExecutionJournalTests
{
	[Fact]
	public void 按请求轮次和调用标识归属且迟到完成不改写其他调用()
	{
		using AgentExecutionJournal journal = new();
		AgentExecutionRequest first = journal.Open("session-a");
		AgentExecutionRequest second = journal.Open("session-a");
		Assert.NotEqual(first.Id, second.Id);
		first.ToolPending(1, "call-a", "readFile");
		first.ToolStarted(1, "call-a", "readFile");
		second.ToolPending(1, "call-b", "readFile");
		second.ToolStarted(1, "call-b", "readFile");

		Assert.True(first.FinishTool(1, "call-a", "readFile", 12, AgentExecutionStatus.Success, null, true));
		Assert.False(journal.TryFinish(second.Id, 1, "call-a", "readFile", 4, AgentExecutionStatus.Failed, "tool_error", false));
		Assert.False(journal.TryFinish(first.Id, 2, "call-a", "readFile", 4, AgentExecutionStatus.Cancelled, "cancelled", false));
		Assert.True(second.FinishTool(1, "call-b", "readFile", 8, AgentExecutionStatus.Success, null, true));

		AgentExecutionEvent[] events = journal.Snapshot().Events.ToArray();
		AgentExecutionEvent firstDone = Applied(events, first.Id, AgentExecutionStatus.Success);
		Assert.True(firstDone.SideEffectsCommitted);
		Assert.Equal(1, firstDone.Round);
		Assert.Contains(events, ev => ev.Late && ev.RequestId == second.Id && ev.Status == AgentExecutionStatus.Failed);
		Assert.Contains(events, ev => ev.Late && ev.RequestId == first.Id && ev.Round == 2);
		Assert.Equal(2, journal.Snapshot().Summary.RealExecutions);
	}

	[Fact]
	public void 拒绝保持拒绝且取消不能改成成功()
	{
		using AgentExecutionJournal journal = new();
		AgentExecutionRequest denied = journal.Open("session");
		denied.ToolPending(0, "call-deny", "writeFile");
		denied.ToolStarted(0, "call-deny", "writeFile");
		denied.Permission(0, "call-deny", "writeFile", "confirm", AgentExecutionStatus.Pending, "requested");
		denied.Permission(0, "call-deny", "writeFile", "confirm", AgentExecutionStatus.Denied, "denied");
		Assert.True(denied.FinishTool(0, "call-deny", "writeFile", 3, AgentExecutionStatus.Denied, "denied", false));
		Assert.False(denied.FinishTool(0, "call-deny", "writeFile", 3, AgentExecutionStatus.Success, null, true));

		AgentExecutionRequest cancelled = journal.Open("session");
		cancelled.ToolStarted(0, "call-cancel", "runTask");
		Assert.True(cancelled.FinishTool(0, "call-cancel", "runTask", 5, AgentExecutionStatus.Cancelled, "cancelled", false));
		Assert.False(journal.TryFinish(cancelled.Id, 0, "call-cancel", "runTask", 9, AgentExecutionStatus.Success, null, true));

		AgentExecutionEvent[] events = journal.Snapshot().Events.ToArray();
		Assert.False(Applied(events, denied.Id, AgentExecutionStatus.Denied).SideEffectsCommitted);
		Assert.Contains(events, ev => ev.Kind == AgentExecutionKind.Permission && ev.Status == AgentExecutionStatus.Denied && !ev.Late);
		Assert.False(Applied(events, cancelled.Id, AgentExecutionStatus.Cancelled).SideEffectsCommitted);
		Assert.DoesNotContain(events, ev => ev.RequestId == cancelled.Id && ev.Status == AgentExecutionStatus.Success && !ev.Late);
	}

	[Fact]
	public void 已完成的调用不会被事后取消写成撤销()
	{
		using AgentExecutionJournal journal = new();
		AgentExecutionRequest request = journal.Open("session");
		request.ToolStarted(0, "call-done", "writeFile");
		Assert.True(request.FinishTool(0, "call-done", "writeFile", 15, AgentExecutionStatus.Success, null, true));
		request.End(20, AgentExecutionStatus.Cancelled, "cancelled");
		Assert.False(journal.TryFinish(request.Id, 0, "call-done", "writeFile", 1, AgentExecutionStatus.Cancelled, "cancelled", false));

		AgentExecutionEvent[] events = journal.Snapshot().Events.ToArray();
		AgentExecutionEvent done = Applied(events, request.Id, AgentExecutionStatus.Success);
		Assert.True(done.SideEffectsCommitted);
		Assert.Contains(events, ev => ev.Kind == AgentExecutionKind.Session && ev.Status == AgentExecutionStatus.Cancelled && !ev.Late);
		Assert.Contains(events, ev => ev.Late && ev.ToolCallId == done.ToolCallId && ev.Status == AgentExecutionStatus.Cancelled);
		Assert.DoesNotContain(events, ev => ev.ToolCallId == done.ToolCallId && ev.Status == AgentExecutionStatus.Cancelled && !ev.Late);
	}

	[Fact]
	public void 重复调用只标记一次真实执行()
	{
		using AgentExecutionJournal journal = new();
		AgentExecutionRequest request = journal.Open("session");
		Assert.True(request.ToolPending(0, "same", "search"));
		Assert.False(request.ToolPending(0, "same", "search"));
		request.ToolStarted(0, "same", "search");
		request.Deduplicated(0, "same", "search");
		Assert.True(request.FinishTool(0, "same", "search", 6, AgentExecutionStatus.Success, null, true));
		request.ToolPending(1, "same", "search");
		request.Deduplicated(1, "same", "search");
		request.End(10, AgentExecutionStatus.Success, null);

		AgentExecutionSnapshot snapshot = journal.Snapshot();
		Assert.Equal(1, snapshot.Summary.RealExecutions);
		Assert.Equal(2, snapshot.Summary.Deduplicated);
		Assert.DoesNotContain(snapshot.Events, ev => ev.Round == 1 && ev.Status == AgentExecutionStatus.Success && !ev.Deduplicated);
		Assert.DoesNotContain(snapshot.Events, ev => ev.FailureCategory == "not_started" && ev.Round == 1);
	}

	[Fact]
	public void 未开始的排队在会话结束时取消且不宣称已执行()
	{
		using AgentExecutionJournal journal = new();
		AgentExecutionRequest request = journal.Open("session");
		request.ToolPending(0, "queued", "later");
		request.End(4, AgentExecutionStatus.Cancelled, "cancelled");

		AgentExecutionEvent cancelled = Assert.Single(journal.Snapshot().Events, ev =>
			ev.Kind == AgentExecutionKind.Tool && ev.Status == AgentExecutionStatus.Cancelled && ev.FailureCategory == "not_started");
		Assert.Equal("not_started", cancelled.FailureCategory);
		Assert.False(cancelled.SideEffectsCommitted);
		Assert.Equal(0, journal.Snapshot().Summary.RealExecutions);
	}

	[Fact]
	public void 容量和单条字节都会淘汰过长内容()
	{
		using AgentExecutionJournal journal = new(capacity: 2, maxEventBytes: 96, maxRetainedBytes: 96 * 4);
		journal.Open("one");
		journal.Open("two");
		AgentExecutionRequest third = journal.Open("three");
		third.ToolStarted(0, "call", new string('测', 400) + " token=secret-value");

		AgentExecutionSnapshot snapshot = journal.Snapshot();
		Assert.True(snapshot.Events.Count <= 2);
		Assert.DoesNotContain(snapshot.Events, ev => ev.SessionId == "one");
		string json = JsonSerializer.Serialize(snapshot);
		Assert.DoesNotContain("secret-value", json, StringComparison.Ordinal);
		Assert.DoesNotContain(new string('测', 80), json, StringComparison.Ordinal);
	}

	[Fact]
	public void 快照不包含正文密钥路径或异常消息()
	{
		using AgentExecutionJournal journal = new();
		AgentExecutionRequest request = journal.Open("session api_key=secret-value C:\\Users\\Nori\\chat.log");
		request.Model(0, 30, AgentExecutionStatus.Success, null, null);
		request.ToolStarted(0, "id:" + new string('x', 300) + "\n/home/user/secret.txt", "tool\r\ntoken=another-secret");
		request.FinishTool(0, "id:" + new string('x', 300) + "\n/home/user/secret.txt", "tool", 1,
			AgentExecutionStatus.Failed, "NullReferenceException: /home/user/secret.txt api_key=secret-value", false);
		request.End(40, AgentExecutionStatus.Failed, "exception");

		string json = JsonSerializer.Serialize(journal.Snapshot());
		Assert.DoesNotContain("secret-value", json, StringComparison.Ordinal);
		Assert.DoesNotContain("another-secret", json, StringComparison.Ordinal);
		Assert.DoesNotContain("/home/user", json, StringComparison.Ordinal);
		Assert.DoesNotContain("C:\\Users\\Nori", json, StringComparison.Ordinal);
		Assert.DoesNotContain("NullReferenceException", json, StringComparison.Ordinal);
		Assert.Contains("unclassified", json, StringComparison.Ordinal);
		Assert.Null(journal.Snapshot().Summary.PromptTokens);
		Assert.Equal(30, journal.Snapshot().Summary.ModelDurationMs);
	}

	[Fact]
	public void 没有上报用量时不生成命中率()
	{
		using AgentExecutionJournal journal = new();
		AgentExecutionRequest request = journal.Open("session");
		request.Model(0, 12, AgentExecutionStatus.Success, null, new AgentTraceUsage(0, 0, 0, 0, 0, "model"));
		request.Model(1, 8, AgentExecutionStatus.Success, null, new AgentTraceUsage(10, 4, 14, 2, 20, "model"));
		AgentExecutionSnapshot snapshot = journal.Snapshot();
		AgentExecutionUsage first = Assert.IsType<AgentExecutionUsage>(snapshot.Events.Single(ev => ev.Round == 0 && ev.Kind == AgentExecutionKind.Model).Usage);
		AgentExecutionUsage second = Assert.IsType<AgentExecutionUsage>(snapshot.Events.Single(ev => ev.Round == 1 && ev.Kind == AgentExecutionKind.Model).Usage);
		Assert.Null(first.CacheHitRate);
		Assert.Equal(20, second.CacheHitRate);
		Assert.Equal(14, snapshot.Summary.TotalTokens);
		Assert.Equal(2, snapshot.Summary.ReportedUsageRounds);
	}

	[Fact]
	public void 释放后不再通知且不再记录()
	{
		AgentExecutionJournal journal = new();
		int notices = 0;
		journal.Changed += () => notices++;
		journal.Open("session");
		Assert.True(notices > 0);
		Assert.Equal(1, journal.ListenerCount);
		int after = notices;
		journal.Dispose();
		Assert.Equal(0, journal.ListenerCount);
		Assert.Same(AgentExecutionRequest.Inactive, journal.Open("session"));
		Assert.Equal(after, notices);
	}

	[Fact]
	public void 并发写入仍然有界()
	{
		using AgentExecutionJournal journal = new(capacity: 32, maxEventBytes: 256, maxRetainedBytes: 16 * 1024);
		Parallel.For(0, 2, requestIndex =>
		{
			AgentExecutionRequest request = journal.Open("session-" + requestIndex);
			for (int call = 0; call < 40; call++)
			{
				string id = "call-" + requestIndex + "-" + call;
				request.ToolStarted(call, id, "tool");
				request.FinishTool(call, id, "tool", 1, AgentExecutionStatus.Success, null, true);
			}
			request.End(5, AgentExecutionStatus.Success, null);
		});
		Assert.InRange(journal.Snapshot().Events.Count, 1, 32);
	}

	private static AgentExecutionEvent Applied(IReadOnlyList<AgentExecutionEvent> events, string requestId, AgentExecutionStatus status) =>
		Assert.Single(events, ev => ev.RequestId == requestId && ev.Status == status && !ev.Late && ev.Kind == AgentExecutionKind.Tool);
}
