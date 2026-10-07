using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Nori.Core.Platform;
using Nori.Desktop.Chat;
using Nori.Desktop.QuickChat;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData(720, 480)]
	[InlineData(1920, 1080)]
	public Task QuickChatDragPressurePreservesLayoutAndTransformedInputRegion(int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		QuickChatWindow window = new(fixture._services);
		try
		{
			QuickChatPlacement placement = QuickChatLayout.Calculate(new(width / 2, height / 2), new(280, 150),
				1, new PixelRect(0, 0, width, height), 74);
			window.Width = placement.Width;
			window.Height = placement.Height;
			window.Position = placement.Position;
			window.Show();
			await window.Body.RefreshAsync();
			window.FocusManager?.Focus(null);
			FinishQuickChatMotion(window.Body);
			window.UpdateLayout();
			Size size = window.ClientSize;
			Rect normal = Assert.Single(window.InteractiveRects());
			CaptureUi(window, $"quick-chat-drag-{width}x{height}-idle");

			window.Body.SetDragPressed(true);
			FinishQuickChatMotion(window.Body);
			// 原生窗口遵循平台减少动效偏好；具体压感插值由下方注入时钟的测试覆盖。
			double expectedScale = PlatformServices.Current.PrefersReducedMotion ? 1 : 0.985;
			Assert.Equal(expectedScale, Assert.IsType<ScaleTransform>(window.Body.RenderTransform).ScaleX, 6);
			Rect pressed = Assert.Single(window.InteractiveRects());
			Assert.Equal(normal.Width * expectedScale, pressed.Width, 5);
			Assert.Equal(normal.Height * expectedScale, pressed.Height, 5);
			Assert.Equal(size, window.ClientSize);
			Assert.Equal(placement.Position, window.Position);
			CaptureUi(window, $"quick-chat-drag-{width}x{height}-pressed");

			window.Body.SetDragPressed(false);
			FinishQuickChatMotion(window.Body);
			Assert.Equal(1, Assert.IsType<ScaleTransform>(window.Body.RenderTransform).ScaleX);
			Assert.Equal(normal, Assert.Single(window.InteractiveRects()));
			CaptureUi(window, $"quick-chat-drag-{width}x{height}-released");
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task QuickChatDragPressureRespectsReducedMotionAndClearsOnHideOrDispose() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		Window host = new();
		using NativeChatService service = new(fixture._services, host, NativeChatSurface.QuickChat);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		bool reduced = false;
		using QuickChatView view = new(service, (_, _, _) => Task.FromResult(default(JsonElement)), () => now, () => reduced);
		ScaleTransform scale = Assert.IsType<ScaleTransform>(view.RenderTransform);
		view.SetDragPressed(true);
		now += TimeSpan.FromMilliseconds(50);
		UpdateQuickChatMotion(view, now);
		Assert.InRange(scale.ScaleX, 0.985, 0.9999);
		view.SetDragPressed(false);
		Assert.InRange(scale.ScaleX, 0.985, 0.9999);
		now += TimeSpan.FromMilliseconds(180);
		UpdateQuickChatMotion(view, now);
		Assert.Equal(1, scale.ScaleX);

		view.SetDragPressed(true);
		now += TimeSpan.FromMilliseconds(100);
		UpdateQuickChatMotion(view, now);
		Assert.Equal(0.985, scale.ScaleX, 6);
		reduced = true;
		UpdateQuickChatMotion(view, now);
		Assert.Equal(1, scale.ScaleX);
		view.SetDragPressed(false);
		view.SetDragPressed(true);
		Assert.Equal(1, scale.ScaleX);

		reduced = false;
		view.SetHostVisible(false);
		view.SetDragPressed(true);
		now += TimeSpan.FromMilliseconds(100);
		UpdateQuickChatMotion(view, now);
		Assert.Equal(0.985, scale.ScaleX, 6);
		view.SetHostVisible(false);
		Assert.Equal(1, scale.ScaleX);
		view.SetDragPressed(true);
		now += TimeSpan.FromMilliseconds(100);
		UpdateQuickChatMotion(view, now);
		view.Dispose();
		Assert.Equal(1, scale.ScaleX);
		return Task.CompletedTask;
	});

	[Fact]
	public Task QuickChatDragPressureFollowsPetStateAndHide() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture.ConfigureDesktop();
		PetWindow pet = new(WindowDefinition.All.Single(item => item.Label == WindowLabels.Pet), fixture._services);
		QuickChatController controller = new(fixture._services, pet, _ => { }, _ => { });
		try
		{
			pet.Show();
			await controller.RefreshAsync();
			QuickChatWindow chat = Assert.IsType<QuickChatWindow>(controller.Window);
			// 安全模式没有模型命中区域；直接驱动拖动状态，验证控制器订阅和收尾。
			SetPetDragPressure(pet, true);
			Assert.True(pet.IsDragPressed);
			FinishQuickChatMotion(chat.Body);
			double expectedScale = PlatformServices.Current.PrefersReducedMotion ? 1 : 0.985;
			Assert.Equal(expectedScale, Assert.IsType<ScaleTransform>(chat.Body.RenderTransform).ScaleX, 6);
			typeof(PetWindow).GetMethod("FinishDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pet, null);
			Assert.False(pet.IsDragPressed);
			FinishQuickChatMotion(chat.Body);
			Assert.Equal(1, Assert.IsType<ScaleTransform>(chat.Body.RenderTransform).ScaleX);

			SetPetDragPressure(pet, true);
			Assert.True(pet.IsDragPressed);
			pet.Hide();
			Assert.False(pet.IsDragPressed);
			Assert.False(chat.IsVisible);
			Assert.Equal(1, Assert.IsType<ScaleTransform>(chat.Body.RenderTransform).ScaleX);
		}
		finally { await controller.ShutdownAsync(); pet.AllowClose = true; pet.Close(); }
	});

	private static void SetPetDragPressure(PetWindow pet, bool pressed) => typeof(PetWindow)
		.GetMethod("SetDragPressed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pet, [pressed]);

	private static void FinishQuickChatMotion(QuickChatView view) => UpdateQuickChatMotion(view, DateTimeOffset.UtcNow.AddSeconds(1));
	private static void UpdateQuickChatMotion(QuickChatView view, DateTimeOffset now) => typeof(QuickChatView)
		.GetMethod("UpdateMotion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [now]);
}
