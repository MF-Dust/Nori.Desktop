using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativeManagementWindowsRemainDarkWhenApplicationThemeChanges() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		Avalonia.Application application = Avalonia.Application.Current!;
		ThemeVariant? original = application.RequestedThemeVariant;
		MemoryWindow memory = new(fixture._services);
		SettingsWindow settings = new();
		try
		{
			application.RequestedThemeVariant = ThemeVariant.Light;
			settings.Show(); memory.Show();
			await RefreshMemoryWindowForTest(memory);
			Assert.Equal(ThemeVariant.Dark, settings.ActualThemeVariant);
			Assert.Equal(ThemeVariant.Dark, memory.ActualThemeVariant);
			await memory.OpenEditorAsync(null);
			Window editor = memory.OwnedWindows.Single(dialog => dialog.Name == "MemoryEditor");
			Assert.Equal(ThemeVariant.Dark, editor.ActualThemeVariant);
			Window settingsDialog = Nori.Desktop.Settings.Pages.NativeSettingsDialogs.CreateWindow("合成深色确认", new TextBlock { Text = "验证深色主题" });
			try
			{
				settingsDialog.Show();
				Assert.Equal(ThemeVariant.Dark, settingsDialog.ActualThemeVariant);
			}
			finally { settingsDialog.Close(); }
			application.RequestedThemeVariant = ThemeVariant.Dark;
			application.RequestedThemeVariant = ThemeVariant.Light;
			Assert.Equal(ThemeVariant.Dark, settings.ActualThemeVariant);
			Assert.Equal(ThemeVariant.Dark, memory.ActualThemeVariant);
			Assert.Equal(ThemeVariant.Dark, editor.ActualThemeVariant);
			editor.Close();
			await memory.PrepareShutdownAsync();
		}
		finally
		{
			memory.AllowClose = true; memory.Close(); settings.Close();
			application.RequestedThemeVariant = original;
		}
	});

	[Fact]
	public Task NativeMemoryWindowKeepsPageAndSearchDraftWhenReopened() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SeedNativeMemoryVisualData(fixture);
		MemoryWindow window = new(fixture._services);
		try
		{
			window.Show();
			window.Navigate("memories");
			await RefreshMemoryWindowForTest(window);
			Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
			Assert.Contains("合成测试记忆", MemoryPageText(window));
			TextBox search = window.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "MemorySearch_memories");
			search.Text = "__no_matching_memory__";
			await Task.Delay(350);
			await RefreshMemoryWindowForTest(window);
			Assert.DoesNotContain("合成测试记忆", MemoryPageText(window));
			Control page = window.PageContent;
			window.Hide();
			window.Navigate(null);
			window.Show();
			await RefreshMemoryWindowForTest(window);
			Assert.Equal("memories", window.CurrentSection);
			Assert.Same(page, window.PageContent);
			Assert.Equal("__no_matching_memory__", search.Text);
			search.Text = "";
			await Task.Delay(350);
			await RefreshMemoryWindowForTest(window);
			Assert.Contains("合成测试记忆", MemoryPageText(window));
			window.Navigate("archive");
			await RefreshMemoryWindowForTest(window);
			Assert.Contains("已归档", MemoryPageText(window));
			Assert.Contains("合成测试记忆", MemoryPageText(window));
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeMemoryWindowSavesAdvancedFieldsAndPreservesControlsOnLanguageChange() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		MemoryWindow window = new(fixture._services);
		try
		{
			window.Show(); window.Navigate("advanced");
			await RefreshMemoryWindowForTest(window);
			NumericUpDown recall = window.GetVisualDescendants().OfType<NumericUpDown>().Single(control => control.Name == "MemorySetting_recallTopK");
			recall.Value = 9.5m;
			Assert.False(await window.FlushPendingSavesAsync());
			Assert.NotEqual(9, fixture._runtime.Memory.Settings.RecallTopK);
			Assert.Equal(9.5m, recall.Value);
			recall.Value = 9;
			await RefreshMemoryWindowForTest(window);
			Assert.Equal(9m, recall.Value);
			Assert.True(await window.FlushPendingSavesAsync());
			Assert.Equal(9, fixture._runtime.Memory.Settings.RecallTopK);
			fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("en-US"));
			fixture._runtime.InvalidateSnapshot("general");
			await RefreshMemoryWindowForTest(window);
			await WaitUntilAsync(() => MemoryPageText(window).Contains("Automatic reflection", StringComparison.Ordinal));
			Assert.Same(recall, window.GetVisualDescendants().OfType<NumericUpDown>().Single(control => control.Name == "MemorySetting_recallTopK"));
			Assert.Equal(9m, recall.Value);
			Assert.Contains("Automatic reflection", MemoryPageText(window));
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeMemoryDebuggerDisplaysStructuredHitsAndInjectedContent() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		MemoryWindow window = new(fixture._services);
		try
		{
			window.Show(); window.Navigate("debugger");
			await RefreshMemoryWindowForTest(window);
			window.RenderDebuggerResult(SyntheticMemoryRecallResult(), "一起读书");
			window.UpdateLayout();
			string text = MemoryPageText(window);
			foreach (string content in new[] {"0.8800", "0.9200", "0.8500", "0.9500", "3, 4", "合成规范摘要", "记得和你一起读书", "合成事实原子", "合成知识", "conscious", "合成记忆残响"})
				Assert.Contains(content, text);
			Assert.DoesNotContain("\"keywordHits\"", text);
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	private static async Task RefreshMemoryWindowForTest(MemoryWindow window)
	{
		await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
		await window.RefreshAsync();
		await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
	}

	private static string MemoryPageText(MemoryWindow window) => string.Join("\n", window.PageContent.GetVisualDescendants().OfType<TextBlock>().Select(control => control.Text));
}
