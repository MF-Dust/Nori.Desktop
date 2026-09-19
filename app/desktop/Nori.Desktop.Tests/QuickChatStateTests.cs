using System.Text.Json;
using Avalonia.Input;
using Nori.Desktop.Chat;
using Nori.Desktop.QuickChat;

namespace Nori.Desktop.Tests;

public sealed class QuickChatStateTests
{
	private static JsonElement Event(object? value) => JsonSerializer.SerializeToElement(value);

	[Fact]
	public void InitialHistoryStaysSilentAndOnlyTheNewestThreeReceiptsAreVisible()
	{
		QuickChatState state = new();
		var ticket = state.Chat.BeginHistory();
		Assert.True(state.AcceptInitialHistory(ticket, Event(new[]
		{
			new { id = 1, role = "user", content = "旧问题" },
			new { id = 2, role = "assistant", content = "旧回答" },
		}), 50));
		state.Chat.EndHistory(ticket);
		Assert.Empty(state.Bubbles);

		DateTimeOffset now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);
		for (int index = 0; index < 4; index++)
		{
			Assert.True(state.BeginSend($"问题 {index}", now.AddSeconds(index)));
			state.Chat.AttachSession($"session-{index}");
			state.ApplyEvent(Event(new { type = "complete", sessionId = $"session-{index}", message = new { text = $"回答 {index}" } }), now.AddSeconds(index));
		}
		Assert.Equal(3, state.Bubbles.Count);
		Assert.Equal(new[] { "回答 2", "问题 3", "回答 3" }, state.Bubbles.Select(item => item.Message.Content));
	}

	[Fact]
	public void StreamingUpdatesDoNotExtendTheFirstReceiptLifetime()
	{
		QuickChatState state = new();
		state.MarkHistoryInitialized();
		DateTimeOffset first = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);
		Assert.True(state.BeginSend("开始", first));
		state.Chat.AttachSession("active");
		state.ApplyEvent(Event(new { type = "chunk", sessionId = "active", chunk = "第一个片段" }), first);
		QuickChatBubble response = Assert.Single(state.Bubbles, item => item.Message.Role == "assistant");
		Assert.Equal(first, response.ReceivedAt);

		state.ApplyEvent(Event(new { type = "chunk", sessionId = "active", chunk = "，很晚的片段" }), first.AddSeconds(29));
		Assert.Equal(first, response.ReceivedAt);
		state.Tick(first.AddSeconds(30));
		Assert.Equal(QuickChatBubblePhase.Exiting, response.Phase);
		state.Tick(first.AddSeconds(30).AddMilliseconds(349));
		Assert.Contains(response, state.Bubbles);
		state.Tick(first.AddSeconds(30).AddMilliseconds(350));
		Assert.DoesNotContain(response, state.Bubbles);

		state.ApplyEvent(Event(new { type = "chunk", sessionId = "active", chunk = "，过期后仍在流式更新" }), first.AddSeconds(31));
		Assert.DoesNotContain(state.Bubbles, item => item.Message.Role == "assistant");
	}

	[Fact]
	public void DraftLimitCountsUnicodeCodePointsInsteadOfUtf16Units()
	{
		string exact = string.Concat(Enumerable.Repeat("🌸", 99)) + "好";
		string overflow = exact + "界";
		Assert.Equal(100, QuickChatState.CountCodePoints(exact));
		Assert.Equal(exact, QuickChatState.LimitCodePoints(overflow, 100));

		QuickChatState state = new();
		state.Draft = overflow;
		Assert.Equal(exact, state.Draft);
	}

	[Fact]
	public void FailedSendRestoresOriginalInputWithoutOverwritingANewerDraft()
	{
		QuickChatState state = new() { Draft = "第一条" };
		state.MarkHistoryInitialized();
		Assert.True(state.BeginSend(state.Draft, DateTimeOffset.UtcNow));
		state.Draft = "正在编辑的下一条";
		state.StartFailed(new InvalidOperationException("提供方不可用"), DateTimeOffset.UtcNow);
		Assert.Equal("正在编辑的下一条", state.Draft);
		Assert.Equal("第一条", state.Chat.FailedInput);
		Assert.Equal("提供方不可用", state.Chat.Error);
		Assert.Empty(state.Chat.Messages);
	}

	[Fact]
	public void DraftStaysVisibleUntilHostAcceptsAndAcceptanceDoesNotClearNewTyping()
	{
		QuickChatState state = new() { Draft = "等待接收" };
		state.MarkHistoryInitialized();
		Assert.True(state.BeginSend(state.Draft, DateTimeOffset.UtcNow));
		Assert.Equal("等待接收", state.Draft);
		state.AcceptSession("first");
		Assert.Equal("", state.Draft);
		state.ApplyEvent(Event(new { type = "complete", sessionId = "first", message = new { text = "完成" } }), DateTimeOffset.UtcNow);

		state.Draft = "第二条";
		Assert.True(state.BeginSend(state.Draft, DateTimeOffset.UtcNow));
		state.Draft = "已经开始写第三条";
		state.AcceptSession("second");
		Assert.Equal("已经开始写第三条", state.Draft);
	}

	[Fact]
	public void EarlyCompleteIsProjectedAfterSessionAcceptance()
	{
		QuickChatState state = new() { Draft = "等待接收" };
		state.MarkHistoryInitialized();
		DateTimeOffset sentAt = DateTimeOffset.UtcNow;
		DateTimeOffset acceptedAt = sentAt.AddMilliseconds(80);
		state.BeginSend(state.Draft, sentAt);
		state.ApplyEvent(Event(new { type = "chunk", sessionId = "early", chunk = "流式预览" }), sentAt.AddMilliseconds(20));
		state.ApplyEvent(Event(new { type = "complete", sessionId = "early", message = new { text = "最终回答" } }), sentAt.AddMilliseconds(40));
		Assert.Single(state.Bubbles);

		state.AcceptSession("early", acceptedAt);
		Assert.False(state.Chat.Sending);
		Assert.Equal("", state.Draft);
		QuickChatBubble answer = Assert.Single(state.Bubbles, item => item.Message.Role == "assistant");
		Assert.Equal("最终回答", answer.Message.Content);
		Assert.Equal(acceptedAt, answer.ReceivedAt);
	}

	[Fact]
	public void EarlyErrorRestoresDraftAndAcceptanceDoesNotClearIt()
	{
		QuickChatState state = new() { Draft = "不能丢失的输入" };
		state.MarkHistoryInitialized();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		state.BeginSend(state.Draft, now);
		state.ApplyEvent(Event(new { type = "error", sessionId = "early-error", error = "提供方提前失败" }), now.AddMilliseconds(20));

		state.AcceptSession("early-error", now.AddMilliseconds(40));
		Assert.False(state.Chat.Sending);
		Assert.Equal("不能丢失的输入", state.Draft);
		Assert.Equal("不能丢失的输入", state.Chat.FailedInput);
		Assert.Equal("提供方提前失败", state.Chat.Error);
		Assert.DoesNotContain(state.Bubbles, item => item.Message.Role == "assistant");
	}

	[Fact]
	public void ExpiredMessagesArePrunedButActiveStreamingResponseIsRetained()
	{
		QuickChatState state = new();
		state.MarkHistoryInitialized();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		state.BeginSend("长回复", now);
		state.AcceptSession("active");
		state.ApplyEvent(Event(new { type = "chunk", sessionId = "active", chunk = "仍在生成" }), now);
		state.Tick(now.AddSeconds(31));
		state.Tick(now.AddSeconds(32));
		Assert.Equal(2, state.Chat.Messages.Count);
		Assert.True(state.Chat.Sending);

		state.ApplyEvent(Event(new { type = "complete", sessionId = "active", message = new { text = "最终回答" } }), now.AddSeconds(33));
		state.Tick(now.AddSeconds(34));
		Assert.Empty(state.Chat.Messages);
	}

	[Fact]
	public void ApprovalResultConsumesCompactCardAndTimeoutIsReported()
	{
		QuickChatState state = new();
		state.MarkHistoryInitialized();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		state.BeginSend("保存文件", now);
		state.Chat.AttachSession("active");
		state.ApplyEvent(Event(new
		{
			type = "approval-request",
			sessionId = "active",
			requestId = "approval-one",
			toolName = "write_file",
			description = "保存笔记",
			deadlineUtc = now.AddSeconds(30),
		}), now);
		NativeChatApproval approval = Assert.Single(state.Chat.Approvals);
		Assert.Equal("write_file", approval.ToolName);
		state.ApplyEvent(Event(new { type = "approval-result", sessionId = "active", requestId = "approval-one", approved = false, reason = "timeout" }), now.AddSeconds(30));
		Assert.Empty(state.Chat.Approvals);
		Assert.Equal("approval-timeout", state.Chat.Status);
	}

	[Theory]
	[InlineData(Key.Enter, KeyModifiers.None, false, true)]
	[InlineData(Key.Enter, KeyModifiers.Shift, false, false)]
	[InlineData(Key.Enter, KeyModifiers.None, true, false)]
	[InlineData(Key.ImeProcessed, KeyModifiers.None, false, false)]
	public void ComposerProtectsImeAndOnlyPlainEnterSends(Key key, KeyModifiers modifiers, bool composing, bool expected) =>
		Assert.Equal(expected, QuickChatComposer.ShouldSend(key, modifiers, composing));
}
