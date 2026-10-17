using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	/// <summary>
	/// 按需导出原生窗口画廊，供视觉对齐比对；未设置 NORI_UI_CAPTURE_DIR 时只验证窗口可构建并呈现。
	/// </summary>
	[Fact]
	public Task 原生窗口画廊可完整呈现() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SettingsLocalization.SetLanguage("zh-CN");
		List<Window> opened = [];
		async Task ShowAsync(Window window, double width, double height)
		{
			window.Width = width;
			window.Height = height;
			window.Show();
			opened.Add(window);
			window.UpdateLayout();
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			window.UpdateLayout();
		}
		try
		{
			SettingsWindow settings = new(fixture._services);
			await ShowAsync(settings, 960, 680);
			foreach (string page in new[] { "general", "ai", "voice", "workspace", "expression", "about" })
			{
				settings.Navigate(page);
				settings.UpdateLayout();
				await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
				CaptureUi(settings, "settings-" + page);
			}
			// 最小窗口尺寸下的导航与表单不应溢出。
			settings.Width = 720; settings.Height = 480; settings.Navigate("ai"); settings.UpdateLayout();
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			CaptureUi(settings, "settings-ai-720x480");
			settings.AllowClose = true;

			ModelsWindow models = new(fixture._services);
			await ShowAsync(models, 960, 680);
			await models.RefreshAsync();
			CaptureUi(models, "models-library");
			models.Navigate("behaviors");
			models.UpdateLayout();
			CaptureUi(models, "models-behaviors");
			models.AllowClose = true;

			MemoryWindow memory = new(fixture._services);
			await ShowAsync(memory, 960, 680);
			await memory.RefreshAsync();
			CaptureUi(memory, "memory-overview");
			memory.AllowClose = true;

			ChatWindow chat = new(fixture._services);
			await ShowAsync(chat, 560, 720);
			CaptureUi(chat, "chat");
			chat.AllowClose = true;

			MainWindow main = new(WindowDefinition.All.Single(item => item.Label == WindowLabels.Main), fixture._services);
			await ShowAsync(main, 960, 640);
			CaptureUi(main, "main");
			main.Width = 720; main.Height = 480; main.UpdateLayout();
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			CaptureUi(main, "main-720x480");
			main.AllowClose = true;

			FirstRunWindow firstRun = new(FirstRunDefinition(), fixture._services);
			await ShowAsync(firstRun, firstRun.Width, firstRun.Height);
			CaptureUi(firstRun, "first-run");
			firstRun.AllowClose = true;

			InitWindow init = new(InitDefinition(), fixture._services);
			await ShowAsync(init, init.Width, init.Height);
			CaptureUi(init, "init");
			init.AllowClose = true;
		}
		finally
		{
			foreach (Window window in opened)
			{
				switch (window)
				{
					case SettingsWindow item: item.AllowClose = true; break;
					case ModelsWindow item: item.AllowClose = true; break;
					case MemoryWindow item: item.AllowClose = true; break;
					case ChatWindow item: item.AllowClose = true; break;
					case MainWindow item: item.AllowClose = true; break;
					case FirstRunWindow item: item.AllowClose = true; break;
					case InitWindow item: item.AllowClose = true; break;
				}
				window.Close();
			}
		}
	});
}
