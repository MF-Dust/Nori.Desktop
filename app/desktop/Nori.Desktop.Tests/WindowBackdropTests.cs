using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Desktop.Appearance;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task BackgroundBlurPersistsAndNotifiesWindowsThroughGeneralSettings() => WithSettingsUiAsync(async () =>
	{
		var commands = CreateCommands();
		JsonElement initial = JsonSerializer.SerializeToElement(_runtime.BuildSnapshot());
		Assert.True(initial.GetProperty("general").GetProperty("backgroundBlurEnabled").GetBoolean());
		using SettingsService service = new(_services, new Window());
		await service.UpdateGeneralAsync(new SettingsGeneralPatchDto { BackgroundBlurEnabled = false });
		await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
		Assert.False(_config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, true));
		Assert.False(JsonSerializer.SerializeToElement(_runtime.BuildSnapshot()).GetProperty("general").GetProperty("backgroundBlurEnabled").GetBoolean());
		Assert.Contains(false, Assert.IsType<FakeWindowManager>(_services.Windows).BackgroundBlurChanges);
		await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_general", Args(new { backgroundBlurEnabled = true }));
		await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
		Assert.True(_config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, false));
		Assert.True(Assert.IsType<FakeWindowManager>(_services.Windows).BackgroundBlurChanges.Last());
	});

	[Fact]
	public Task BackgroundBlurFieldSavesAndIgnoresStaleSnapshotDuringEdit() => WithSettingsUiAsync(async () =>
	{
		using SettingsService service = new(_services, new Window());
		using PersonalizationSettingsPage page = new(service);
		SettingsFieldViewModel field = page.Sections.SelectMany(section => section.Fields).Single(item => item.Key == "backgroundBlurEnabled");
		Assert.True(field.Boolean);
		field.Boolean = false;
		page.ApplySnapshot(JsonSerializer.SerializeToElement(new { general = new { backgroundBlurEnabled = true } }));
		Assert.False(field.Boolean);
		Assert.True(await page.FlushPendingSavesAsync());
		Assert.False(_config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, true));
	});

	[Fact]
	public Task BackgroundBlurUpdatesOwnedAndNewWindowsAndUnsubscribesOnClose() => WithSettingsUiAsync(() =>
	{
		using WindowBackdropController controller = new();
		Window owner = new();
		Window child = new();
		Window later = new();
		try
		{
			controller.Register(owner);
			owner.Show();
			child.Show(owner);
			Assert.Contains(WindowTransparencyLevel.AcrylicBlur, child.TransparencyLevelHint);
			controller.SetEnabled(false);
			Assert.Equal([WindowTransparencyLevel.Transparent], owner.TransparencyLevelHint);
			Assert.Equal([WindowTransparencyLevel.Transparent], child.TransparencyLevelHint);
			Assert.Equal(NoriThemeTokens.Color("bg-base"), Assert.IsAssignableFrom<ISolidColorBrush>(owner.Background).Color);
			controller.Register(later);
			Assert.Equal([WindowTransparencyLevel.Transparent], later.TransparencyLevelHint);
			child.Close();
			controller.SetEnabled(true);
			Assert.Contains(WindowTransparencyLevel.AcrylicBlur, owner.TransparencyLevelHint);
			Assert.Contains(WindowTransparencyLevel.Blur, later.TransparencyLevelHint);
			Assert.Equal([WindowTransparencyLevel.Transparent], child.TransparencyLevelHint);
		}
		finally { child.Close(); later.Close(); owner.Close(); }
		return Task.CompletedTask;
	});

	[Fact]
	public Task 背景材质失败不会阻止窗口打开() => WithSettingsUiAsync(() =>
	{
		WindowBackdropController.EligibilityOverride = _ => throw new FileNotFoundException();
		using WindowBackdropController controller = new();
		Window window = new();
		try
		{
			controller.Register(window);
			window.Show();
			Assert.True(window.IsVisible);
			Assert.NotNull(window.PlatformImpl);
		}
		finally
		{
			WindowBackdropController.EligibilityOverride = null;
			window.Close();
		}
		return Task.CompletedTask;
	});

	[Fact]
	public Task 打开后材质失败不会打断窗口() => WithSettingsUiAsync(() =>
	{
		int failures = 0;
		using WindowBackdropController controller = new(onFailure: _ => failures++);
		Window window = new();
		try
		{
			controller.Register(window);
			WindowBackdropController.WaylandBlurOverride = (_, _) => throw new InvalidOperationException("材质失败");
			window.Show();
			window.TransparencyLevelHint = [WindowTransparencyLevel.None];
			Assert.True(window.IsVisible);
			Assert.NotNull(window.PlatformImpl);
			Assert.True(failures > 0);
		}
		finally
		{
			WindowBackdropController.WaylandBlurOverride = null;
			window.Close();
		}
		return Task.CompletedTask;
	});

	[Fact]
	public Task BackgroundBlurUnsupportedBackendPreservesEnabledPreference() => WithSettingsUiAsync(async () =>
	{
		_config.Set(ConfigStore.KeyBackgroundBlurEnabled, new ConfigValue.Boolean(true));
		using WindowBackdropController controller = new();
		Window window = new();
		try
		{
			await controller.InitializeAsync(() => _config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, true));
			controller.Register(window);
			window.Show();
			Assert.False(WindowBackdropController.IsBlurActive(true, window.ActualTransparencyLevel));
			Assert.Equal(NoriThemeTokens.Color("bg-base"), Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);
			Assert.True(_config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, true));
			Assert.True(WindowBackdropController.IsBlurActive(true, WindowTransparencyLevel.Blur));
			Assert.True(WindowBackdropController.IsBlurActive(true, WindowTransparencyLevel.AcrylicBlur));
			Assert.False(WindowBackdropController.IsBlurActive(false, WindowTransparencyLevel.AcrylicBlur));
		}
		finally { window.Close(); }
	});

	[Fact]
	public Task Wayland模糊探测结果决定玻璃面且不改偏好() => WithSettingsUiAsync(() =>
	{
		_config.Set(ConfigStore.KeyBackgroundBlurEnabled, new ConfigValue.Boolean(true));
		WindowBackdropController.WaylandBlurOverride = (_, enabled) => enabled;
		using WindowBackdropController enabled = new();
		Window glass = new();
		Window solid = new();
		try
		{
			enabled.Register(glass);
			glass.Show();
			Assert.Equal(NoriThemeTokens.Color("bg-glass"), Assert.IsAssignableFrom<ISolidColorBrush>(glass.Background).Color);
			Assert.True(_config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, false));
			WindowBackdropController.WaylandBlurOverride = (_, _) => false;
			using WindowBackdropController disabled = new();
			disabled.Register(solid);
			solid.Show();
			Assert.Equal(NoriThemeTokens.Color("bg-base"), Assert.IsAssignableFrom<ISolidColorBrush>(solid.Background).Color);
			Assert.True(_config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, false));
		}
		finally
		{
			WindowBackdropController.WaylandBlurOverride = null;
			glass.Close();
			solid.Close();
		}
		return Task.CompletedTask;
	});

	[Fact]
	public Task BackgroundBlurStartupReadCannotOverwriteNewUserChoice() => WithSettingsUiAsync(async () =>
	{
		using WindowBackdropController controller = new(false);
		using ManualResetEventSlim release = new();
		Window window = new();
		controller.Register(window);
		Task initialization = controller.InitializeAsync(() => { release.Wait(); return false; });
		try
		{
			controller.SetEnabled(true);
			release.Set();
			await initialization;
			Assert.Contains(WindowTransparencyLevel.AcrylicBlur, window.TransparencyLevelHint);
		}
		finally { release.Set(); window.Close(); }
	});
}
