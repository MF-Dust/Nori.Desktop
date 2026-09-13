using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Desktop.Chat;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativeChatWindowStaysDarkAndHideRetainsDraftTurnAndControls() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(); fixture.ConfigureDesktop();
		fixture._services.Chat.SaveMessage("user", "旧消息"); fixture._services.Chat.SaveMessage("assistant", "旧回复");
		ChatWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshChatForTest(window);
			Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant); Assert.Equal(720, window.MinWidth); Assert.Equal(480, window.MinHeight);
			Assert.True(window.Body.HistoryLoaded); Assert.Equal(2, window.Body.State.Messages.Count);
			ChatComposer composer = window.Body.Composer; composer.Text = "尚未发送的草稿";
			Border original = ChatControl<Border>(window, "ChatUserBubble");
			window.Body.State.BeginSend("活动会话"); window.Body.State.AttachSession("synthetic"); window.Body.FlushRender();
			window.Close(); await WaitUntilAsync(() => !window.IsVisible);
			Assert.True(window.Body.State.Sending); Assert.Equal("尚未发送的草稿", composer.Text);
			window.Show(); await RefreshChatForTest(window);
			Assert.Same(composer, window.Body.Composer); Assert.Contains(original, window.GetVisualDescendants());
			window.Body.State.ApplyEvent(ChatEvent(new { type = "chunk", sessionId = "synthetic", chunk = "正在回复" })); window.Body.FlushRender();
			Assert.Contains(original, window.GetVisualDescendants());
			window.Body.State.ApplyEvent(ChatEvent(new { type = "complete", sessionId = "synthetic", message = new { text = "完成回复" } })); window.Body.FlushRender();
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeChatInitialHistoryBlocksSendUntilLoadedAndNeverDuplicatesSuccessfulProjection() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(); fixture.ConfigureDesktop();
		Window owner = new(); using NativeChatService service = new(fixture._services, owner);
		TaskCompletionSource<JsonElement> history = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int starts = 0, pages = 0;
		using ChatView view = new(service, (command, _, _) => command switch
		{
			"chat_history_page" => CountPage(),
			"chat_start" => CountStart(),
			_ => Task.FromResult(ChatEvent(true)),
		});
		owner.Content = view;
		try
		{
			owner.Show(); Task refresh = view.RefreshAsync(); await WaitUntilAsync(() => pages == 1);
			view.Composer.Text = "新消息"; await view.SendAsync(); Assert.Equal(0, starts); Assert.False(ChatControl<Button>(owner, "ChatSend").IsEnabled);
			history.SetResult(ChatEvent(new[] { new { id = 1, role = "user", content = "之前的记录" } })); await refresh;
			Assert.True(view.HistoryLoaded); await view.SendAsync(); Assert.Equal(1, starts);
			view.State.ApplyEvent(ChatEvent(new { type = "complete", sessionId = "accepted", message = new { text = "最终答复" } })); view.FlushRender();
			await view.RefreshAsync(); Assert.Equal(1, pages); Assert.Equal(3, view.State.Messages.Count);
		}
		finally { history.TrySetResult(ChatEvent(Array.Empty<object>())); owner.Close(); }
		Task<JsonElement> CountPage() { pages++; return history.Task; }
		Task<JsonElement> CountStart() { starts++; return Task.FromResult(ChatEvent("accepted")); }
	});

	[Fact]
	public Task NativeChatDraftSynchronizesBeforeDeferredEventsAndSurvivesSnapshotStreamAndSend() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(); fixture.ConfigureDesktop();
		Window owner = new(); using NativeChatService service = new(fixture._services, owner);
		JsonElement sent = default;
		using ChatView view = new(service, (command, args, _) =>
		{
			if (command == "chat_start") { sent = ChatEvent(args); return Task.FromResult(ChatEvent("active")); }
			return Task.FromResult(ChatEvent(Array.Empty<object>()));
		}); owner.Content = view;
		try
		{
			owner.Show(); await view.RefreshAsync(); Assert.True(view.HistoryLoaded);
			view.Composer.Text = "刚输入的草稿";
			Assert.Equal("刚输入的草稿", view.State.Draft);
			Assert.True(ChatControl<Button>(owner, "ChatSend").IsEnabled);
			view.ApplySnapshot(ChatEvent(fixture._runtime.BuildSnapshot()));
			Assert.Equal("刚输入的草稿", view.Composer.Text);
			await view.SendAsync();
			Assert.Equal("刚输入的草稿", sent.GetProperty("text").GetString());
			Assert.Equal("", view.Composer.Text); Assert.Equal("", view.State.Draft);
			view.Composer.Text = "回复期间正在编辑的下一条";
			view.State.ApplyEvent(ChatEvent(new { type = "chunk", sessionId = "active", chunk = "部分答复" })); view.FlushRender();
			Assert.Equal("回复期间正在编辑的下一条", view.State.Draft);
			view.State.ApplyEvent(ChatEvent(new { type = "error", sessionId = "active", error = "合成请求失败" })); view.FlushRender();
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			Assert.Equal("回复期间正在编辑的下一条", view.Composer.Text); Assert.Equal(view.Composer.Text, view.State.Draft);
			Assert.Equal("刚输入的草稿", view.State.FailedInput);
		}
		finally { owner.Close(); }
	});

	[Fact]
	public Task NativeChatVoiceRetriesSynchronousFailuresAndHidingDuringStartKeepsTranscript() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(); Window owner = new(); using NativeChatService service = new(fixture._services, owner);
		bool failStart = true, failStop = true; int starts = 0, stops = 0;
		TaskCompletionSource<JsonElement>? pendingStart = null;
		using ChatView view = new(service, (command, _, _) =>
		{
			if (command == "stt_start")
			{
				starts++; if (failStart) throw new InvalidOperationException("合成录音启动失败");
				return pendingStart?.Task ?? Task.FromResult(ChatEvent((object?)null));
			}
			if (command == "stt_stop")
			{
				stops++; if (failStop) throw new InvalidOperationException("合成转写失败");
				return Task.FromResult(ChatEvent(new { text = "保留的转写" }));
			}
			return Task.FromResult(ChatEvent(Array.Empty<object>()));
		}); owner.Content = view;
		try
		{
			owner.Show();
			await Assert.ThrowsAsync<InvalidOperationException>(view.ToggleVoiceAsync); Assert.Equal("idle", view.VoiceState);
			failStart = false; await view.ToggleVoiceAsync(); Assert.Equal("recording", view.VoiceState);
			await Assert.ThrowsAsync<InvalidOperationException>(view.StopRecordingAsync); Assert.Equal("recording", view.VoiceState);
			failStop = false; await view.StopRecordingAsync(); Assert.Equal("idle", view.VoiceState);
			pendingStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
			view.Composer.Text = "已有草稿";
			Task start = view.ToggleVoiceAsync(), hide = view.PrepareHideAsync(); Assert.False(hide.IsCompleted);
			pendingStart.SetResult(ChatEvent((object?)null)); await start; await hide;
			Assert.Equal("idle", view.VoiceState); Assert.Equal("已有草稿 保留的转写", view.Composer.Text); Assert.Equal(3, starts); Assert.Equal(3, stops);
			await view.ToggleVoiceAsync(); await view.StopRecordingAsync(); Assert.Equal(4, starts); Assert.Equal(4, stops);
		}
		finally { pendingStart?.TrySetResult(ChatEvent((object?)null)); owner.Close(); }
	});

	[Fact]
	public Task NativeChatApprovalDialogConsumesTimeoutWhileHiddenAndCloseSendsDeny() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(); Window owner = new(); using NativeChatService service = new(fixture._services, owner);
		List<(string Command, JsonElement Args)> commands = [];
		using ChatView view = new(service, (command, args, _) => { commands.Add((command, ChatEvent(args))); return Task.FromResult(ChatEvent(true)); }); owner.Content = view;
		try
		{
			owner.Show(); view.State.BeginSend("合成请求"); view.State.AttachSession("active");
			AddApproval("one"); view.FlushRender(); owner.UpdateLayout();
			Assert.True(ChatControl<Border>(owner, "ChatModal").IsVisible);
			ChatControl<Button>(owner, "ChatDialogClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await WaitUntilAsync(() => view.State.Approvals.Count == 0);
			var deny = Assert.Single(commands); Assert.Equal("approval_respond", deny.Command); Assert.False(deny.Args.GetProperty("approved").GetBoolean());
			AddApproval("two"); view.FlushRender(); owner.Hide();
			view.State.ApplyEvent(ChatEvent(new { type = "approval-result", sessionId = "active", requestId = "two", approved = false, reason = "timeout" })); view.FlushRender();
			Assert.False(ChatControl<Border>(owner, "ChatModal").IsVisible); Assert.True(view.State.Sending);
		}
		finally { owner.Close(); }
		void AddApproval(string id) => view.State.ApplyEvent(ChatEvent(new { type = "approval-request", sessionId = "active", requestId = id, toolName = "write_file", deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30), arguments = new { path = "document.md" } }));
	});

	[Fact]
	public Task NativeChatClearRequiresConfirmationAndFailureKeepsHistoryAndDraft() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(); Window owner = new(); using NativeChatService service = new(fixture._services, owner);
		bool fail = true; int clears = 0;
		using ChatView view = new(service, (command, _, _) =>
		{
			if (command == "chat_clear") { clears++; if (fail) throw new InvalidOperationException("远端重置失败"); }
			return Task.FromResult(ChatEvent(new { remoteReset = true }));
		}); owner.Content = view;
		try
		{
			owner.Show(); var ticket = view.State.BeginHistory(); view.State.AcceptHistory(ticket, ChatEvent(new[] { new { id = 1, role = "user", content = "保留历史" } }), 50); view.State.EndHistory(ticket);
			view.Composer.Text = "保留草稿"; view.FlushRender(); owner.UpdateLayout();
			ChatControl<Button>(owner, "ChatClear").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(0, clears);
			ChatControl<Button>(owner, "ChatApprovalDeny").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Single(view.State.Messages);
			ChatControl<Button>(owner, "ChatClear").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			ChatControl<Button>(owner, "ChatApprovalAllow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await WaitUntilAsync(() => view.State.Error.Length > 0); Assert.Single(view.State.Messages); Assert.Equal("保留草稿", view.Composer.Text);
			Assert.True(ChatControl<Border>(owner, "ChatModal").IsVisible);
			fail = false; ChatControl<Button>(owner, "ChatApprovalAllow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await WaitUntilAsync(() => view.State.Messages.Count == 0); Assert.Equal(2, clears); Assert.Equal("保留草稿", view.Composer.Text);
		}
		finally { owner.Close(); }
	});

	[Fact]
	public Task NativeChatUsesRealRemoteReadinessAndLiveLanguageWithoutReplacingComposer() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(); fixture.ConfigureUnreachableLuoLiCore();
		ChatWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshChatForTest(window); window.Body.Composer.Text = "不会实际发送";
			Assert.Equal("不会实际发送", window.Body.State.Draft);
			Assert.True(ChatControl<Button>(window, "ChatSend").IsEnabled);
			ChatComposer composer = window.Body.Composer;
			fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("en-US")); fixture._runtime.InvalidateSnapshot("general");
			await RefreshChatForTest(window);
			// 状态通知可能合并到比显式刷新更新的一次后台请求，等待实际界面提交。
			await WaitUntilAsync(() => window.Title == "Nori · Chat");
			Assert.Equal("Nori · Chat", window.Title); Assert.Same(composer, window.Body.Composer);
			Assert.Equal("Send message", AutomationProperties.GetName(ChatControl<Button>(window, "ChatSend")));
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeChatComposerReadsTheActualImePresenterAndKeepsKeyboardNewlines() => WithSettingsUiAsync(async () =>
	{
		Window owner = new() { Content = new ChatComposer { AcceptsReturn = true, Width = 300 } };
		try
		{
			owner.Show(); owner.UpdateLayout(); ChatComposer composer = (ChatComposer)owner.Content!;
			TextPresenter presenter = composer.GetVisualDescendants().OfType<TextPresenter>().Single(); int sends = 0; composer.SendRequested += () => sends++;
			presenter.PreeditText = "拼音候选"; composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }); Assert.Equal(0, sends);
			presenter.PreeditText = null; composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Shift }); Assert.Equal(0, sends);
			composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }); Assert.Equal(1, sends);
			await Task.CompletedTask;
		}
		finally { owner.Close(); }
	});

	[Fact]
	public Task NativeChatButtonsAndPaleBubblesRemainDarkThemeSafeInEveryInteractionState() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(); fixture.ConfigureDesktop();
		ThemeVariant? previous = Application.Current!.RequestedThemeVariant;
		ChatWindow window = new(fixture._services);
		try
		{
			Application.Current.RequestedThemeVariant = ThemeVariant.Light;
			window.Show(); await RefreshChatForTest(window); window.Body.Composer.Text = "检查按钮";
			Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
			Button button = ChatControl<Button>(window, "ChatSend"); Assert.True(button.IsEnabled);
			foreach (string state in new[] { "", ":pointerover", ":pressed" })
			{
				((IPseudoClasses)button.Classes).Set(":pointerover", state == ":pointerover");
				((IPseudoClasses)button.Classes).Set(":pressed", state == ":pressed"); window.UpdateLayout();
				Assert.Equal(((ISolidColorBrush)ChatPalette.Accent).Color, ((ISolidColorBrush)button.Background!).Color);
				Assert.Equal(((ISolidColorBrush)ChatPalette.OnTeal).Color, ((ISolidColorBrush)button.Foreground!).Color);
			}
			((IPseudoClasses)button.Classes).Set(":pointerover", false); ((IPseudoClasses)button.Classes).Set(":pressed", false);
			window.Body.Composer.Text = ""; window.UpdateLayout();
			Assert.False(button.IsEnabled); Assert.Equal(((ISolidColorBrush)ChatPalette.Deep).Color, ((ISolidColorBrush)button.Background!).Color);
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); Application.Current.RequestedThemeVariant = previous; }
	});

	private static JsonElement ChatEvent(object? value) => JsonSerializer.SerializeToElement(value);
	private static T ChatControl<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
	private static async Task RefreshChatForTest(ChatWindow window)
	{
		await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); await window.Body.RefreshAsync();
		await WaitUntilAsync(() => window.Body.HistoryLoaded); window.Body.FlushRender();
		await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
	}
}
