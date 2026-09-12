using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Logging;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>只在专门的视觉 CI 步骤中启用真实 Skia 渲染，普通单测明确报告未执行截图。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NativeSettingsVisualFactAttribute : FactAttribute
{
	/// <summary>视觉 CI 必须显式设置环境变量，避免普通功能单测隐式生成截图。</summary>
	public NativeSettingsVisualFactAttribute()
	{
		if (Environment.GetEnvironmentVariable("NORI_CAPTURE_SETTINGS") != "1")
			Skip = "由设置视觉 CI 步骤使用 NORI_CAPTURE_SETTINGS=1 执行真实渲染。";
	}
}

public partial class BridgeCommandsTests
{
	private sealed class NativeSettingsVisualApplicationBuilder
	{
		/// <summary>保留生产 App 主题初始化，使用真实 Skia 画布而非无绘制占位实现。</summary>
		public static AppBuilder BuildAvaloniaApp() =>
			AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions
			{
				UseHeadlessDrawing = false,
			});
	}

	/// <summary>用隔离数据库和安全模式渲染设置页，保存最小尺寸与全高清明暗截图及布局清单。</summary>
	[NativeSettingsVisualFact]
	public async Task NativeSettingsVisualCaptureProducesInspectableFrames()
	{
		Assert.Equal("1", Environment.GetEnvironmentVariable("NORI_CAPTURE_SETTINGS"));
		string outputDirectory = NativeSettingsCaptureDirectory();
		Directory.CreateDirectory(outputDirectory);
		List<object> manifest = [];
		using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(NativeSettingsVisualApplicationBuilder));
		await session.Dispatch(async () =>
		{
			using BridgeCommandsTests fixture = new(safeMode: true);
			// 固定英文以检查较长文案；只使用临时数据库和保留测试域名，不加载个人配置。
			fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("en-US"));
			fixture._config.Set(AiSettingsStore.KeyLlmBaseUrl, new ConfigValue.Text("https://api.example.test/v1"));
			fixture._config.Set(AiSettingsStore.KeyLlmModel, new ConfigValue.Text("nori-example"));
			for (int index = 0; index < 8; index++)
				fixture._services.Logger.Write(LogSource.Backend, index % 3 == 0 ? "warn" : "info", $"Synthetic visual verification message {index + 1:D2}");

			foreach ((int width, int height) in new[] {(720, 480), (1920, 1080)})
			foreach (ThemeVariant theme in new[] {ThemeVariant.Light, ThemeVariant.Dark})
			foreach (string page in new[] {"ai", "voice", "proactive", "skills", "mcp", "automation", "plugins", "general", "updates", "debug", "about"})
			{
				SettingsWindow window = new() {Width = width, Height = height, RequestedThemeVariant = theme};
				using SettingsService service = new(fixture._services, window);
				using SettingsViewModel viewModel = new(service);
				window.DataContext = viewModel;
				try
				{
					window.Show();
					viewModel.Navigate(page);
					await viewModel.RefreshSnapshotAsync();
					await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
					Assert.Empty(viewModel.ErrorMessage);
					Assert.Equal(page, viewModel.CurrentPage?.Key);
					Assert.Equal(theme, window.ActualThemeVariant);

					SettingsPagePresenter presenter = Assert.IsType<SettingsPagePresenter>(window.FindControl<SettingsPagePresenter>("PagePresenter"));
					ScrollViewer scroll = Assert.IsType<ScrollViewer>(window.FindControl<ScrollViewer>("SettingsPageScroll"));
					using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
					string themeName = theme == ThemeVariant.Dark ? "dark" : "light";
					string fileName = $"{page}-{themeName}-{width}x{height}.png";
					string pngPath = Path.Combine(outputDirectory, fileName);
					frame.Save(pngPath);
					byte[] png = File.ReadAllBytes(pngPath);
					string preview = "NORI_SETTINGS_PREVIEW_PNG_BASE64:" + Convert.ToBase64String(png);
					File.WriteAllText(Path.Combine(outputDirectory, fileName + ".base64.txt"), preview);
					if (page == "ai" && themeName == "dark" && width == 720)
						File.WriteAllText(Path.Combine(outputDirectory, "preview-base64.txt"), preview);

					int sampledColors = NativeSettingsSampledColors(frame);
					SettingsFieldPresenter[] fields = presenter.GetVisualDescendants().OfType<SettingsFieldPresenter>()
						.Where(field => field.IsEffectivelyVisible).ToArray();
					manifest.Add(new
					{
						fileName,
						page,
						theme = themeName,
						language = viewModel.Language,
						pixelWidth = frame.PixelSize.Width,
						pixelHeight = frame.PixelSize.Height,
						viewportWidth = scroll.Viewport.Width,
						viewportHeight = scroll.Viewport.Height,
						extentWidth = scroll.Extent.Width,
						extentHeight = scroll.Extent.Height,
						sampledColors,
						sha256 = Convert.ToHexString(SHA256.HashData(png)),
						fields = fields.Select(field => new
						{
							key = (field.DataContext as SettingsFieldViewModel)?.Key,
							width = field.Bounds.Width,
							height = field.Bounds.Height,
						}).ToArray(),
					});
					// 每张图立即更新清单，即使后续尺寸检查失败也保留可诊断产物。
					File.WriteAllText(Path.Combine(outputDirectory, "manifest.json"), JsonSerializer.Serialize(new
					{
						renderer = "Avalonia 12.1.1 / Skia / Headless",
						syntheticData = true,
						captures = manifest,
					}, new JsonSerializerOptions {WriteIndented = true}));

					Assert.Equal(width, frame.PixelSize.Width);
					Assert.Equal(height, frame.PixelSize.Height);
					Assert.True(sampledColors > 8, $"真实渲染内容不足：{fileName}，采样颜色数 {sampledColors}");
					Assert.True(presenter.Bounds.Width > 0 && presenter.Bounds.Height > 0);
					Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 2, $"页面横向溢出：{fileName}");
					if (page is "ai" or "voice" or "proactive" or "general" or "updates" or "about") Assert.NotEmpty(fields);
					Assert.All(fields, field => Assert.True(field.Bounds.Width > 0 && field.Bounds.Height > 0));
				}
				finally
				{
					window.DataContext = null;
					window.Close();
				}
			}
			SettingsLocalization.SetLanguage("zh-CN");
			return true;
		}, CancellationToken.None);
		Assert.Equal(44, manifest.Count);
	}

	private static int NativeSettingsSampledColors(WriteableBitmap frame)
	{
		HashSet<int> colors = [];
		using var pixels = frame.Lock();
		for (int y = 0; y < frame.PixelSize.Height; y += 13)
			for (int x = 0; x < frame.PixelSize.Width; x += 13)
				colors.Add(Marshal.ReadInt32(pixels.Address, y * pixels.RowBytes + x * 4));
		return colors.Count;
	}

	private static string NativeSettingsCaptureDirectory()
	{
		if (Environment.GetEnvironmentVariable("NORI_SETTINGS_CAPTURE_DIR") is {Length: > 0} configured)
			return Path.GetFullPath(configured);
		for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
			if (Directory.Exists(Path.Combine(directory.FullName, "Nori.Desktop")) && Directory.Exists(Path.Combine(directory.FullName, "Nori.Desktop.Tests")))
				return Path.Combine(directory.FullName, "artifacts", "native-settings");
		throw new DirectoryNotFoundException("找不到设置视觉产物目录；请设置 NORI_SETTINGS_CAPTURE_DIR。");
	}
}
