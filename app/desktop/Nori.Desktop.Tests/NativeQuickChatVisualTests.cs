using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Desktop.Bridge;
using Nori.Desktop.Chat;
using Nori.Desktop.QuickChat;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>Quick Chat 视觉截图开关，兼容现有截图步骤的环境变量。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NativeQuickChatScreenshotFactAttribute : FactAttribute
{
	public NativeQuickChatScreenshotFactAttribute()
	{
		if (Environment.GetEnvironmentVariable("NORI_CAPTURE_CHAT") != "1"
			&& Environment.GetEnvironmentVariable("NORI_NATIVE_CHAT_SCREENSHOTS") != "1")
		{
			Skip = "设置 NORI_CAPTURE_CHAT=1 或 NORI_NATIVE_CHAT_SCREENSHOTS=1 执行 Quick Chat 原生截图。";
		}
	}
}

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativeQuickChatShowsExactShortcutStatusAndLayeredSkin() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new();
		fixture.ConfigureDesktop();
		QuickChatWindow window = new(fixture._services);
		try
		{
			window.Show();
			await window.Body.RefreshAsync();
			await WaitUntilAsync(() => window.Body.HistoryLoaded);
			ApplyQuickChatVisualSnapshot(window.Body, "zh-CN");
			window.UpdateLayout();
			TextBlock placeholder = window.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "QuickChatPlaceholder");
			Border shortcut = window.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "QuickChatShortcutHint");
			Border composer = window.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "QuickChatComposeBar");
			Assert.Equal("和 Nori 聊天...", placeholder.Text);
			Assert.True(shortcut.IsVisible);
			Assert.Equal(4, composer.BoxShadow.Count);
			Assert.Equal(WindowTransparencyLevel.Transparent, Assert.Single(window.TransparencyLevelHint));
			Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color.A);

			DateTimeOffset now = DateTimeOffset.UtcNow;
			window.Body.State.BeginSend("测试状态", now);
			TextBlock activity = window.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Text == "正在连接...");
			Assert.True(activity.IsVisible);
			window.Body.State.AcceptSession("visual-status");
			Assert.Equal("Nori 正在回复...", activity.Text);
			window.Body.State.ApplyEvent(JsonSerializer.SerializeToElement(new { type = "chunk", sessionId = "visual-status", chunk = "分层阴影" }), now);
			Border bubble = window.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "QuickChatAgentBubble");
			Assert.Equal(4, bubble.BoxShadow.Count);
			TextBlock message = Assert.IsType<TextBlock>(bubble.Child);
			Assert.Equal(Color.Parse("#45454F"), Assert.IsAssignableFrom<ISolidColorBrush>(message.Foreground).Color);
			Assert.Equal(14, message.FontSize);
		}
		finally
		{
			window.AllowClose = true;
			window.Close();
		}
	});

	[NativeQuickChatScreenshotFact]
	public async Task NativeQuickChatVisualCaptureCoversTenScenesTranslationsAndWidthExtremes()
	{
		string output = Path.Combine(Path.GetDirectoryName(NativeSettingsCaptureDirectory())!, "native-quick-chat");
		Directory.CreateDirectory(output);
		int frameCount = 0;
		string[] scenes = ["empty", "player", "agent", "streaming", "error", "approval", "approval-details", "focus", "whitespace", "overflow"];
		await VisualUiSession.Value.Dispatch(async () =>
		{
			using BridgeCommandsTests fixture = new();
			fixture.ConfigureDesktop();
			foreach (string language in new[] { "zh-CN", "en-US" })
			foreach ((int width, int height) in new[] { (228, 260), (308, 520) })
			foreach (string scene in scenes)
			{
				fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
				fixture._runtime.InvalidateSnapshot("general");
				QuickChatWindow window = new(fixture._services) { Width = width, Height = height };
				try
				{
					window.Show();
					await window.Body.RefreshAsync();
					await WaitUntilAsync(() => window.Body.HistoryLoaded);
					ApplyQuickChatVisualSnapshot(window.Body, language);
					SeedQuickChatScene(window.Body, scene, language);
					window.UpdateLayout();
					await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();
					await Task.Delay(120);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();
					using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
					string file = $"{scene}-{language}-{width}x{height}.png";
					frame.Save(Path.Combine(output, file), PngBitmapEncoderOptions.Default);
					Assert.True(NativeSettingsSampledColors(frame) > 5, $"QuickChat 未真实绘制：{file}");
					Assert.InRange(window.Body.Bounds.Width, 199, 281);
					Control composer = window.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "QuickChatComposeBar");
					Assert.InRange(composer.Bounds.Width, 199, 281);
					Assert.InRange(composer.Bounds.Height, 45.5, 46.5);
					foreach (ScrollViewer scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(control => control.Name != "PART_ScrollViewer" && control.IsEffectivelyVisible && control.Viewport.Width > 0))
						Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 2, $"QuickChat 横向溢出：{file} · {scroll.Name}");
					frameCount++;
				}
				finally
				{
					window.AllowClose = true;
					window.Close();
				}
			}
			return true;
		}, CancellationToken.None);
		Assert.Equal(40, frameCount);
	}

	[NativeQuickChatScreenshotFact]
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2681", Justification = "两个视口枚举十个固定场景，组成受控截图矩阵。")]
	public async Task NativeQuickChatVisualCaptureCoversNamedViewportScenes()
	{
		string output = Path.Combine(Path.GetDirectoryName(NativeSettingsCaptureDirectory())!, "native-quick-chat");
		Directory.CreateDirectory(output);
		string[] scenes =
		[
			"quick-chat-empty", "quick-chat-focused", "quick-chat-draft", "quick-chat-player-bubble", "quick-chat-nori-bubble",
			"quick-chat-three-bubbles", "quick-chat-streaming", "quick-chat-error", "quick-chat-approval", "quick-chat-disabled",
		];
		(int Width, int Height)[] viewports = [(720, 480), (1920, 1080)];
		int frameCount = 0;

		await VisualUiSession.Value.Dispatch(async () =>
		{
			using BridgeCommandsTests fixture = new();
			fixture.ConfigureDesktop();
			fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("zh-CN"));
			fixture._runtime.InvalidateSnapshot("general");

			foreach ((int width, int height) in viewports)
			foreach (string scene in scenes)
			{
				await CaptureQuickChatViewportSceneAsync(fixture, output, scene, width, height);
				frameCount++;
			}
			return true;
		}, CancellationToken.None);

		Assert.Equal(20, frameCount);
	}

	private static async Task CaptureQuickChatViewportSceneAsync(
		BridgeCommandsTests fixture,
		string output,
		string scene,
		int width,
		int height)
	{
		Window window = new();
		using NativeChatService service = new(fixture._services, window, NativeChatSurface.QuickChat);
		using QuickChatView view = new(service, reduceMotion: () => true) { Width = 280 };
		Canvas canvas = new()
		{
			Width = width,
			Height = height,
			Background = new SolidColorBrush(Color.Parse("#24242C")),
		};
		canvas.Children.Add(view);
		window.Width = width;
		window.Height = height;
		window.Background = Brushes.Transparent;
		window.Content = canvas;
		try
		{
			window.Show();
			await view.RefreshAsync();
			await WaitUntilAsync(() => view.HistoryLoaded);
			GlyphTypeface conversationGlyphs = new Typeface(view.FontFamily, weight: FontWeight.Medium).GlyphTypeface;
			Assert.NotEqual((ushort)0, conversationGlyphs.CharacterToGlyphMap.GetGlyph('N'));
			view.ApplySnapshot(JsonSerializer.SerializeToElement(new
			{
				app = new {safeMode = false},
				chat = new {configured = true},
				general = new {language = "zh-CN"},
			}, BridgeJson.Options));
			SeedNamedQuickChatScene(view, scene);
			if (scene == "quick-chat-focused") Assert.Null(view.Composer.GetValue(AdornerLayer.AdornerProperty));
			view.SetHostVisible(true);
			window.UpdateLayout();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Canvas.SetLeft(view, Math.Max(24, width - 280 - 32));
			Canvas.SetTop(view, Math.Max(24, height - view.DesiredSize.Height - 32));
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			if (scene == "quick-chat-focused")
			{
				Control focusedComposer = view.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "QuickChatComposeBar");
				foreach (Control ancestor in focusedComposer.GetVisualAncestors().OfType<Control>().Where(control => control.ClipToBounds))
				{
					Rect visualBounds = new Rect(focusedComposer.Bounds.Size).TransformToAABB(focusedComposer.TransformToVisual(ancestor)!.Value);
					Assert.True(new Rect(ancestor.Bounds.Size).Contains(visualBounds), $"聚焦输入栏被裁切：{ancestor.GetType().Name} {ancestor.Name}，{visualBounds} / {ancestor.Bounds.Size}");
				}
			}
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			await Task.Delay(120);
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();

			Assert.Equal(280, Math.Round(view.Bounds.Width));
			Assert.InRange(Canvas.GetLeft(view), 24, width - 304);
			Assert.InRange(Canvas.GetTop(view), 24, height - 24);
			Control composer = view.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "QuickChatComposeBar");
			Assert.InRange(composer.Bounds.Width, 279, 281);
			using WriteableBitmap frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
			if (scene == "quick-chat-disabled")
				Assert.False(view.IsEffectivelyVisible);
			else
			{
				Assert.True(view.IsEffectivelyVisible);
				Assert.True(NativeSettingsSampledColors(frame) > 5, scene);
			}

			frame.Save(Path.Combine(output, $"{scene}-{width}x{height}.png"), PngBitmapEncoderOptions.Default);
		}
		finally
		{
			view.SetHostVisible(false);
			window.Close();
		}
	}

	private static void SeedNamedQuickChatScene(QuickChatView view, string scene)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		switch (scene)
		{
			case "quick-chat-focused":
				view.Composer.Text = "正在输入的草稿";
				view.FocusComposer();
				break;
			case "quick-chat-draft":
				view.Composer.Text = "这是一条尚未发送的快捷聊天草稿";
				break;
			case "quick-chat-player-bubble":
				AddQuickChatMessage(view, "user", "请把这条消息显示在桌面上", now, "player");
				break;
			case "quick-chat-nori-bubble":
				AddQuickChatMessage(view, "assistant", "当然可以，我会把回复显示在这里。", now, "nori");
				break;
			case "quick-chat-three-bubbles":
				AddQuickChatMessage(view, "user", "第一条消息", now.AddSeconds(-3), "one");
				AddQuickChatMessage(view, "assistant", "第二条消息", now.AddSeconds(-2), "two");
				AddQuickChatMessage(view, "user", "第三条消息", now.AddSeconds(-1), "three");
				break;
			case "quick-chat-streaming":
				view.State.BeginSend("流式请求", now);
				view.State.AcceptSession("streaming");
				view.State.ApplyEvent(JsonSerializer.SerializeToElement(new {type = "chunk", sessionId = "streaming", chunk = "正在逐字生成"}), now);
				break;
			case "quick-chat-error":
				view.State.Chat.SetError("连接服务失败，请稍后重试");
				break;
			case "quick-chat-approval":
				view.State.BeginSend("需要确认的操作", now);
				view.State.AcceptSession("approval");
				view.State.ApplyEvent(JsonSerializer.SerializeToElement(new
				{
					type = "approval-request",
					sessionId = "approval",
					requestId = "quick-visual-approval",
					toolName = "write_file",
					permissionLevel = "confirm",
					description = "将一段简短笔记保存到工作区。",
					deadlineUtc = now.AddMinutes(1),
					arguments = new {path = "notes/today.md", content = "快捷聊天截图"},
				}), now);
				break;
			case "quick-chat-disabled":
				view.IsVisible = false;
				break;
		}
	}

	private static void AddQuickChatMessage(QuickChatView view, string role, string content, DateTimeOffset receivedAt, string key)
	{
		view.State.Chat.Messages.Add(new NativeChatMessage(key, role, content));
		view.State.Synchronize(receivedAt, silentNewMessages: false);
	}

	private static void ApplyQuickChatVisualSnapshot(QuickChatView view, string language) =>
		view.ApplySnapshot(JsonSerializer.SerializeToElement(new
		{
			app = new { safeMode = false },
			chat = new { configured = true },
			general = new { language },
		}, BridgeJson.Options));

	private static void SeedQuickChatScene(QuickChatView view, string scene, string language)
	{
		bool english = language == "en-US";
		DateTimeOffset now = DateTimeOffset.UtcNow;
		if (scene == "empty") return;
		if (scene == "whitespace")
		{
			view.Composer.Text = "   ";
			return;
		}
		if (scene == "focus")
		{
			view.Composer.Text = english ? "A focused draft" : "正在输入的草稿";
			view.FocusComposer();
			return;
		}
		string prompt = scene == "overflow"
			? string.Concat(Enumerable.Repeat("VeryLongUnbrokenQuickChatContent", 12))
			: english ? "Stay with me for a moment." : "陪我安静地待一会儿。";
		view.State.BeginSend(prompt, now);
		view.State.AcceptSession("visual-session");
		if (scene == "player") return;
		string answer = scene == "overflow"
			? string.Concat(Enumerable.Repeat("OverflowShouldWrapWithoutGrowingTheWindow", 16))
			: english ? "Of course. I am right here." : "当然可以，我就在这里。";
		view.State.ApplyEvent(JsonSerializer.SerializeToElement(new { type = "chunk", sessionId = "visual-session", chunk = answer }), now);
		if (scene is "approval" or "approval-details")
		{
			view.State.ApplyEvent(JsonSerializer.SerializeToElement(new
			{
				type = "approval-request",
				sessionId = "visual-session",
				requestId = "visual-approval",
				toolName = "write_file",
				description = english ? "Save a short note in the workspace." : "将一段简短笔记保存到工作区。",
				deadlineUtc = now.AddMinutes(1),
				arguments = new { path = "notes/today.md", content = answer },
			}), now);
			if (scene == "approval-details")
			{
				Button details = view.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "QuickChatApprovalDetails");
				details.Command?.Execute(details.CommandParameter);
				details.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
			}
			return;
		}
		if (scene == "streaming") return;
		if (scene == "error")
		{
			view.State.ApplyEvent(JsonSerializer.SerializeToElement(new { type = "error", sessionId = "visual-session", error = english ? "Provider connection interrupted." : "提供方连接中断。" }), now);
			return;
		}
		view.State.ApplyEvent(JsonSerializer.SerializeToElement(new { type = "complete", sessionId = "visual-session", message = new { text = answer } }), now);
	}
}
