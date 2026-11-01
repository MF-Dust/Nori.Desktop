using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Core.Live2D;
using Nori.Desktop.Chat;
using Nori.Desktop.QuickChat;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task QuickChatTemplatePreservesImeEnterAndEscapeBehavior() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		QuickChatWindow window = new(fixture._services);
		try
		{
			window.Show();
			await window.Body.RefreshAsync();
			window.UpdateLayout();
			var composer = window.Body.Composer;
			TextPresenter presenter = Assert.Single(composer.GetVisualDescendants().OfType<TextPresenter>());
			int sends = 0;
			composer.SendRequested += () => sends++;
			composer.Text = "草稿";
			composer.Focus();
			presenter.PreeditText = "你好";
			composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
			Assert.Equal(0, sends);
			presenter.PreeditText = "";
			composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
			Assert.Equal(1, sends);
			composer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
			Assert.False(composer.IsFocused);
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Theory]
	[InlineData(1)]
	[InlineData(1.25)]
	[InlineData(1.5)]
	public void QuickChatLayoutFollowsPetAndClampsToPhysicalWorkingArea(double scale)
	{
		PixelRect work = new(-1920, 0, 1920, 1080);
		QuickChatPlacement first = QuickChatLayout.Calculate(new(-1600, 300), new(280, 150), scale, work, 74);
		QuickChatPlacement moved = QuickChatLayout.Calculate(new(-1500, 400), new(280, 150), scale, work, 74);
		Assert.Equal(first.Position.X + 100, moved.Position.X);
		Assert.Equal(first.Position.Y + 100, moved.Position.Y);
		QuickChatPlacement edge = QuickChatLayout.Calculate(new(-10, 1070), new(280, 150), scale, work, 800);
		Assert.True(edge.Position.X >= work.X);
		Assert.True(edge.Position.Y >= work.Y);
		Assert.True(edge.Position.X + Math.Ceiling(edge.Width * scale) <= work.Right);
		Assert.True(edge.Position.Y + Math.Ceiling(edge.Height * scale) <= work.Bottom);
	}

	[Theory]
	[InlineData(14, 14, 280, 46, 1, 14, 14, 280, 46)]
	[InlineData(14, 14, 280, 46, 1.25, 17, 17, 351, 58)]
	[InlineData(14, 14, 280, 46, 1.5, 21, 21, 420, 69)]
	[InlineData(14, 14, 280, 46, 2, 28, 28, 560, 92)]
	[InlineData(0.2, 0.4, 10.4, 8.2, 1, 0, 0, 11, 9)]
	[InlineData(-0.25, -0.75, 10.5, 8.25, 1, -1, -1, 12, 9)]
	public void QuickChatPhysicalInputRegionIncludesEveryVisibleEdge(double x, double y, double width, double height,
		double scale, int expectedX, int expectedY, int expectedWidth, int expectedHeight)
	{
		Rect logical = new(x, y, width, height);
		var region = QuickChatWindow.PhysicalInputRegion(logical, scale);
		Assert.Equal((expectedX, expectedY, expectedWidth, expectedHeight), region);
		Assert.True(region.X <= logical.Left * scale);
		Assert.True(region.Y <= logical.Top * scale);
		Assert.True(region.X + region.Width >= logical.Right * scale);
		Assert.True(region.Y + region.Height >= logical.Bottom * scale);
	}

	[Fact]
	public void QuickChatGrowingBubblesStayBelowPetAndGrowDownward()
	{
		PixelRect work = new(0, 0, 1920, 1080);
		QuickChatPlacement shortPanel = QuickChatLayout.Calculate(new(900, 600), new(280, 150), 1, work, 74);
		QuickChatPlacement tallPanel = QuickChatLayout.Calculate(new(900, 600), new(280, 150), 1, work, 300);
		Assert.Equal(shortPanel.Position.Y, tallPanel.Position.Y);
		Assert.True(shortPanel.Position.Y + QuickChatLayout.ShadowMargin > 750);
	}

	[Fact]
	public Task QuickChatAlwaysTopmostAndDoesNotActivateOnShow() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(); fixture.ConfigureDesktop();
		QuickChatWindow window = new(fixture._services);
		try
		{
			window.Show();
			Assert.False(window.ShowActivated); Assert.False(window.ShowInTaskbar);
			Assert.Equal(WindowDecorations.None, window.WindowDecorations);
			Assert.Equal(Nori.Core.Platform.PlatformServices.Current.Capabilities.SupportsTopmost, window.Topmost);
			window.Topmost = false;
			Assert.Equal(Nori.Core.Platform.PlatformServices.Current.Capabilities.SupportsTopmost, window.Topmost);
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task QuickChatToggleAndPetVisibilityKeepSeparateLifetimes() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(); fixture.ConfigureDesktop();
		PetWindow pet = new(WindowDefinition.All.Single(item => item.Label == WindowLabels.Pet), fixture._services);
		QuickChatController controller = new(fixture._services, pet, _ => { }, _ => { });
		try
		{
			pet.Show(); await controller.RefreshAsync();
			QuickChatView original = Assert.IsType<QuickChatView>(controller.Body);
			Assert.True(original.IsEffectivelyVisible);
			Assert.Equal(PetPresentationMode.QuickChat, fixture._services.PetRuntime.PresentationMode);
			pet.Hide(); await controller.RefreshAsync();
			Assert.False(original.IsEffectivelyVisible); Assert.True(controller.Enabled);
			pet.Show(); await controller.RefreshAsync();
			Assert.Same(original, controller.Body); Assert.True(original.IsEffectivelyVisible);
			original.State.Draft = "保留草稿";
			fixture._config.Set(ConfigStore.KeyQuickChatEnabled, new ConfigValue.Boolean(false));
			await controller.RefreshAsync();
			Assert.True(pet.IsVisible); Assert.Null(controller.Body);
			Assert.Equal(PetPresentationMode.Ordinary, fixture._services.PetRuntime.PresentationMode);
			fixture._config.Set(ConfigStore.KeyQuickChatEnabled, new ConfigValue.Boolean(true));
			await controller.RefreshAsync();
			Assert.NotSame(original, controller.Body);
			Assert.Equal("保留草稿", controller.Body!.State.Draft);
		}
		finally { await controller.ShutdownAsync(); pet.AllowClose = true; pet.Close(); }
	});

	[Fact]
	public Task 宿主窗口关闭不会提前释放快捷聊天表面() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		Window owner = new();
		QuickChatSurface surface = new(fixture._services, owner);
		try
		{
			owner.Show();
			owner.Close();
			// 生命周期由创建者统一收尾：宿主先关闭时，表面仍须能刷新并走完正常收尾。
			fixture._config.Set("language", new ConfigValue.Text("en-US"));
			fixture._runtime.InvalidateSnapshot();
			await surface.Body.RefreshAsync();
			// 显示宿主时排队的刷新可能取代这一次，等新快照落地即可；旧实现在关闭时已释放表面，永远等不到。
			await WaitUntilAsync(() => surface.Body.State.Language == "en-US");
			Assert.Equal("", surface.Body.State.Chat.Error);
		}
		finally { await surface.PrepareShutdownAsync(); }
	});

	// 布局按控制器参数而不是测试机会话决定，CI 也能覆盖原生 Wayland 的内嵌布局。
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public Task QuickChatSurfaceFollowsPetInEmbeddedAndFloatingModes(bool embedInPet) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true); fixture.ConfigureDesktop();
		PetWindow pet = new(WindowDefinition.All.Single(item => item.Label == WindowLabels.Pet), fixture._services);
		int registered = 0;
		QuickChatController controller = new(fixture._services, pet, _ => registered++, _ => registered--, embedInPet);
		try
		{
			pet.Show(); await controller.RefreshAsync();
			await controller.Body!.RefreshAsync();
			controller.Reposition(); pet.UpdateLayout();
			await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
			pet.UpdateLayout();
			Assert.Equal(WindowDecorations.None, pet.WindowDecorations);
			if (embedInPet)
			{
				Assert.Null(controller.Window);
				Assert.Equal(0, registered);
				Assert.Same(pet, TopLevel.GetTopLevel(controller.Body));
				Assert.Equal(pet.QuickChatLayout.ViewportWidth, pet.ModelViewportSize.Width, 1);
				Assert.Equal(pet.QuickChatLayout.ViewportHeight, pet.ModelViewportSize.Height, 1);
				Assert.True(pet.Height > pet.ModelViewportSize.Height);
				var composer = controller.Body.Composer.TranslatePoint(default, pet);
				Assert.NotNull(composer);
				Assert.True(composer.Value.Y >= pet.ModelViewportSize.Height);
				Assert.True(controller.FocusComposer());
				var oldViewport = pet.ModelViewportSize;
				pet.Position = new(160, 120); pet.UpdateLayout();
				Assert.Equal(oldViewport, pet.ModelViewportSize);
				Assert.Same(pet, TopLevel.GetTopLevel(controller.Body));
				CaptureUi(pet, "wayland-unified-quick-chat");
			}
			else
			{
				Assert.Equal(1, registered);
				Assert.Same(controller.Window, TopLevel.GetTopLevel(controller.Body));
			}
			fixture._config.Set(ConfigStore.KeyQuickChatEnabled, new ConfigValue.Boolean(false));
			await controller.RefreshAsync(); pet.UpdateLayout();
			Assert.Null(controller.Body);
			Assert.True(pet.ModelViewportSize.Width > 0);
			Assert.Equal(pet.Bounds.Width, pet.ModelViewportSize.Width, 1);
		}
		finally { await controller.ShutdownAsync(); pet.AllowClose = true; pet.Close(); }
	});

	[Fact]
	public void FullChatMergesQuickChatHistoryWithoutLosingDraftOrOlderPage()
	{
		NativeChatState state = new() { Draft = "未发送" };
		state.AcceptHistory(state.BeginHistory(), ChatEvent(new[] { new { id = 1, role = "user", content = "较早记录" } }), 50);
		var page = ChatEvent(new[] { new { id = 2, role = "user", content = "快捷输入" }, new { id = 3, role = "assistant", content = "快捷回复" } });
		state.MergeLatestHistory(page); state.MergeLatestHistory(page);
		Assert.Equal(3, state.Messages.Count); Assert.Equal("未发送", state.Draft);
		Assert.Equal("较早记录", state.Messages[0].Content);
	}

	[Fact]
	public void FullChatLatestPageMergeKeepsChronologyWhenItIncludesOlderRows()
	{
		NativeChatState state = new();
		state.AcceptHistory(state.BeginHistory(), ChatEvent(new[] { new { id = 50, role = "user", content = "原本最新页" } }), 50);
		state.MergeLatestHistory(ChatEvent(new[]
		{
			new { id = 1, role = "user", content = "扩大页覆盖的旧消息" },
			new { id = 50, role = "user", content = "原本最新页" },
			new { id = 51, role = "assistant", content = "快捷新回复" },
		}));
		Assert.Equal(new[] { "1", "50", "51" }, state.Messages.Select(message => message.Key));
	}

	private static JsonElement ChatEvent(object? value) => JsonSerializer.SerializeToElement(value);
}
