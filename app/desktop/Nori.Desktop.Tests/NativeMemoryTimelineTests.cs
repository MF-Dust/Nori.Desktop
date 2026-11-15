using System.Collections;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Core.Memory;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData("zh-CN", 720, 480)]
	[InlineData("zh-CN", 1920, 1080)]
	[InlineData("en-US", 720, 480)]
	[InlineData("en-US", 1920, 1080)]
	public Task 记忆时间线在两种语言和分辨率下可浏览(string language, int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
		MemoryItem known = fixture._services.Memory.AddAggregate("preference", "timeline-known", 0.6, "manual", kind: MemoryKind.Preference, canonicalSummary: "known-summary");
		MemoryItem unknown = fixture._services.Memory.AddAggregate("general", "timeline-unknown", 0.4, "chat");
		StampMemory(fixture, known.Id, "2024-06-02T04:00:00Z", "2024-06-02T04:00:00Z");
		StampMemory(fixture, unknown.Id, "not-a-date", "2099-01-01T00:00:00Z");
		MemoryWindow window = new(fixture._services) { Width = width, Height = height };
		bool english = language == "en-US";
		try
		{
			window.Show();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			window.Navigate("timeline");
			await window.RefreshAsync();
			await WaitTimelineAsync(() => window.TimelineTotalForTests >= 2 && TimelineHasText(window, "known-summary"), window);
			ListBox list = NamedControl<ListBox>(window, "MemoryTimelineList");
			foreach (object? item in list.Items) list.ScrollIntoView(item!);
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Assert.Contains(english ? "Forgetting archives" : "遗忘会把记忆归档", TimelineNotice(window));
			Assert.True(TimelineHasText(window, english ? "Creation time unknown" : "创建时间未知"));
			Assert.True(TimelineHasText(window, "known-summary"));
			Assert.False(TimelineHasText(window, "2099"));
			Button open = RequiredTimelineButton(window, "MemoryTimelineOpen_" + known.Id.ToString(CultureInfo.InvariantCulture));
			open.BringIntoView();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Assert.True(open.Focusable);
			Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(open)));
			Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button.IsVisible && Equals(button.Content, english ? "Delete this memory" : "删除此记忆"));
			TextBox search = NamedControl<TextBox>(window, "MemoryTimelineSearch");
			Assert.True(search.Focusable);
			AssertInsideWindow(window, search);
			Button next = RequiredTimelineButton(window, "MemoryTimelineNext");
			next.BringIntoView();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			CaptureUi(window, $"memory-timeline-{language}-{width}x{height}");
			search.Focus();
			Assert.True(search.IsKeyboardFocusWithin || search.IsFocused);
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task 时间线编辑归档后按当前筛选刷新且列表仍可删除() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		MemoryItem alpha = fixture._services.Memory.AddAggregate("factual", "alpha-memory", kind: MemoryKind.Factual, canonicalSummary: "alpha-summary");
		MemoryItem beta = fixture._services.Memory.AddAggregate("preference", "beta-memory", kind: MemoryKind.Preference, canonicalSummary: "beta-summary");
		StampMemory(fixture, alpha.Id, "2024-06-03T02:00:00Z", "2024-06-03T02:00:00Z");
		StampMemory(fixture, beta.Id, "2024-06-01T02:00:00Z", "2024-06-01T02:00:00Z");
		MemoryWindow window = new(fixture._services) { Width = 1040, Height = 720 };
		try
		{
			window.Show();
			window.Navigate("memories");
			await window.RefreshAsync();
			await WaitTimelineAsync(() => window.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "删除此记忆")));
			window.Navigate("timeline");
			await window.RefreshAsync();
			await WaitTimelineAsync(() => window.TimelineTotalForTests >= 2 && TimelineHasText(window, "alpha-summary") && TimelineHasText(window, "beta-summary"), window);
			ComboBox activity = NamedControl<ComboBox>(window, "MemoryTimelineActivity");
			activity.SelectedIndex = 1;
			await WaitTimelineAsync(() => TimelineHasText(window, "alpha-summary"), window);
			foreach (object? item in NamedControl<ListBox>(window, "MemoryTimelineList").Items) NamedControl<ListBox>(window, "MemoryTimelineList").ScrollIntoView(item!);
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Button archive = RequiredTimelineButton(window, "MemoryTimelineArchive_" + alpha.Id.ToString(CultureInfo.InvariantCulture));
			archive.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await WaitTimelineAsync(() => window.OwnedWindows.Count > 0);
			Assert.Single(window.OwnedWindows).Close(true);
			await WaitTimelineAsync(() => !TimelineHasText(window, "alpha-summary") && fixture._services.Memory.Get(alpha.Id)!.Status == "archived");
			Assert.Equal("active", fixture._services.Memory.Get(beta.Id)!.Status);
			activity.SelectedIndex = 2;
			await WaitTimelineAsync(() => TimelineHasText(window, "alpha-summary") && !TimelineHasText(window, "beta-summary"));
			activity.SelectedIndex = 0;
			TextBox search = NamedControl<TextBox>(window, "MemoryTimelineSearch");
			search.Text = "beta-summary";
			await WaitTimelineAsync(() => TimelineHasText(window, "beta-summary") && !TimelineHasText(window, "alpha-summary"));
			Button open = RequiredTimelineButton(window, "MemoryTimelineOpen_" + beta.Id.ToString(CultureInfo.InvariantCulture));
			open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await WaitTimelineAsync(() => window.OwnedWindows.Any(dialog => dialog.Name == "MemoryTimelineDetail"));
			Window detail = window.OwnedWindows.Single(dialog => dialog.Name == "MemoryTimelineDetail");
			Assert.Contains(detail.GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.Contains("不会永久删除") == true);
			Assert.DoesNotContain(detail.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "删除此记忆"));
			detail.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "编辑")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await WaitTimelineAsync(() => window.OwnedWindows.Any(dialog => dialog.Name == "MemoryEditor"));
			Window editor = window.OwnedWindows.Single(dialog => dialog.Name == "MemoryEditor");
			editor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "MemoryEditorContent").Text = "beta-edited";
			editor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "MemoryEditorCanonical").Text = "beta-summary-edited";
			editor.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "MemoryEditorSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await WaitTimelineAsync(() => TimelineHasText(window, "beta-summary-edited"));
			Assert.Equal("beta-edited", fixture._services.Memory.Get(beta.Id)!.Content);
			Assert.Contains("beta-summary", search.Text ?? "", StringComparison.Ordinal);
			if (window.OwnedWindows.FirstOrDefault(dialog => dialog.Name == "MemoryEditor") is { } reopened) reopened.Close();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task 时间线旧读取被取消且不会覆盖新筛选() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._services.Memory.AddAggregate("general", "alpha-late", canonicalSummary: "alpha-late");
		fixture._services.Memory.AddAggregate("general", "beta-late", canonicalSummary: "beta-late");
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int calls = 0;
		bool firstCancelled = false;
		fixture._services.Memory.BeforeTimelineRead = cancellation =>
		{
			if (Interlocked.Increment(ref calls) != 1) return;
			started.TrySetResult();
			cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
			firstCancelled = cancellation.IsCancellationRequested;
			cancellation.ThrowIfCancellationRequested();
		};
		MemoryWindow window = new(fixture._services) { Width = 1040, Height = 720 };
		try
		{
			window.Show();
			window.Navigate("timeline");
			_ = window.RefreshAsync();
			await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
			TextBox search = NamedControl<TextBox>(window, "MemoryTimelineSearch");
			search.Text = "beta-late";
			await WaitTimelineAsync(() => TimelineHasText(window, "beta-late") && !TimelineHasText(window, "alpha-late"), window);
			Assert.True(firstCancelled);
			window.Hide();
			Assert.False(window.IsVisible);
		}
		finally
		{
			fixture._services.Memory.BeforeTimelineRead = null;
			window.AllowClose = true;
			window.Close();
		}
	});

	private static void StampMemory(BridgeCommandsTests fixture, long id, string createdAt, string updatedAt) => fixture._database.Locked(connection =>
	{
		using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
		command.CommandText = "UPDATE memories SET created_at = $created, updated_at = $updated WHERE id = $id";
		command.Parameters.AddWithValue("$created", createdAt);
		command.Parameters.AddWithValue("$updated", updatedAt);
		command.Parameters.AddWithValue("$id", id);
		if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("未能写回记忆时间");
	});

	private static bool TimelineHasText(MemoryWindow window, string text)
	{
		ListBox? list = window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(control => control.Name == "MemoryTimelineList");
		if (list?.ItemsSource is IEnumerable rows)
		{
			foreach (object? row in rows)
			{
				if (row is null) continue;
				PropertyInfo? property = row.GetType().GetProperty("Text");
				if (property?.GetValue(row) is string value && value.Contains(text, StringComparison.Ordinal)) return true;
			}
		}
		return window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.IsVisible && block.Text?.Contains(text, StringComparison.Ordinal) == true);
	}

	private static string TimelineNotice(MemoryWindow window) =>
		NamedControl<TextBlock>(window, "MemoryTimelineNotice").Text ?? "";

	private static T NamedControl<T>(MemoryWindow window, string name) where T : Control =>
		window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
		?? throw new InvalidOperationException("缺少控件 " + name);

	private static Button RequiredTimelineButton(MemoryWindow window, string name) =>
		window.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == name)
		?? throw new InvalidOperationException("缺少按钮 " + name);

	private static void AssertInsideWindow(Window window, Control control)
	{
		Point? origin = control.TranslatePoint(default, window);
		Assert.NotNull(origin);
		Assert.InRange(origin.Value.X, 0, window.ClientSize.Width);
		Assert.True(origin.Value.X + control.Bounds.Width <= window.ClientSize.Width + 1);
		Assert.InRange(origin.Value.Y, -1, window.ClientSize.Height);
		Assert.True(origin.Value.Y + control.Bounds.Height <= window.ClientSize.Height + 1);
	}

	private static async Task WaitTimelineAsync(Func<bool> condition, MemoryWindow? window = null)
	{
		for (int attempt = 0; attempt < 120 && !condition(); attempt++)
		{
			await Task.Delay(50);
			if (window is not null) await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
		}
		if (condition()) return;
		string status = window?.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).FirstOrDefault(text => text?.Contains("失败") == true || text?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true)
			?? window?.TimelineTotalForTests.ToString(CultureInfo.InvariantCulture)
			?? "unknown";
		Assert.True(condition(), "时间线未就绪: " + status);
	}
}
