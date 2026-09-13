using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Desktop.Chat;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>显式启用真实 Skia/headless 截图；未与旧 Vue 做像素差比较。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NativeChatVisualFactAttribute : FactAttribute
{
	/// <summary>默认测试不生成截图文件。</summary>
	public NativeChatVisualFactAttribute()
	{
		if (Environment.GetEnvironmentVariable("NORI_CAPTURE_CHAT") != "1") Skip = "设置 NORI_CAPTURE_CHAT=1 执行原生对话截图。";
	}
}

public partial class BridgeCommandsTests
{
	[Fact]
	public void NativeChatPaletteRetainsVuePaleAssistantAndAccessibleText()
	{
		Assert.Equal(Color.Parse("#b8d7d8"), ((ISolidColorBrush)ChatPalette.AssistantBackground).Color);
		Assert.Equal(Color.Parse("#111827"), ((ISolidColorBrush)ChatPalette.AssistantText).Color);
		Assert.Equal(Color.Parse("#454752"), ((ISolidColorBrush)ChatPalette.UserBackground).Color);
		CheckContrast(ChatPalette.AssistantText, ChatPalette.AssistantBackground);
		CheckContrast(ChatPalette.UserText, ChatPalette.UserBackground);
		CheckContrast(ChatPalette.Muted, ChatPalette.Deep);
		CheckContrast(ChatPalette.OnTeal, ChatPalette.Accent);
		static void CheckContrast(IBrush foreground, IBrush background)
		{
			static double Channel(byte value) { double channel = value / 255d; return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4); }
			static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
			double fg = Luminance(((ISolidColorBrush)foreground).Color), bg = Luminance(((ISolidColorBrush)background).Color);
			Assert.True((Math.Max(fg, bg) + 0.05) / (Math.Min(fg, bg) + 0.05) >= 4.5);
		}
	}

	[Fact]
	public Task NativeChatSharedPaletteHasNoDispatcherThreadOwnership() => WithSettingsUiAsync(async () =>
	{
		ISolidColorBrush[] brushes = [
			(ISolidColorBrush)ChatPalette.Background, (ISolidColorBrush)ChatPalette.Accent,
			(ISolidColorBrush)ChatPalette.AssistantBackground, (ISolidColorBrush)ChatPalette.AssistantText,
			(ISolidColorBrush)ChatPalette.Line, (ISolidColorBrush)ChatPalette.AssistantBorder,
			(ISolidColorBrush)ChatPalette.Overlay, (ISolidColorBrush)ChatPalette.Scrim,
		];
		Color[] expected = brushes.Select(brush => brush.Color).ToArray();
		Color[] actual = await Task.Run(() => brushes.Select(brush => brush.Color).ToArray());
		Assert.Equal(expected, actual);
	});

	[NativeChatVisualFact]
	public async Task NativeChatVisualCaptureCoversDarkConversationMarkdownStreamingErrorAndApproval()
	{
		string output = Path.Combine(Path.GetDirectoryName(NativeSettingsCaptureDirectory())!, "native-chat"); Directory.CreateDirectory(output);
		List<object> manifest = [];
		await VisualUiSession.Value.Dispatch(async () =>
		{
			using BridgeCommandsTests fixture = new(); fixture.ConfigureDesktop();
			foreach (string language in new[] { "zh-CN", "en-US" })
			foreach ((int width, int height) in new[] { (720, 480), (960, 640), (1920, 1080) })
			{
				fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language)); fixture._runtime.InvalidateSnapshot("general");
				ChatWindow window = new(fixture._services) { Width = width, Height = height };
				try
				{
					window.Show(); await RefreshChatForTest(window);
					foreach (string scene in new[] { "empty", "history", "markdown", "streaming", "error", "approval" })
					{
						NativeChatState state = window.Body.State;
						if (state.SessionId is { } previous) state.ApplyEvent(ChatEvent(new { type = "cancelled", sessionId = previous }));
						state.Clear(""); state.Draft = language == "zh-CN" ? "在这里继续和 Nori 聊聊…" : "Keep chatting with Nori here…";
						if (scene != "empty") SeedChatScene(state, scene, language);
						window.Body.FlushRender(); window.Body.ScrollToLatest();
						await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
						AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(120); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
						using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
						string file = $"{scene}-{language}-{width}x{height}.png"; frame.Save(Path.Combine(output, file), PngBitmapEncoderOptions.Default);
						Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant); Assert.True(NativeSettingsSampledColors(frame) > 8);
						foreach (ScrollViewer scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(control => control.IsEffectivelyVisible && control.Viewport.Width > 0))
							Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 2, $"原生对话横向溢出：{file}，{scroll.Name}");
						Control composer = ChatControl<Control>(window, "ChatComposeBar"); Point origin = composer.TranslatePoint(default, window)!.Value;
						Assert.True(origin.Y + composer.Bounds.Height <= window.Bounds.Height + 1, $"原生输入区被裁切：{file}");
						manifest.Add(new { file, scene, language, width, height, theme = "dark", syntheticConversation = true, vuePixelComparison = false });
					}
					if (window.Body.State.SessionId is { } final) window.Body.State.ApplyEvent(ChatEvent(new { type = "cancelled", sessionId = final }));
					await window.PrepareShutdownAsync();
				}
				finally { window.AllowClose = true; window.Close(); }
			}
			return true;
		}, CancellationToken.None);
		Assert.Equal(36, manifest.Count);
		await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
	}

	private static void SeedChatScene(NativeChatState state, string scene, string language)
	{
		bool english = language == "en-US";
		state.BeginSend(english ? "Nori, keep me company for a while." : "Nori，陪我聊一会儿吧。"); state.AttachSession("visual-session");
		string text = scene == "markdown"
			? "**Nori** · [OpenAI](https://openai.com)\n\n```csharp\nvar greeting = \"Hello, Nori!\";\n```\n\n| Item | Status |\n| --- | --- |\n| Memory | Ready |\n| Voice | Ready |"
			: english ? "Of course. I am right here.\n\nTell me about your day — or we can simply enjoy a quiet moment together." : "当然可以，我就在这里。\n\n今天过得怎么样？也可以什么都不想，和我一起安静地待一会儿。";
		state.ApplyEvent(ChatEvent(new { type = "chunk", sessionId = "visual-session", chunk = text }));
		state.ApplyEvent(ChatEvent(new { type = "usage", sessionId = "visual-session", totalTokens = 2148, completionTokens = 186, cachedTokens = 1024, cacheHitRate = 48, durationMs = 3260 }));
		if (scene == "approval")
		{
			state.ApplyEvent(ChatEvent(new { type = "approval-request", sessionId = "visual-session", requestId = "visual-approval", toolName = "write_file", description = english ? "Save the note in your workspace." : "将这段笔记保存到工作区。", deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1), permissionLevel = "confirm", arguments = new { path = "notes/today.md", content = string.Join("\n", Enumerable.Repeat(english ? "A quiet day with Nori." : "和 Nori 一起安静的一天。", 12)) } }));
		}
		else if (scene == "error") state.ApplyEvent(ChatEvent(new { type = "error", sessionId = "visual-session", error = english ? "The provider connection was interrupted. Your draft has been kept." : "提供方连接中断，已保留你的草稿。" }));
		else if (scene != "streaming") state.ApplyEvent(ChatEvent(new { type = "complete", sessionId = "visual-session", message = new { text } }));
	}
}
