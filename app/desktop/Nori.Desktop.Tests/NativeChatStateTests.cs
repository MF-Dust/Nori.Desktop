using System.Text.Json;
using Avalonia.Input;
using Nori.Desktop.Chat;

namespace Nori.Desktop.Tests;

public sealed class NativeChatStateTests
{
	private static JsonElement Event(object value) => JsonSerializer.SerializeToElement(value);

	[Fact]
	public void EarlyEventsReplayOnlyForTheReturnedSessionAndCompleteReplacesPreview()
	{
		NativeChatState state = new() { Draft = "  你好  " };
		Assert.True(state.BeginSend(state.Draft)); Assert.Equal("", state.Draft);
		state.ApplyEvent(Event(new { type = "chunk", sessionId = "other", chunk = "错误会话" }));
		state.ApplyEvent(Event(new { type = "chunk", sessionId = "active", chunk = "流式预览" }));
		state.ApplyEvent(Event(new { type = "complete", sessionId = "active", message = new { text = "最终正文" } }));
		Assert.True(state.Sending); state.AttachSession("active");
		Assert.False(state.Sending); Assert.Equal("最终正文", state.Messages[1].Content);
		Assert.False(state.Messages[1].Streaming); Assert.Equal("你好", state.Messages[0].Content);
	}

	[Fact]
	public void SpeechIdleToolAndErrorStatesNeverUnlockAnActiveTurn()
	{
		NativeChatState state = new(); state.BeginSend("第一轮"); state.AttachSession("first");
		state.ApplyEvent(Event(new { type = "state", state = "idle" }));
		state.ApplyEvent(Event(new { type = "state", sessionId = "first", state = "error" }));
		state.ApplyEvent(Event(new { type = "cancelled", sessionId = "other" }));
		state.RequestCancel();
		Assert.True(state.Sending); Assert.True(state.CancelRequested); Assert.False(state.BeginSend("第二轮"));
		state.ApplyEvent(Event(new { type = "cancelled", sessionId = "first" }));
		Assert.False(state.Sending); Assert.Equal("第一轮", state.FailedInput); Assert.Equal("第一轮", state.Draft);
	}

	[Fact]
	public void EmptyCompletionRemovesPreviewRatherThanKeepingRetractedText()
	{
		NativeChatState state = new(); state.BeginSend("测试"); state.AttachSession("active");
		state.ApplyEvent(Event(new { type = "chunk", sessionId = "active", chunk = "服务端后来删去的正文" }));
		state.ApplyEvent(Event(new { type = "complete", sessionId = "active", message = new { text = "" } }));
		Assert.Single(state.Messages); Assert.Equal("user", state.Messages[0].Role); Assert.False(state.Sending);
	}

	[Fact]
	public void FailedStartRestoresWithoutPhantomMessageAndNeverClobbersANewDraft()
	{
		NativeChatState state = new() { Draft = "第一次输入" }; state.BeginSend(state.Draft);
		state.Draft = "正在编辑的新草稿"; state.StartFailed(new InvalidOperationException("宿主拒绝启动"));
		Assert.Empty(state.Messages); Assert.Equal("正在编辑的新草稿", state.Draft); Assert.Equal("第一次输入", state.FailedInput);
		Assert.True(state.BeginSend(state.FailedInput)); state.StartFailed(new InvalidOperationException("仍不可用"));
		Assert.Empty(state.Messages); Assert.Equal("正在编辑的新草稿", state.Draft);
	}

	[Fact]
	public void SessionErrorRetainsAcceptedUserAndPartialReplyWhileRestoringEditableInput()
	{
		NativeChatState state = new() { Draft = "已接受的输入" }; state.BeginSend(state.Draft); state.AttachSession("active");
		state.ApplyEvent(Event(new { type = "chunk", sessionId = "active", chunk = "部分正文" }));
		state.ApplyEvent(Event(new { type = "error", sessionId = "active", error = "提供方连接中断" }));
		Assert.Equal(2, state.Messages.Count); Assert.Equal("部分正文", state.Messages[1].Content);
		Assert.Equal("已接受的输入", state.Draft); Assert.Equal("提供方连接中断", state.Error);
	}

	[Fact]
	public void HistoryUsesStableIdsSortsDeduplicatesAndRejectsStaleGeneration()
	{
		NativeChatState state = new();
		var first = state.BeginHistory();
		Assert.True(state.AcceptHistory(first, Event(new[] { new { id = 12L, role = "assistant", content = "后" }, new { id = 11L, role = "user", content = "前" } }), 2));
		state.EndHistory(first); Assert.True(state.HasMoreHistory); Assert.Equal(11, state.OldestId);
		var older = state.BeginHistory();
		Assert.True(state.AcceptHistory(older, Event(new[] { new { id = 11L, role = "user", content = "重复" }, new { id = 9L, role = "assistant", content = "更早" } }), 2));
		state.EndHistory(older); Assert.Equal(new[] { "9", "11", "12" }, state.Messages.Select(message => message.Key));
		var stale = state.BeginHistory(); state.Clear("");
		Assert.False(state.AcceptHistory(stale, Event(new[] { new { id = 8, role = "user", content = "不应复活" } }), 2));
		state.EndHistory(stale); Assert.Empty(state.Messages); Assert.False(state.LoadingHistory);
	}

	[Fact]
	public void OlderHistoryCannotOverwriteATurnStartedAfterItsRequest()
	{
		NativeChatState state = new(); var ticket = state.BeginHistory(); state.BeginSend("新一轮");
		Assert.False(state.AcceptHistory(ticket, Event(new[] { new { id = 1, role = "user", content = "旧请求" } }), 50));
		Assert.Equal(2, state.Messages.Count); Assert.Equal("新一轮", state.Messages[0].Content); Assert.False(state.LoadingHistory);
	}

	[Fact]
	public void ApprovalQueueUsesServerDeadlinesAndConsumesResultsIncludingReplayedRequests()
	{
		NativeChatState state = new(); state.BeginSend("使用工具"); state.AttachSession("active");
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
		JsonElement first = Event(new { type = "approval-request", sessionId = "active", requestId = "one", toolName = "write_file", deadlineUtc = deadline, arguments = new { path = "document.md" } });
		state.ApplyEvent(first); state.ApplyEvent(first);
		state.ApplyEvent(Event(new { type = "approval-request", sessionId = "active", requestId = "two", toolName = "run", deadlineUtc = deadline }));
		Assert.Equal(2, state.Approvals.Count); Assert.InRange(state.Approvals[0].RemainingSeconds(DateTimeOffset.UtcNow), 29, 30);
		state.ApplyEvent(Event(new { type = "approval-extended", sessionId = "active", requestId = "one", deadlineUtc = deadline.AddSeconds(30) }));
		Assert.Equal(deadline.AddSeconds(30), state.Approvals[0].Deadline);
		state.ApplyEvent(Event(new { type = "approval-result", sessionId = "active", requestId = "one", approved = false, reason = "timeout" }));
		state.ApplyEvent(first); Assert.Single(state.Approvals); Assert.Equal("two", state.Approvals[0].RequestId); Assert.Equal("approval-timeout", state.Status);
		Assert.True(state.Sending); state.ApplyEvent(Event(new { type = "cancelled", sessionId = "active" })); Assert.Empty(state.Approvals);
	}

	[Fact]
	public void MissingApprovalDeadlineFailsClosed()
	{
		NativeChatApproval approval = new(Event(new { requestId = "broken", toolName = "run" }));
		Assert.Equal(0, approval.RemainingSeconds(DateTimeOffset.UtcNow));
	}

	[Theory]
	[InlineData(Key.Enter, KeyModifiers.None, false, true)]
	[InlineData(Key.Enter, KeyModifiers.Shift, false, false)]
	[InlineData(Key.Enter, KeyModifiers.None, true, false)]
	[InlineData(Key.ImeProcessed, KeyModifiers.None, false, false)]
	public void ComposerKeepsImeSelectionAndShiftEnterOutOfTheSendPath(Key key, KeyModifiers modifiers, bool composing, bool expected) =>
		Assert.Equal(expected, ChatComposer.ShouldSend(key, modifiers, composing));
}
