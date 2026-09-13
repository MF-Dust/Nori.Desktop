using System.Text.Json;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Core.Memory;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>显式启用原生记忆真实渲染，普通测试不产生截图文件。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NativeMemoryVisualFactAttribute : FactAttribute
{
	/// <summary>仅视觉验证步骤启用截图。</summary>
	public NativeMemoryVisualFactAttribute()
	{
		if (Environment.GetEnvironmentVariable("NORI_CAPTURE_MEMORY") != "1")
			Skip = "设置 NORI_CAPTURE_MEMORY=1 执行原生记忆真实渲染。";
	}
}

public partial class BridgeCommandsTests
{
	private static readonly string[] MemoryPages = ["overview", "memories", "atoms", "knowledge", "archive", "transfer", "debugger", "advanced"];

	/// <summary>使用合成记忆与隔离数据库生成八分区的中英文、最小尺寸和全高清深色截图。</summary>
	[NativeMemoryVisualFact]
	public async Task NativeMemoryVisualCaptureCoversEveryPage()
	{
		string output = Path.Combine(Path.GetDirectoryName(NativeSettingsCaptureDirectory())!, "native-memory");
		Directory.CreateDirectory(output);
		List<object> manifest = [];
		using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(NativeSettingsVisualApplicationBuilder));
		await session.Dispatch(async () =>
		{
			using BridgeCommandsTests fixture = new(safeMode: true);
			SeedNativeMemoryVisualData(fixture);
			foreach (string language in new[] {"zh-CN", "en-US"})
			{
				fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
				fixture._runtime.InvalidateSnapshot("general");
				foreach ((int width, int height) in new[] {(720, 480), (960, 640), (1920, 1080)})
				{
					MemoryWindow window = new(fixture._services) {Width = width, Height = height};
					try
					{
						window.Show();
						foreach (string page in MemoryPages)
						{
							window.Navigate(page);
							await window.RefreshAsync();
							await WaitUntilAsync(() => window.Title == Nori.Desktop.Memory.MemoryResources.Get("header.title", language));
							Assert.Equal(Nori.Desktop.Memory.MemoryResources.Get("header.title", language), window.Title);
							if (page == "debugger") window.RenderDebuggerResult(SyntheticMemoryRecallResult(), "一起读书");
							if (page == "transfer")
							{
								await window.ExportMemoriesAsync();
								using MemoryStream stream = new(Encoding.UTF8.GetBytes("{\"version\":\"nori-memory-v1\",\"memories\":[{\"content\":\"合成迁移预览 Synthetic import preview\",\"kind\":\"preference\",\"importance\":0.8,\"tags\":\"reading,生活\"}]}"));
								await window.LoadTransferStreamAsync(stream, "synthetic-memory.json");
								await window.PreviewImportAsync();
							}
							await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
							AvaloniaHeadlessPlatform.ForceRenderTimerTick();
							await Task.Delay(240);
							AvaloniaHeadlessPlatform.ForceRenderTimerTick();
							using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
							string fileName = $"{page}-{language}-{width}x{height}.png";
							frame.Save(Path.Combine(output, fileName), PngBitmapEncoderOptions.Default);
							Assert.Equal(width, frame.PixelSize.Width);
							Assert.Equal(height, frame.PixelSize.Height);
							Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
							Assert.True(NativeSettingsSampledColors(frame) > 8, $"记忆页面未真实绘制：{fileName}");
							ScrollViewer[] scrolls = window.GetVisualDescendants().OfType<ScrollViewer>()
								.Where(scroll => scroll.IsEffectivelyVisible && scroll.Viewport.Width > 0).ToArray();
							Assert.NotEmpty(scrolls);
							foreach (ScrollViewer scroll in scrolls)
								Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 2, $"记忆页面横向溢出：{fileName}，{scroll.Name}");
							manifest.Add(new {fileName, page, language, width, height, theme = "dark", syntheticData = true});
							if (page == "transfer")
							{
								ScrollMemoryWindowToEnd(window);
								await CaptureAdditionalMemoryFrame(window, $"transfer-preview-{language}-{width}x{height}", output, manifest);
							}
						}
						await window.OpenEditorAsync(fixture._services.Memory.GetAll(100).First(item => item.Status == "active").Id);
						Window editor = window.OwnedWindows.Single(dialog => dialog.Name == "MemoryEditor");
						await CaptureAdditionalMemoryFrame(editor, $"detail-{language}-{width}x{height}", output, manifest);
						foreach (Expander expander in editor.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
						editor.UpdateLayout();
						ScrollMemoryWindowToEnd(editor);
						await CaptureAdditionalMemoryFrame(editor, $"detail-provenance-{language}-{width}x{height}", output, manifest);
						editor.Close();
						await window.PrepareShutdownAsync();
					}
					finally
					{
						window.AllowClose = true;
						window.Close();
					}
				}
			}
			return true;
		}, CancellationToken.None);
		Assert.Equal(66, manifest.Count);
		await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions {WriteIndented = true}));
	}

	private static void ScrollMemoryWindowToEnd(Window window)
	{
		ScrollViewer scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
			.Where(control => control.IsEffectivelyVisible && control.Viewport.Height > 0)
			.OrderByDescending(control => control.Extent.Height - control.Viewport.Height).First();
		scroll.Offset = new Vector(0, scroll.Extent.Height);
	}

	private static async Task CaptureAdditionalMemoryFrame(Window window, string name, string output, List<object> manifest)
	{
		await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		await Task.Delay(100);
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
		frame.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
		Assert.True(NativeSettingsSampledColors(frame) > 8);
		foreach (Button button in window.GetVisualDescendants().OfType<Button>().Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0))
		{
			Point? origin = button.TranslatePoint(default, window);
			if (origin is { } point && point.Y >= 0 && point.Y < window.ClientSize.Height)
				Assert.True(point.X >= -1 && point.X + button.Bounds.Width <= window.ClientSize.Width + 1, $"记忆操作按钮横向越界：{name}，{button.Name}，{button.Content}，x={point.X}，width={button.Bounds.Width}");
		}
		foreach (ScrollViewer scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(control => control.IsEffectivelyVisible && control.Viewport.Width > 0))
			Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 2, $"记忆弹窗横向溢出：{name}");
		Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
		manifest.Add(new {fileName = name + ".png", width = frame.PixelSize.Width, height = frame.PixelSize.Height, theme = "dark", syntheticData = true});
	}

	private static JsonElement SyntheticMemoryRecallResult() => JsonSerializer.SerializeToElement(new
	{
		trace = new
		{
			query = "一起读书", expandedQuery = "阅读 偏好 共同活动 / Reading preferences and shared activities",
			keywordHits = new[] {new {memoryId = 2, score = 0.88, rank = 1}},
			vectorHits = new[] {new {memoryId = 2, score = 0.92, rank = 1}},
			atomHits = new[] {new {memoryId = 2, score = 0.85, rank = 1}},
			rrfHits = new[] {new {memoryId = 2, score = 0.95, rank = 1}},
			filteredIds = new[] {3, 4}, injectedIds = new[] {2},
		},
		personal = new[] {new {id = 2, content = "合成注入记忆", canonicalSummary = "合成规范摘要", personaSummary = "记得和你一起读书"}},
		atoms = new[] {new {id = 2, parentMemoryId = 2, atomType = "preference", status = "active", importance = 0.8, confidence = 0.9, content = "合成事实原子", createdAt = "2026-09-01T12:00:00Z"}},
		knowledge = new[] {new {id = 1, heading = "合成知识 Synthetic knowledge", awareness = "conscious", score = 0.91, content = "仅用于验证的知识条目"}},
		echoes = new[] {new {content = "合成记忆残响", score = 0.7}},
	});

	private static void SeedNativeMemoryVisualData(BridgeCommandsTests fixture)
	{
		for (int index = 0; index < 24; index++)
		{
			MemoryItem item = fixture._services.Memory.AddAggregate("manual",
				$"合成测试记忆 {index + 1:D2}：喜欢散步、阅读与温暖的午后。Synthetic memory for visual verification. " + (index == 0 ? new string('W', 180) : ""),
				importance: 0.8, source: "manual", tags: "生活, reading", kind: MemoryKind.Preference,
				canonicalSummary: "合成规范摘要 / A synthetic canonical summary",
				personaSummary: "记得一起读书 / Let us read together", confidence: 0.85,
				ttlDays: 30, sources: [new MemorySource {Id = 0, MemoryId = 0, Role = "user", Content = "只用于验证的合成来源消息\nSynthetic source message", Sequence = 1, MessageTime = "2026-09-01T12:00:00Z"}]);
			if (index % 5 == 0) fixture._runtime.Memory.Archive(item.Id);
		}
	}
}
