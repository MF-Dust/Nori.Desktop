using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>截图覆盖原生深色布局；合成元数据不冒充真实 Live2D 渲染。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NativeModelsVisualFactAttribute : FactAttribute
{
	/// <summary>仅在显式请求视觉验证时生成文件。</summary>
	public NativeModelsVisualFactAttribute()
	{
		if (Environment.GetEnvironmentVariable("NORI_CAPTURE_MODELS") != "1") Skip = "设置 NORI_CAPTURE_MODELS=1 执行原生模型布局截图。";
	}
}

public partial class BridgeCommandsTests
{
	[NativeModelsVisualFact]
	public async Task NativeModelsVisualCaptureCoversDarkLibraryBehaviorDisplayAndRegions()
	{
		string output = Path.Combine(Path.GetDirectoryName(NativeSettingsCaptureDirectory())!, "native-models"); Directory.CreateDirectory(output);
		List<object> manifest = [];
		using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(NativeSettingsVisualApplicationBuilder));
		await session.Dispatch(async () =>
		{
			using BridgeCommandsTests fixture = new(safeMode: true); SeedNativeModels(fixture);
			foreach (string language in new[] { "zh-CN", "en-US" })
			foreach ((int width, int height) in new[] { (720, 480), (960, 640), (1920, 1080) })
			{
				fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language)); fixture._runtime.InvalidateSnapshot("general");
				ModelsWindow window = new(fixture._services) { Width = width, Height = height };
				try
				{
					window.Show(); await RefreshModelsForTest(window);
					foreach (string page in new[] { "library", "behaviors", "display", "regions" })
					{
						if (page is "library" or "behaviors") window.Navigate(page);
						else if (page == "display") await window.OpenAdjustAsync("arg-nori");
						else
						{
							ModelControl<Button>(window, "ModelsTabInteractions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.AddRegion();
						}
						await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
						AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(120); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
						using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
						string file = $"{page}-{language}-{width}x{height}.png"; frame.Save(Path.Combine(output, file), PngBitmapEncoderOptions.Default);
						Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant); Assert.True(NativeSettingsSampledColors(frame) > 8);
						foreach (ScrollViewer scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(control => control.IsEffectivelyVisible && control.Viewport.Width > 0))
							Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 2, $"原生模型布局横向溢出：{file}，{scroll.Name}");
						if (page is "display" or "regions")
						{
							Border stage = ModelControl<Border>(window, "ModelsPreviewHost");
							Avalonia.Point stageOrigin = stage.TranslatePoint(default, window)!.Value;
							Avalonia.Point footerOrigin = ModelControl<Button>(window, "ModelsRetry").TranslatePoint(default, window)!.Value;
							Assert.True(stageOrigin.Y + stage.Bounds.Height <= footerOrigin.Y - 8, $"原生预览舞台被页脚裁切：{file}");
							Grid layout = ModelControl<Grid>(window, "ModelsAdjustLayout");
							Assert.Equal(width == 720 ? 2 : 1, layout.RowDefinitions.Count);
							Assert.Equal(width == 720 ? 1 : 2, layout.ColumnDefinitions.Count);
						}
						manifest.Add(new { file, page, language, width, height, theme = "dark", syntheticMetadata = true, nativeLive2DRendered = false });
						if (page == "regions" && language == "zh-CN" && width == 960)
						{
							ComboBox menu = ModelControl<ComboBox>(window, "ModelsRegionList");
							menu.BringIntoView(); menu.IsDropDownOpen = true;
							await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
							AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(120); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
							Popup popup = Assert.Single(menu.GetVisualDescendants().OfType<Popup>());
							TopLevel popupRoot = TopLevel.GetTopLevel(popup.Child!)!;
							using WriteableBitmap popupFrame = Assert.IsType<WriteableBitmap>(popupRoot.CaptureRenderedFrame());
							const string popupFile = "region-options-zh-CN.png";
							popupFrame.Save(Path.Combine(output, popupFile), PngBitmapEncoderOptions.Default);
							manifest.Add(new { file = popupFile, page = "region-options", language, width = popupFrame.PixelSize.Width, height = popupFrame.PixelSize.Height, theme = "dark", syntheticMetadata = true, nativeLive2DRendered = false });
							menu.IsDropDownOpen = false;
						}
					}
					window.ClearRegions(); await window.PrepareShutdownAsync();
				}
				finally { window.AllowClose = true; window.Close(); }
			}
			return true;
		}, CancellationToken.None);
		Assert.Equal(25, manifest.Count);
		await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
	}
}
