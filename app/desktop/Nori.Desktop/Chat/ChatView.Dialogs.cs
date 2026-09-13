using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nori.Desktop.Chat;

public sealed partial class ChatView
{
	private readonly Border _modal = new() { Name = "ChatModal", Background = ChatPalette.Scrim, IsVisible = false };
	private readonly TextBlock _dialogTitle = Text("", 18, true);
	private readonly TextBlock _approvalName = Text("", 14, true);
	private readonly TextBlock _approvalDescription = Text("", 12);
	private readonly TextBlock _approvalQueue = Text("", 11.5);
	private readonly TextBlock _approvalCountdown = Text("", 12);
	private readonly TextBlock _dialogError = Text("", 12);
	private readonly SelectableTextBlock _approvalArgs = new() { FontSize = 12, FontFamily = new FontFamily("Consolas, Menlo, monospace"), TextWrapping = TextWrapping.Wrap, Foreground = ChatPalette.Body };
	private readonly ScrollViewer _argsScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 70 };
	private Border _dialogPanel = null!;
	private Control _approvalBody = null!;
	private TextBlock _clearDescription = null!;
	private Button _dialogClose = null!;
	private Button _allow = null!;
	private Button _deny = null!;
	private Button _extend = null!;
	private Button _expandArgs = null!;
	private bool _argsExpanded;
	private bool _approvalBusy;
	private bool _confirmOpen;
	private string? _shownApproval;

	private void BuildDialogs()
	{
		var dialog = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 12 };
		KeyboardNavigation.SetTabNavigation(dialog, KeyboardNavigationMode.Cycle);
		var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
		_dialogTitle.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(_dialogTitle);
		_dialogClose = Button(() => T("关闭并拒绝", "Close and deny"), () =>
		{
			if (_state.Approvals.Count > 0) return DecideApprovalAsync(false);
			_confirmOpen = false; RenderDialog(); return Task.CompletedTask;
		}, "ChatDialogClose", "close", iconOnly: true);
		Grid.SetColumn(_dialogClose, 1); heading.Children.Add(_dialogClose); dialog.Children.Add(heading);
		var contents = new Grid();
		var approval = new StackPanel { Spacing = 12 };
		_approvalName.Foreground = ChatPalette.Accent; approval.Children.Add(_approvalName);
		_approvalQueue.Foreground = ChatPalette.Muted; approval.Children.Add(_approvalQueue);
		_approvalDescription.Foreground = ChatPalette.Muted; approval.Children.Add(_approvalDescription);
		var argsHeading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
		TextBlock argsLabel = Local(() => T("调用参数", "Call arguments"), 11.5); argsLabel.Foreground = ChatPalette.Muted; argsLabel.VerticalAlignment = VerticalAlignment.Center; argsHeading.Children.Add(argsLabel);
		_expandArgs = Button(() => T("展开", "Expand"), () => { _argsExpanded = !_argsExpanded; RenderDialog(); return Task.CompletedTask; }, "ChatApprovalExpand");
		Grid.SetColumn(_expandArgs, 1); argsHeading.Children.Add(_expandArgs); approval.Children.Add(argsHeading);
		_argsScroll.Content = _approvalArgs;
		approval.Children.Add(new Border { Background = ChatPalette.Deep, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Child = _argsScroll });
		_approvalCountdown.Foreground = ChatPalette.Muted; approval.Children.Add(_approvalCountdown);
		_approvalBody = approval; contents.Children.Add(approval);
		_clearDescription = Local(() => T("确定清空当前所有对话历史吗？清空后无法撤销。", "Clear the whole conversation history? This cannot be undone.")); contents.Children.Add(_clearDescription);
		var scroller = new ScrollViewer { Content = contents, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
		Grid.SetRow(scroller, 1); dialog.Children.Add(scroller);
		var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
		_extend = Button(() => T("延长倒计时", "Extend countdown"), ExtendActiveApprovalAsync, "ChatApprovalExtend");
		_deny = Button(() => T("拒绝", "Deny"), () =>
		{
			if (_state.Approvals.Count > 0) return DecideApprovalAsync(false);
			_confirmOpen = false; RenderDialog(); return Task.CompletedTask;
		}, "ChatApprovalDeny");
		_allow = Button(() => T("允许执行", "Allow once"), () => _state.Approvals.Count > 0 ? DecideApprovalAsync(true) : ClearHistoryAsync(), "ChatApprovalAllow", primary: true);
		foreach (Button button in new[] { _extend, _deny, _allow }) { button.Margin = new Thickness(8, 4, 0, 0); footer.Children.Add(button); }
		_dialogError.Foreground = ChatPalette.Danger; _dialogError.Name = "ChatDialogError"; _dialogError.MaxHeight = 72;
		AutomationProperties.SetLiveSetting(_dialogError, AutomationLiveSetting.Assertive);
		Grid.SetRow(_dialogError, 2); dialog.Children.Add(_dialogError);
		Grid.SetRow(footer, 3); dialog.Children.Add(footer);
		_dialogPanel = new Border { Name = "ChatDialogPanel", MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20), Padding = new Thickness(24), Background = ChatPalette.Background, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Child = dialog };
		_modal.Child = _dialogPanel; _root.Children.Add(_modal);
		SizeChanged += (_, _) => _dialogPanel.MaxHeight = Math.Max(160, Bounds.Height - 40);
	}

	private void RenderDialog()
	{
		NativeChatApproval? approval = _state.Approvals.FirstOrDefault();
		bool wasVisible = _modal.IsVisible;
		_modal.IsVisible = approval is not null || _confirmOpen;
		_main.IsEnabled = !_modal.IsVisible;
		if (!_modal.IsVisible)
		{
			_shownApproval = null;
			if (wasVisible && _hostVisible) _composer.Focus();
			return;
		}
		bool changed = _shownApproval != approval?.RequestId;
		if (changed) { _shownApproval = approval?.RequestId; _argsExpanded = false; _argsScroll.Offset = default; }
		_dialogTitle.Text = approval is not null ? T("工具执行确认", "Confirm tool execution") : T("清空记录", "Clear history");
		AutomationProperties.SetName(_modal, _dialogTitle.Text);
		_dialogError.Text = _state.Error; _dialogError.IsVisible = _state.Error.Length > 0; ToolTip.SetTip(_dialogError, _state.Error);
		_approvalBody.IsVisible = approval is not null; _clearDescription.IsVisible = approval is null;
		_extend.IsVisible = approval is not null;
		SetButtonLabel(_allow, approval is null ? T("确认清空", "Clear") : T("允许执行", "Allow once"));
		SetButtonLabel(_deny, approval is null ? T("取消", "Cancel") : T("拒绝", "Deny"));
		AutomationProperties.SetName(_dialogClose, approval is null ? T("关闭", "Close") : T("关闭并拒绝", "Close and deny"));
		_allow.Classes.Set("stop", approval is null);
		_allow.Classes.Set("primary", approval is not null);
		if (approval is not null)
		{
			_approvalName.Text = approval.ToolName;
			_approvalQueue.Text = _state.Approvals.Count + T(" 个待决", " pending") + (approval.PermissionLevel == "dangerous" ? T(" · 高风险操作", " · High-risk operation") : "");
			_approvalQueue.IsVisible = _state.Approvals.Count > 1 || approval.PermissionLevel == "dangerous";
			_approvalDescription.Text = approval.Description; _approvalDescription.IsVisible = approval.Description.Length > 0;
			string arguments = approval.Arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : JsonSerializer.Serialize(approval.Arguments, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
			if (_approvalArgs.Text != arguments) _approvalArgs.Text = arguments;
			int lines = arguments.Count(character => character == '\n') + 1;
			_expandArgs.IsVisible = lines > 8;
			_argsScroll.MaxHeight = _argsExpanded ? 240 : 70;
			SetButtonLabel(_expandArgs, _argsExpanded ? T("收起", "Collapse") : T($"展开 ({lines} 行)", $"Expand ({lines} lines)"));
		}
		UpdateApprovalCountdown();
		_deny.IsEnabled = !_approvalBusy && !_clearing && !_preparing; _dialogClose.IsEnabled = _deny.IsEnabled;
		if (!wasVisible || changed) Dispatcher.UIThread.Post(() => { if (_modal.IsVisible) _deny.Focus(); }, DispatcherPriority.Input);
	}
	private void UpdateApprovalCountdown()
	{
		NativeChatApproval? approval = _state.Approvals.FirstOrDefault();
		int seconds = approval?.RemainingSeconds(DateTimeOffset.UtcNow) ?? 0;
		_approvalCountdown.Text = seconds > 0 ? T($"授权将在 {seconds} 秒后超时", $"Approval expires in {seconds} seconds") : T("授权已到期，等待后端确认拒绝。", "Approval expired; waiting for the host to confirm denial.");
		_allow.IsEnabled = !_approvalBusy && !_clearing && !_preparing && (approval is null || seconds > 0);
		_extend.IsEnabled = approval is not null && seconds > 0 && !_approvalBusy && !_preparing;
	}
	private async Task DecideApprovalAsync(bool approved)
	{
		NativeChatApproval? approval = _state.Approvals.FirstOrDefault();
		if (approval is null || _approvalBusy || _preparing || (approved && approval.RemainingSeconds(DateTimeOffset.UtcNow) == 0)) return;
		_approvalBusy = true; RenderDialog();
		try
		{
			await ExecuteAsync("approval_respond", new { requestId = approval.RequestId, approved }, _lifetime.Token);
			_state.ResolveApproval(approval.RequestId);
		}
		finally { _approvalBusy = false; RenderDialog(); }
	}
	private async Task ExtendActiveApprovalAsync()
	{
		NativeChatApproval? approval = _state.Approvals.FirstOrDefault();
		if (approval is null || _approvalBusy || _preparing || approval.RemainingSeconds(DateTimeOffset.UtcNow) == 0) return;
		_approvalBusy = true; RenderDialog();
		try
		{
			JsonElement result = await ExecuteAsync("approval_extend", new { requestId = approval.RequestId }, _lifetime.Token);
			DateTimeOffset deadline = NativeChatJson.Deadline(result);
			if (deadline == DateTimeOffset.MinValue) throw new InvalidOperationException(T("授权延期没有返回有效截止时间。", "The approval extension did not return a valid deadline."));
			_state.ExtendApproval(approval.RequestId, deadline);
		}
		finally { _approvalBusy = false; RenderDialog(); }
	}
	private async Task ClearHistoryAsync()
	{
		if (_clearing || _state.Sending) return;
		_clearing = true; _state.InvalidateHistory(); UpdateActions(); RenderDialog();
		try
		{
			JsonElement result = await ExecuteAsync("chat_clear", cancellationToken: _lifetime.Token);
			string note = NativeChatJson.S(result, "note");
			if (note.Length > 0) note = T(note, "Local history was cleared. Safe mode did not contact the external service; a new remote conversation will be used after restart.");
			_state.Clear(note); _loadedHistory = true; _confirmOpen = false; ScrollToLatest();
		}
		finally { _clearing = false; QueueRender(); FlushRender(); }
	}
}
