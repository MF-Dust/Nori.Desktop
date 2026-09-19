using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[NativeSettingsVisualFact]
	public async Task NativeLoggingAndModelPopupVisualCapture()
	{
		string output = Path.Combine(NativeSettingsCaptureDirectory(), "logging");
		Directory.CreateDirectory(output);
		await VisualUiSession.Value.Dispatch(async () =>
		{
			using BridgeCommandsTests fixture = new(safeMode: true);
			SeedNativeModels(fixture);
			foreach (string language in new[] { "zh-CN", "en-US" })
			foreach ((int width, int height) in new[] { (720, 480), (1920, 1080) })
			{
				fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
				SettingsWindow window = new() { Width = width, Height = height };
				using SettingsService service = new(fixture._services, window);
				using SettingsViewModel viewModel = new(service);
				window.DataContext = viewModel;
				try
				{
					for (int index = 0; index < 80; index++) fixture._services.Logger.Write(LogSource.Backend, index % 3 == 0 ? "error" : "info", $"合成诊断事件 {index}", "VisualTest", "diagnostics.test");
					window.Show(); viewModel.Navigate("debug"); await viewModel.RefreshSnapshotAsync();
					await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
					StackPanel card = window.GetVisualDescendants().OfType<StackPanel>().Single(control => control.Name == "DebugLogCard");
					ScrollViewer scroll = window.FindControl<ScrollViewer>("SettingsPageScroll")!;
					Point position = card.TranslatePoint(default, (Visual)scroll.Content!)!.Value;
					scroll.Offset = new Vector(0, position.Y);
					await Capture(window, $"logging-{language}-{width}x{height}");
					Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 2);
					ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "DebugLogList");
					Assert.True(list.GetVisualDescendants().OfType<ListBoxItem>().Count() < 80);
				}
				finally { window.DataContext = null; window.Close(); }
				ModelsWindow models = new(fixture._services) { Width = width, Height = height };
				try
				{
					models.Show(); await RefreshModelsForTest(models); await models.OpenAdjustAsync("arg-nori"); models.UpdateLayout();
					ComboBox combo = ModelControl<ComboBox>(models, "ModelsDisplay_arg-nori_maxFps");
					combo.BringIntoView(); combo.IsDropDownOpen = true;
					await Capture(models, $"models-popup-{language}-{width}x{height}");
					combo.IsDropDownOpen = false;
					ModelControl<Button>(models, "ModelsTabInteractions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
					models.AddRegion(); models.UpdateLayout();
					ModelControl<Button>(models, "ModelsRegionClear").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
					Window confirm = Assert.Single(models.OwnedWindows);
					await Capture(confirm, $"models-confirm-{language}-{width}x{height}");
					confirm.Close(false); await models.PrepareShutdownAsync();
				}
				finally { models.AllowClose = true; models.Close(); }
			}
			return true;
		}, CancellationToken.None);

		async Task Capture(Window window, string name)
		{
			window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			await Task.Delay(250); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
			Assert.True(NativeSettingsSampledColors(frame) > 8);
			frame.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
		}
	}
}
