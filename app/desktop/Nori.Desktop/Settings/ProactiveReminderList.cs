using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.Desktop.Settings.Pages;

namespace Nori.Desktop.Settings;

/// <summary>原生提醒列表，显示重复状态、绝对和相对时间，并在取消前确认。</summary>
public sealed class ProactiveReminderList : ContentControl
{
	private ProactiveSettingsPage? _page;
	private readonly DispatcherTimer _timer = new() {Interval = TimeSpan.FromSeconds(30)};
	private readonly List<(TextBlock Label, DateTimeOffset Trigger)> _times = [];
	private bool _attached;
	private bool _listening;

	/// <summary>创建提醒列表。</summary>
	public ProactiveReminderList()
	{
		DataContextChanged += (_, _) =>
		{
			StopListening();
			_page = DataContext as ProactiveSettingsPage;
			if (_attached) StartListening();
			Build();
		};
		AttachedToVisualTree += (_, _) =>
		{
			_attached = true;
			StartListening();
			Build();
		};
		DetachedFromVisualTree += (_, _) =>
		{
			_attached = false;
			StopListening();
		};
		_timer.Tick += (_, _) => UpdateTimes();
	}

	private void StartListening()
	{
		if (_page is null || _listening) return;
		_page.Reminders.CollectionChanged += OnRemindersChanged;
		SettingsLocalization.Changed += OnLanguageChanged;
		_timer.Start();
		_listening = true;
	}

	private void StopListening()
	{
		if (_page is not null && _listening) _page.Reminders.CollectionChanged -= OnRemindersChanged;
		SettingsLocalization.Changed -= OnLanguageChanged;
		_timer.Stop();
		_listening = false;
	}

	private void OnRemindersChanged(object? sender, NotifyCollectionChangedEventArgs args) => Build();
	private void OnLanguageChanged() => Dispatcher.UIThread.Post(Build);

	private void Build()
	{
		_times.Clear();
		if (_page is null) { Content = null; return; }
		StackPanel body = new() {Spacing = 12};
		body.Children.Add(new TextBlock
		{
			Text = Text($"现有提醒 · {_page.Reminders.Count}", $"Reminders · {_page.Reminders.Count}"),
			FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brush("SettingsPrimaryBrush"),
		});
		if (_page.Reminders.Count == 0)
			body.Children.Add(new TextBlock {Text = Text("暂无提醒，可在上方添加。", "No reminders yet. Add one above."), TextWrapping = TextWrapping.Wrap, Foreground = Brush("SettingsSecondaryBrush")});
		foreach (ProactiveSettingsPage.ReminderItemViewModel item in _page.Reminders)
		{
			Grid row = new() {ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12};
			StackPanel details = new() {Spacing = 5};
			details.Children.Add(new TextBlock {Text = item.Content, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap, Foreground = Brush("SettingsPrimaryBrush")});
			string status = item.RepeatDaily ? Text("每日重复", "Daily") : Text("单次提醒", "One-time");
			if (item.Status == "claimed") status += Text(" · 正在处理", " · Processing");
			details.Children.Add(new TextBlock {Text = status, FontSize = 12, Foreground = Brush("SettingsSecondaryBrush")});
			TextBlock time = new() {FontSize = 12, Foreground = Brush("SettingsSecondaryBrush"), TextWrapping = TextWrapping.Wrap};
			details.Children.Add(time);
			_times.Add((time, item.TriggerAt));
			row.Children.Add(details);
			Button cancel = new() {Content = Text("取消提醒", "Cancel"), VerticalAlignment = VerticalAlignment.Top};
			cancel.Classes.Add("danger");
			cancel.Click += async (_, _) => await ConfirmCancelAsync(item, cancel);
			Grid.SetColumn(cancel, 1);
			row.Children.Add(cancel);
			body.Children.Add(new Border
			{
				BorderBrush = Brush("SettingsBorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
				Padding = new Thickness(0, 12, 0, 0), Child = row,
			});
		}
		UpdateTimes();
		Content = new Border
		{
			Background = Brush("SettingsCardBrush"), BorderBrush = Brush("SettingsBorderBrush"), BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 16), Child = body,
		};
	}

	private void UpdateTimes()
	{
		DateTimeOffset now = DateTimeOffset.Now;
		foreach ((TextBlock label, DateTimeOffset trigger) in _times)
			label.Text = trigger.ToLocalTime().ToString("g") + " · " + RelativeTime(trigger, now, SettingsLocalization.IsEnglish);
	}

	/// <summary>生成随时间更新的提醒说明。</summary>
	internal static string RelativeTime(DateTimeOffset trigger, DateTimeOffset now, bool english)
	{
		double minutes = (trigger - now).TotalMinutes;
		if (minutes <= 0) return english ? "Due now" : "即将提醒";
		if (minutes < 60)
		{
			int count = Math.Max(1, (int)Math.Round(minutes));
			return english ? $"In {count} minute{(count == 1 ? "" : "s")}" : $"{count} 分钟后";
		}
		if (minutes < 1440)
		{
			int count = Math.Max(1, (int)Math.Round(minutes / 60));
			return english ? $"In {count} hour{(count == 1 ? "" : "s")}" : $"{count} 小时后";
		}
		int days = Math.Max(1, (int)Math.Round(minutes / 1440));
		return english ? $"In {days} day{(days == 1 ? "" : "s")}" : $"{days} 天后";
	}

	private async Task ConfirmCancelAsync(ProactiveSettingsPage.ReminderItemViewModel item, Button button)
	{
		ProactiveSettingsPage? page = _page;
		if (page is null || TopLevel.GetTopLevel(this) is not Window owner) return;
		button.IsEnabled = false;
		try
		{
			bool confirmed = await NativeSettingsDialogs.ConfirmAsync(owner, Text("取消提醒", "Cancel reminder"),
				Text("确定取消这条提醒？", "Cancel this reminder?") + Environment.NewLine + item.Content, true).ConfigureAwait(true);
			if (confirmed) await page.CancelReminderAsync(item).ConfigureAwait(true);
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { page.ReportActionFailure(exception); }
		finally { button.IsEnabled = true; }
	}

	private IBrush Brush(string key) => SettingsBrushes.Resolve(this, key);
	private static string Text(string chinese, string english) => SettingsLocalization.IsEnglish ? english : chinese;
}
