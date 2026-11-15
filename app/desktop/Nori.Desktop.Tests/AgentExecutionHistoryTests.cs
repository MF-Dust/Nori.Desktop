using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Nori.Core.Agent;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Settings;
using Nori.Desktop.Settings.Pages;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData("zh-CN", 720, 480, 1d)]
	[InlineData("zh-CN", 1920, 1080, 1d)]
	[InlineData("en-US", 720, 480, 1d)]
	[InlineData("en-US", 1920, 1080, 1d)]
	[InlineData("zh-CN", 1920, 1080, 2d)]
	public Task 执行记录在两种语言和分辨率下可聚焦(string language, int width, int height, double scale) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SeedExecution(fixture._services.AgentExecution, "readFile token=secret-value");
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
		SettingsWindow window = new() { Width = width, Height = height };
		using SettingsService service = new(fixture._services, window);
		using SettingsViewModel viewModel = new(service);
		window.DataContext = viewModel;
		try
		{
			TrySetScale(window, scale);
			window.Show();
			await viewModel.RefreshSnapshotAsync();
			viewModel.Navigate("debug");
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			NativeSettingsPagePresenter presenter = ExecutionPresenter(window);
			presenter.RunDebugRefreshTickForTests();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);

			Control card = window.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "AgentExecutionCard");
			card.BringIntoView();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			ScrollViewer scroll = window.FindControl<ScrollViewer>("SettingsPageScroll")!;
			Assert.True(card.Bounds.Width > 160);
			Assert.True(card.Bounds.Width <= scroll.Viewport.Width + 1);
			Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);

			bool english = language == "en-US";
			string title = english ? "Execution history" : "执行记录";
			string denied = english ? "Denied" : "已拒绝";
			string usageNone = english ? "Usage unavailable" : "用量不可用";
			string cardText = string.Join("\n", card.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text));
			Assert.Contains(title, cardText, StringComparison.Ordinal);

			AgentExecutionPresentation presentation = AgentExecutionText.Present(ExecutionPage(viewModel).Execution);
			Assert.Contains(denied, presentation.CopyText, StringComparison.Ordinal);
			Assert.Contains(usageNone, presentation.CopyText, StringComparison.Ordinal);
			Assert.DoesNotContain("secret-value", presentation.CopyText, StringComparison.Ordinal);
			Assert.DoesNotContain("缓存 0%", presentation.CopyText, StringComparison.Ordinal);
			Assert.DoesNotContain("Cache 0%", presentation.CopyText, StringComparison.Ordinal);

			ListBox list = window.GetLogicalDescendants().OfType<ListBox>().Single(item => item.Name == "AgentExecutionList");
			Button copy = window.GetLogicalDescendants().OfType<Button>().Single(item => item.Name == "AgentExecutionCopy");
			IEnumerable rows = Assert.IsAssignableFrom<IEnumerable>(list.ItemsSource);
			Assert.Contains(rows.Cast<object>(), row => row is AgentExecutionLine line && line.Text.Contains(denied, StringComparison.Ordinal));
			list.Focus();
			Assert.True(list.IsFocused);
			Assert.True(list.Focusable);
			copy.Focus();
			Assert.True(copy.IsFocused);
			Assert.True(copy.IsEffectivelyEnabled);
			CaptureExecutionEvidence(window, card, presentation,
				"execution-" + language + "-" + width + "x" + height + (scale > 1 ? "-dpi2" : ""),
				language, width, height, scale);
		}
		finally
		{
			window.DataContext = null;
			window.Close();
			SettingsLocalization.SetLanguage("zh-CN");
		}
	});

	[Fact]
	public Task 执行记录隐藏和释放后停止刷新() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("zh-CN"));
		SettingsWindow window = new() { Width = 720, Height = 480 };
		using SettingsService service = new(fixture._services, window);
		using SettingsViewModel viewModel = new(service);
		window.DataContext = viewModel;
		try
		{
			Assert.Equal(1, fixture._services.AgentExecution.ListenerCount);
			window.Show();
			await viewModel.RefreshSnapshotAsync();
			viewModel.Navigate("debug");
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			NativeSettingsPagePresenter presenter = ExecutionPresenter(window);
			DebugSettingsViewModel debug = ExecutionPage(viewModel);
			Assert.True(presenter.DebugRefreshActive);
			presenter.RunDebugRefreshTickForTests();
			int visibleReads = debug.ExecutionReadCount;
			Assert.True(visibleReads > 0);

			window.Hide();
			fixture._services.AgentExecution.Open("hidden");
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			presenter.RunDebugRefreshTickForTests();
			Assert.Equal(visibleReads, debug.ExecutionReadCount);

			window.Show();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			fixture._services.AgentExecution.Open("shown");
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			Assert.True(debug.ExecutionReadCount > visibleReads);

			viewModel.Navigate("general");
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Assert.False(presenter.DebugRefreshActive);
			int afterLeave = debug.ExecutionReadCount;
			fixture._services.AgentExecution.Open("left");
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			presenter.RunDebugRefreshTickForTests();
			Assert.Equal(afterLeave, debug.ExecutionReadCount);
		}
		finally
		{
			window.DataContext = null;
			window.Close();
			SettingsLocalization.SetLanguage("zh-CN");
		}
		service.Dispose();
		Assert.Equal(0, fixture._services.AgentExecution.ListenerCount);
		fixture._services.AgentExecution.Open("disposed");
	});

	[Fact]
	public void 导出的执行记录沿用脱敏边界()
	{
		string directory = Path.Combine(Path.GetTempPath(), "nori-exec-export-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			using AgentExecutionJournal journal = new();
			AgentExecutionRequest request = journal.Open("session");
			request.ToolStarted(0, "call", "tool token=secret-value");
			request.FinishTool(0, "call", "tool", 1, AgentExecutionStatus.Failed, "boom /home/user/notes.txt api_key=secret-value", false);
			using FileLogger logger = new(Path.Combine(directory, "logs"));
			string target = Path.Combine(directory, "diagnostics.zip");
			DiagnosticExporter.Export(target, logger, null, new Nori.Core.Data.AppStoragePaths(directory), true, default, null, journal);
			using System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.OpenRead(target);
			string execution = ReadZip(archive, "agent-execution.json");
			Assert.Contains("Failed", execution, StringComparison.Ordinal);
			Assert.DoesNotContain("secret-value", execution, StringComparison.Ordinal);
			Assert.DoesNotContain("/home/user", execution, StringComparison.Ordinal);
			Assert.DoesNotContain("notes.txt", execution, StringComparison.Ordinal);
			using JsonDocument document = JsonDocument.Parse(execution);
			Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("summary").GetProperty("PromptTokens").ValueKind);
		}
		finally
		{
			try { Directory.Delete(directory, true); } catch (IOException) { }
		}
	}

	private static void SeedExecution(AgentExecutionJournal journal, string toolName)
	{
		AgentExecutionRequest request = journal.Open("session");
		request.Model(0, 18, AgentExecutionStatus.Success, null, null);
		request.ToolPending(0, "call-1", toolName);
		request.ToolStarted(0, "call-1", toolName);
		request.Permission(0, "call-1", toolName, "confirm", AgentExecutionStatus.Pending, "requested");
		request.Permission(0, "call-1", toolName, "confirm", AgentExecutionStatus.Denied, "denied");
		request.FinishTool(0, "call-1", toolName, 12, AgentExecutionStatus.Denied, "denied", false);
		request.End(40, AgentExecutionStatus.Success, null);
	}

	private static void TrySetScale(TopLevel window, double scale)
	{
		if (scale <= 1) return;
		PropertyInfo? property = typeof(TopLevel).GetProperty("RenderScaling", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (property?.CanWrite == true) property.SetValue(window, scale);
	}

	private static NativeSettingsPagePresenter ExecutionPresenter(SettingsWindow window)
	{
		SettingsPagePresenter page = window.FindControl<SettingsPagePresenter>("PagePresenter")!;
		return Assert.IsType<NativeSettingsPagePresenter>(page.Content);
	}

	private static DebugSettingsViewModel ExecutionPage(SettingsViewModel viewModel) =>
		Assert.IsType<DebugSettingsViewModel>(Assert.IsType<DebugSettingsPage>(viewModel.CurrentPage).ComplexViewModel);

	private static string ReadZip(System.IO.Compression.ZipArchive archive, string name)
	{
		System.IO.Compression.ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidOperationException(name);
		using Stream stream = entry.Open();
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}

	/// <summary>
	/// 有 NORI_UI_CAPTURE_DIR 时优先保存真实帧；默认 headless drawing 无帧则写标注证据，不声称实机截图。
	/// </summary>
	private static void CaptureExecutionEvidence(
		TopLevel surface, Control card, AgentExecutionPresentation presentation,
		string name, string language, int width, int height, double scale)
	{
		string? directory = Environment.GetEnvironmentVariable("NORI_UI_CAPTURE_DIR");
		if (string.IsNullOrWhiteSpace(directory)) return;
		Directory.CreateDirectory(directory);
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		using WriteableBitmap? frame = surface.CaptureRenderedFrame();
		StringBuilder notes = new();
		notes.AppendLine("mode: " + (frame is null ? "headless-annotated" : "rendered-frame"));
		notes.AppendLine("language: " + language);
		notes.AppendLine("window: " + width + "x" + height);
		notes.AppendLine("scale-request: " + scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
		notes.AppendLine("card: " + card.Bounds.Width.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
			+ "x" + card.Bounds.Height.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
		notes.AppendLine("lines: " + presentation.Lines.Count);
		notes.AppendLine("--- summary ---");
		notes.AppendLine(presentation.Summary);
		notes.AppendLine("--- copy ---");
		notes.AppendLine(presentation.CopyText);
		File.WriteAllText(Path.Combine(directory, name + ".txt"), notes.ToString());
		if (frame is not null)
			frame.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
	}
}
