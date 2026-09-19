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

	[Fact]
	public void QuickChatGrowingBubblesKeepComposerAtSameHeight()
	{
		PixelRect work = new(0, 0, 1920, 1080);
		QuickChatPlacement shortPanel = QuickChatLayout.Calculate(new(900, 600), new(280, 150), 1, work, 74);
		QuickChatPlacement tallPanel = QuickChatLayout.Calculate(new(900, 600), new(280, 150), 1, work, 300);
		Assert.Equal(shortPanel.Position.Y + shortPanel.Height, tallPanel.Position.Y + tallPanel.Height);
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
			Assert.True(window.Topmost);
			window.Topmost = false;
			Assert.True(window.Topmost);
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
			QuickChatWindow original = Assert.IsType<QuickChatWindow>(controller.Window);
			Assert.True(original.IsVisible);
			Assert.Equal(PetPresentationMode.QuickChat, fixture._services.PetRuntime.PresentationMode);
			pet.Hide(); await controller.RefreshAsync();
			Assert.False(original.IsVisible); Assert.True(controller.Enabled);
			pet.Show(); await controller.RefreshAsync();
			Assert.Same(original, controller.Window); Assert.True(original.IsVisible);
			original.Body.State.Draft = "保留草稿";
			fixture._config.Set(ConfigStore.KeyQuickChatEnabled, new ConfigValue.Boolean(false));
			await controller.RefreshAsync();
			Assert.True(pet.IsVisible); Assert.Null(controller.Window);
			Assert.Equal(PetPresentationMode.Ordinary, fixture._services.PetRuntime.PresentationMode);
			fixture._config.Set(ConfigStore.KeyQuickChatEnabled, new ConfigValue.Boolean(true));
			await controller.RefreshAsync();
			Assert.NotSame(original, controller.Window);
			Assert.Equal("保留草稿", controller.Window!.Body.State.Draft);
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
}
