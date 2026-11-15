using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData("zh-CN", 720, 480)]
	[InlineData("zh-CN", 1920, 1080)]
	[InlineData("en-US", 720, 480)]
	[InlineData("en-US", 1920, 1080)]
	public Task 专注设置在两种语言和分辨率下可操作(string language, int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new();
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
		SettingsWindow window = new(fixture._services) { Width = width, Height = height };
		bool english = language == "en-US";
		try
		{
			window.Show();
			SettingsViewModel viewModel = Assert.IsType<SettingsViewModel>(window.DataContext);
			await viewModel.RefreshSnapshotAsync();
			Assert.Equal(language, viewModel.Language);
			window.Navigate("proactive");
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);

			Button start = RequiredButton(window, english ? "Start focus" : "开始专注");
			Button pause = RequiredButton(window, english ? "Pause" : "暂停");
			Button resume = RequiredButton(window, english ? "Resume" : "继续");
			Button end = RequiredButton(window, english ? "End" : "结束");
			Assert.True(start.IsEffectivelyEnabled);
			Assert.False(pause.IsEffectivelyEnabled);
			Assert.False(resume.IsEffectivelyEnabled);
			Assert.False(end.IsEffectivelyEnabled);

			ComboBox duration = FocusDurationCombo(window, english);
			int customIndex = IndexOf(duration, english ? "Custom" : "自定义");
			Assert.True(customIndex >= 0);
			duration.SelectedIndex = customIndex;
			window.UpdateLayout();
			NumericUpDown minutes = Assert.Single(window.GetVisualDescendants().OfType<NumericUpDown>(), box => box.IsVisible && box.Minimum == 1 && box.Maximum == 180);
			Assert.True(minutes.IsVisible);
			minutes.Value = 40;

			ScrollViewer scroll = window.FindControl<ScrollViewer>("SettingsPageScroll") ?? throw new InvalidOperationException("缺少设置页滚动区域");
			Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
			SelectableTextBlock status = Assert.Single(window.GetVisualDescendants().OfType<SelectableTextBlock>(),
				block => (block.Text ?? "").Contains(english ? "Not started" : "未开始", StringComparison.Ordinal));
			status.BringIntoView();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			AssertInside(window, status);
			CaptureUi(window, $"focus-status-{width}x{height}-{language}");
			foreach (Button button in new[] { start, pause, resume, end })
			{
				button.BringIntoView();
				await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
				AssertInside(window, button);
			}
			Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
			if (height == 480) Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
			CaptureUi(window, $"focus-idle-{width}x{height}-{language}");

			start.Command?.Execute(null);
			await WaitForFocusStatusAsync(window, english ? "In progress" : "进行中");
			Assert.False(start.IsEffectivelyEnabled);
			Assert.True(pause.IsEffectivelyEnabled);
			SelectableTextBlock running = Assert.Single(window.GetVisualDescendants().OfType<SelectableTextBlock>(),
				block => (block.Text ?? "").Contains(english ? "In progress" : "进行中", StringComparison.Ordinal));
			running.BringIntoView();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			AssertInside(window, running);
			CaptureUi(window, $"focus-running-{width}x{height}-{language}");

			end.Command?.Execute(null);
			await WaitForFocusStatusAsync(window, english ? "Not started" : "未开始");
			Assert.True(start.IsEffectivelyEnabled);
		}
		finally
		{
			window.AllowClose = true;
			window.Close();
			SettingsLocalization.SetLanguage("zh-CN");
		}
	});

	[Theory]
	[InlineData("zh-CN", 720, 480)]
	[InlineData("en-US", 1920, 1080)]
	public Task 安全模式专注页不能开始(string language, int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
		SettingsWindow window = new(fixture._services) { Width = width, Height = height };
		bool english = language == "en-US";
		try
		{
			window.Show();
			SettingsViewModel viewModel = Assert.IsType<SettingsViewModel>(window.DataContext);
			await viewModel.RefreshSnapshotAsync();
			window.Navigate("proactive");
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Button start = RequiredButton(window, english ? "Start focus" : "开始专注");
			Button resume = RequiredButton(window, english ? "Resume" : "继续");
			start.BringIntoView();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Assert.False(start.IsEffectivelyEnabled);
			Assert.False(resume.IsEffectivelyEnabled);
			AssertInside(window, start);
			CaptureUi(window, $"focus-safe-{width}x{height}-{language}");
		}
		finally
		{
			window.AllowClose = true;
			window.Close();
			SettingsLocalization.SetLanguage("zh-CN");
		}
	});

	private static Button RequiredButton(SettingsWindow window, string content) =>
		Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => button.Content as string == content);

	private static ComboBox FocusDurationCombo(SettingsWindow window, bool english)
	{
		string marker = english ? "25 minutes" : "25 分钟";
		return Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), combo =>
			combo.Items.OfType<string>().Contains(marker));
	}

	private static int IndexOf(ComboBox combo, string text) =>
		combo.Items.OfType<string>().ToList().IndexOf(text);

	private static void AssertInside(SettingsWindow window, Control control)
	{
		Point? origin = control.TranslatePoint(default, window);
		Assert.NotNull(origin);
		Assert.InRange(origin.Value.X, 0, window.ClientSize.Width);
		Assert.True(origin.Value.X + control.Bounds.Width <= window.ClientSize.Width + 1);
		Assert.InRange(origin.Value.Y, -1, window.ClientSize.Height);
		Assert.True(origin.Value.Y + control.Bounds.Height <= window.ClientSize.Height + 1);
	}

	private static async Task WaitForFocusStatusAsync(SettingsWindow window, string expected)
	{
		for (int attempt = 0; attempt < 100; attempt++)
		{
			if (FocusStatusText(window).Contains(expected, StringComparison.Ordinal)) return;
			await Task.Delay(50);
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
		}
		Assert.Contains(expected, FocusStatusText(window), StringComparison.Ordinal);
	}

	private static string FocusStatusText(SettingsWindow window) =>
		string.Join('\n', window.GetVisualDescendants().OfType<SelectableTextBlock>().Select(block => block.Text));
}
