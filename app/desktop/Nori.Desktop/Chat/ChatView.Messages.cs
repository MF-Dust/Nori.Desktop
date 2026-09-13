using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nori.Desktop.Chat;

public sealed partial class ChatView
{
	private readonly Dictionary<NativeChatMessage, MessageVisual> _messageViews = [];
	private readonly HashSet<MessageVisual> _dirtyMessages = [];
	private bool _followLatest = true;
	private bool _scrollPending;
	private bool _preserveScroll;
	private bool _loadingOlder;
	private int _unread;

	private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs args)
	{
		if (args.Action == NotifyCollectionChangedAction.Reset)
		{
			foreach (MessageVisual visual in _messageViews.Values) visual.Dispose();
			_messageViews.Clear(); _dirtyMessages.Clear(); _messageList.Children.Clear(); _unread = 0;
		}
		if (args.OldItems is not null)
			foreach (NativeChatMessage message in args.OldItems)
				if (_messageViews.Remove(message, out MessageVisual? visual))
				{
					visual.Dispose(); _dirtyMessages.Remove(visual); _messageList.Children.Remove(visual.Root);
				}
		if (args.NewItems is not null)
		{
			int index = args.NewStartingIndex;
			foreach (NativeChatMessage message in args.NewItems)
			{
				var visual = new MessageVisual(this, message); _messageViews[message] = visual;
				_messageList.Children.Insert(index++, visual.Root); _dirtyMessages.Add(visual);
				if (!_loadingOlder && !_state.LoadingHistory && !_followLatest) _unread++;
			}
		}
		QueueRender();
	}
	private void FlushMessages()
	{
		if (_dirtyMessages.Count == 0) return;
		foreach (MessageVisual visual in _dirtyMessages.ToArray()) visual.Render();
		_dirtyMessages.Clear(); ResizeBubbles();
		if (_followLatest && !_loadingOlder && !_preserveScroll) ScheduleScroll();
		RenderLatest();
	}
	private void ResizeBubbles()
	{
		double width = Math.Max(80, (_scroll.Viewport.Width > 0 ? _scroll.Viewport.Width : Bounds.Width) - 40) * 0.84;
		foreach (MessageVisual visual in _messageViews.Values)
			foreach (Control row in visual.Root.Children) row.MaxWidth = width;
	}
	private void OnScrollChanged()
	{
		if (_preserveScroll || _scrollPending) return;
		_followLatest = _scroll.Extent.Height - _scroll.Viewport.Height - _scroll.Offset.Y <= 120;
		if (_followLatest) _unread = 0;
		RenderLatest();
	}
	private void RenderLatest()
	{
		_latest.IsVisible = !_followLatest;
		string label = _unread > 0 ? T($"有新消息 ({_unread})", $"New messages ({_unread})") : T("回到最新消息", "Back to latest");
		if (_latest.Content is Grid row && row.Children.OfType<TextBlock>().FirstOrDefault() is { } text) text.Text = label;
		AutomationProperties.SetName(_latest, label); ToolTip.SetTip(_latest, label);
	}
	internal void ScrollToLatest()
	{
		_followLatest = true; _unread = 0; RenderLatest(); ScheduleScroll();
	}
	private void ScheduleScroll()
	{
		if (_scrollPending || !_hostVisible) return;
		_scrollPending = true;
		Dispatcher.UIThread.Post(() =>
		{
			if (!_disposed && _followLatest && !_preserveScroll)
			{
				_scroll.UpdateLayout(); _scroll.ScrollToEnd();
			}
			_scrollPending = false;
		}, DispatcherPriority.Loaded);
	}

	private sealed class MessageVisual : IDisposable
	{
		private readonly ChatView _view;
		private readonly NativeChatMessage _message;
		private readonly List<(Border Bubble, Button Copy, string Text)> _segments = [];
		private string? _renderedText;
		private bool _streaming;
		internal StackPanel Root { get; } = new() { Spacing = 10 };
		internal MessageVisual(ChatView view, NativeChatMessage message)
		{
			_view = view; _message = message; Root.Name = "ChatMessage_" + message.Key;
			_message.Changed += MarkDirty;
		}
		private void MarkDirty() { _view._dirtyMessages.Add(this); _view.QueueRender(); }
		internal void Render()
		{
			string text = _message.Content;
			if (text == _renderedText && _streaming == _message.Streaming) return;
			_renderedText = text; _streaming = _message.Streaming;
			IReadOnlyList<string> slices = _message.Role == "assistant" ? ChatMarkdown.Split(text, _streaming) : [text];
			if (_streaming && slices.Count == 0) slices = [""];
			// 流式更新只替换活动气泡的正文；已完成的其他消息和复制按钮不重新挂载。
			if (slices.Count == 1 && _segments.Count == 1)
			{
				var segment = _segments[0]; segment.Bubble.Child = Body(slices[0]); _segments[0] = (segment.Bubble, segment.Copy, slices[0]); return;
			}
			Root.Children.Clear(); _segments.Clear();
			foreach (string slice in slices)
			{
				bool user = _message.Role == "user";
				var bubble = new Border
				{
					Name = user ? "ChatUserBubble" : "ChatAssistantBubble", Padding = new Thickness(18, 12),
					CornerRadius = user ? new CornerRadius(14, 14, 4, 14) : new CornerRadius(14, 14, 14, 4),
					Background = user ? ChatPalette.UserBackground : ChatPalette.AssistantBackground,
					BorderBrush = user ? ChatPalette.Line : ChatPalette.AssistantBorder, BorderThickness = new Thickness(1), Child = Body(slice),
				};
				var copy = new Button { Name = "ChatCopy", Content = Icon("copy", ChatPalette.Muted, 12), Width = 26, Height = 26, Padding = new Thickness(5), Opacity = 0, VerticalAlignment = VerticalAlignment.Center };
				copy.Classes.Add("chat-button");
				var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,26"), ColumnSpacing = 6, HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left };
				row.Children.Add(bubble); Grid.SetColumn(copy, 1); row.Children.Add(copy);
				row.PointerEntered += (_, _) => copy.Opacity = 1;
				row.PointerExited += (_, _) => { if (!copy.IsKeyboardFocusWithin) copy.Opacity = 0; };
				copy.GotFocus += (_, _) => copy.Opacity = 1;
				copy.LostFocus += (_, _) => { if (!row.IsPointerOver) copy.Opacity = 0; };
				copy.Click += (_, _) => _view.Run(CopyAsync(copy));
				_segments.Add((bubble, copy, slice)); Root.Children.Add(row);
			}
			Localize();
		}
		private Control Body(string text)
		{
			if (text.Length == 0 && _message.Streaming) return new TextBlock { Text = "…", Foreground = ChatPalette.AssistantText, FontSize = 13 };
			if (_message.Role == "assistant") return ChatMarkdown.Render(text, url => _view.Run(_view.ExecuteAsync("open_url", new { url }, _view._lifetime.Token)));
			return new SelectableTextBlock { Text = text, Foreground = ChatPalette.UserText, FontSize = 13, LineHeight = 20.8, TextWrapping = TextWrapping.Wrap };
		}
		private async Task CopyAsync(Button copy)
		{
			string text = _segments.FirstOrDefault(segment => ReferenceEquals(segment.Copy, copy)).Text ?? "";
			await _view.ExecuteAsync("clipboard_write_text", new { text }, _view._lifetime.Token);
			copy.Content = Icon("check", ChatPalette.Teal, 12); copy.Opacity = 1;
			AutomationProperties.SetName(copy, _view.T("已复制", "Copied")); ToolTip.SetTip(copy, _view.T("已复制", "Copied"));
			await Task.Delay(1500, _view._lifetime.Token);
			copy.Content = Icon("copy", ChatPalette.Muted, 12); Localize();
		}
		internal void Localize()
		{
			foreach (var segment in _segments)
			{
				AutomationProperties.SetName(segment.Copy, _view.T("复制消息", "Copy message")); ToolTip.SetTip(segment.Copy, _view.T("复制消息", "Copy message"));
			}
		}
		public void Dispose() => _message.Changed -= MarkDirty;
	}
}
