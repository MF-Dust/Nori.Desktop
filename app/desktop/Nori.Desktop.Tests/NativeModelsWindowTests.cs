using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Nori.Desktop.Settings;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Core.Live2D;
using Nori.Core.Resources;
using Nori.Desktop.Memory;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativeModelsLibraryHasTwoModelsLocalImportsAndAllTenBehaviorSaves() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(); fixture.ConfigureDesktop(); SeedNativeModels(fixture);
		ModelsWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshModelsForTest(window);
			Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
			Assert.Equal(2, window.GetVisualDescendants().OfType<Border>().Count(control => control.Name?.StartsWith("ModelsCard_", StringComparison.Ordinal) == true));
			Assert.NotNull(ModelControl<Button>(window, "ModelsImportZip")); Assert.NotNull(ModelControl<Button>(window, "ModelsImportFolder"));
			Assert.False(ModelControl<Button>(window, "ModelsEnable_nori").IsEnabled);
			Assert.True(ModelControl<Button>(window, "ModelsAdjust_arg-nori").IsEnabled);
			window.Navigate("behaviors"); window.UpdateLayout();
			ToggleSwitch[] switches = window.GetVisualDescendants().OfType<ToggleSwitch>().Where(control => control.Name?.StartsWith("ModelsBehavior_", StringComparison.Ordinal) == true).ToArray();
			Assert.Equal(10, switches.Length);
			var expected = new Dictionary<string, bool>();
			foreach (ToggleSwitch toggle in switches)
			{
				if (!toggle.IsEnabled) continue;
				string key = toggle.Name!["ModelsBehavior_".Length..]; expected[key] = toggle.IsChecked != true;
				toggle.IsChecked = expected[key];
				MemorySettingDraft draft = ModelDrafts(window)["behavior:" + key];
				Assert.True(draft.Saving || !draft.Dirty);
			}
			Assert.True(await window.FlushPendingSavesAsync());
			JsonElement snapshot = JsonSerializer.SerializeToElement(fixture._runtime.BuildSnapshot());
			foreach (var pair in expected) Assert.Equal(pair.Value, snapshot.GetProperty("behaviors").GetProperty(pair.Key).GetBoolean());
			Assert.Contains("aiInteraction", expected.Keys);
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeModelsNavigationKeepsSettingsIconsAcrossLanguageChanges() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true); SeedNativeModels(fixture);
		ModelsWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshModelsForTest(window); window.UpdateLayout();
			Button library = ModelControl<Button>(window, "ModelsNav_library");
			Button behaviors = ModelControl<Button>(window, "ModelsNav_behaviors");
			Button[] navigation = [library, behaviors];
			Control?[] content = navigation.Select(button => button.Content as Control).ToArray();
			foreach (Button button in navigation)
			{
				Assert.IsType<Grid>(button.Content); Assert.True(button.Focusable);
				Border symbol = Assert.Single(button.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("settings-nav-symbol"));
				Assert.Equal(26, symbol.Width); Assert.Equal(26, symbol.Height);
				var icon = Assert.Single(symbol.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
				Assert.NotNull(icon.Data); Assert.Equal(24, icon.Width);
			}
			behaviors.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.Contains("selected", behaviors.Classes); Assert.DoesNotContain("selected", library.Classes);
			fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("en-US")); fixture._runtime.InvalidateSnapshot("general");
			await RefreshModelsForTest(window);
			await WaitUntilAsync(() => window.Title == "Nori · Models");
			Assert.Same(content[0], library.Content); Assert.Same(content[1], behaviors.Content);
			Assert.Equal("Model library", Avalonia.Automation.AutomationProperties.GetName(library));
			Assert.Equal("Companion behavior", Avalonia.Automation.AutomationProperties.GetName(behaviors));
			library.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.Contains("selected", library.Classes); Assert.DoesNotContain("selected", behaviors.Classes);
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeModelsChoicesAndPopupsRemainReadableInEveryState() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true); SeedNativeModels(fixture);
		ModelsWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshModelsForTest(window);
			AssertReadable(SettingsBrushes.Resolve(window, "SettingsPrimaryBrush"), SettingsBrushes.Resolve(window, "PopupBackgroundBrush"));
			Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("仅导入本地模型") == true);
			await window.OpenAdjustAsync("arg-nori"); window.UpdateLayout();
			CheckChoices(ModelControl<ToggleButton>(window, "ModelsExpression_Default"));
			CheckPopup(ModelControl<ComboBox>(window, "ModelsDisplay_arg-nori_maxFps"));
			ModelControl<Button>(window, "ModelsTabInteractions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			window.AddRegion(); window.UpdateLayout();
			CheckChoices(ModelControl<ToggleButton>(window, "ModelsReactionLocal"));
			CheckChoices(ModelControl<ToggleButton>(window, "ModelsReactionAi"));
			CheckPopup(ModelControl<ComboBox>(window, "ModelsRegionList"));
			CheckPopup(ModelControl<ComboBox>(window, "ModelsMotionMode"));
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }

		void CheckChoices(ToggleButton button)
		{
			foreach (bool enabled in new[] { true, false })
			foreach (bool selected in new[] { false, true })
			foreach (string state in new[] { "", ":pointerover", ":pressed" })
			{
				button.IsEnabled = enabled; button.IsChecked = selected;
				((IPseudoClasses)button.Classes).Set(":pointerover", state == ":pointerover");
				((IPseudoClasses)button.Classes).Set(":pressed", state == ":pressed");
				window.UpdateLayout();
				AssertReadable(button.Foreground!, button.Background!);
			}
			((IPseudoClasses)button.Classes).Set(":pointerover", false);
			((IPseudoClasses)button.Classes).Set(":pressed", false);
		}

		void CheckPopup(ComboBox combo)
		{
			combo.IsDropDownOpen = true; window.UpdateLayout();
			Popup popup = Assert.Single(combo.GetVisualDescendants().OfType<Popup>());
			Border panel = Assert.Single(popup.Child!.GetVisualDescendants().OfType<Border>(), border => border.Name == "InnerBorderHighlight");
			ComboBoxItem[] items = popup.Child!.GetVisualDescendants().OfType<ComboBoxItem>().ToArray();
			Assert.NotEmpty(items);
			foreach (ComboBoxItem item in items)
			{
				Border background = Assert.Single(item.GetVisualDescendants().OfType<Border>(), border => border.Name == "RootPanel");
				var content = Assert.Single(item.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>(), presenter => presenter.Name == "PART_ContentPresenter");
				AssertReadable(content.Foreground!, background.Background is ISolidColorBrush { Color.A: 255 } ? background.Background : panel.Background!);
				((IPseudoClasses)item.Classes).Set(":pointerover", true);
				window.UpdateLayout();
				AssertReadable(content.Foreground!, background.Background!);
				((IPseudoClasses)item.Classes).Set(":pointerover", false);
			}
			combo.IsDropDownOpen = false;
		}

		static void AssertReadable(IBrush foreground, IBrush background)
		{
			Color fg = Assert.IsAssignableFrom<ISolidColorBrush>(foreground).Color;
			Color bg = Assert.IsAssignableFrom<ISolidColorBrush>(background).Color;
			static double Channel(byte value) { double channel = value / 255d; return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4); }
			static double Luminance(Color color) => Channel(color.R) * 0.2126 + Channel(color.G) * 0.7152 + Channel(color.B) * 0.0722;
			double ratio = (Math.Max(Luminance(fg), Luminance(bg)) + 0.05) / (Math.Min(Luminance(fg), Luminance(bg)) + 0.05);
			Assert.True(ratio >= 4.5, $"文字对比度不足：{fg} / {bg} = {ratio:F2}");
		}
	});

	[Fact]
	public Task NativeModelsExpressionsAreSingleChoiceClearablePerModelAndSnapshotSynchronized() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true); SeedNativeModels(fixture);
		fixture._config.Set("l2d_expression_arg-nori", new ConfigValue.Json(JsonNode.Parse("[\"KiraKira\"]")!));
		ModelsWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshModelsForTest(window); await window.OpenAdjustAsync("arg-nori"); window.UpdateLayout();
			Assert.Equal("nori", fixture._config.GetStringOr(ConfigStore.KeySelectedModel, ""));
			Assert.Equal(4, ExpressionButtons(window).Length);
			Assert.True(ModelControl<ToggleButton>(window, "ModelsExpression_KiraKira").IsChecked);
			ModelControl<ToggleButton>(window, "ModelsExpression_Default").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.True(ModelControl<ToggleButton>(window, "ModelsExpression_Default").IsChecked);
			Assert.False(ModelControl<ToggleButton>(window, "ModelsExpression_KiraKira").IsChecked);
			ModelControl<ToggleButton>(window, "ModelsExpression_Default").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.True(ModelControl<ToggleButton>(window, "ModelsExpression_").IsChecked);
			ModelControl<ToggleButton>(window, "ModelsExpression_Angry").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await RefreshModelsForTest(window); Assert.True(ModelControl<ToggleButton>(window, "ModelsExpression_Angry").IsChecked);
			Assert.True(await window.FlushPendingSavesAsync());
			Assert.Contains("Angry", fixture._config.Get("l2d_expression_arg-nori")!.ToStorage());
			fixture._config.Set("l2d_expression_arg-nori", new ConfigValue.Json(JsonNode.Parse("[\"KiraKira\"]")!)); fixture._runtime.InvalidateSnapshot("models");
			await RefreshModelsForTest(window);
			await WaitUntilAsync(() => ModelControl<ToggleButton>(window, "ModelsExpression_KiraKira").IsChecked == true);
			Assert.True(await window.CloseAdjustAsync()); await window.OpenAdjustAsync("nori"); window.UpdateLayout();
			Assert.Equal(3, ExpressionButtons(window).Length);
			ModelControl<ToggleButton>(window, "ModelsExpression_Smile").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			ModelControl<ToggleButton>(window, "ModelsExpression_").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.True(await window.CloseAdjustAsync());
			Assert.Equal("[]", fixture._config.Get("l2d_expression_nori")!.ToStorage());
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeModelsDisplayFieldsSaveIndependentlyAndNeverOverwriteEditingFromSnapshots() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true); SeedNativeModels(fixture);
		ModelsWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshModelsForTest(window); await window.OpenAdjustAsync("arg-nori"); window.UpdateLayout();
			Slider scale = ModelControl<Slider>(window, "ModelsDisplay_arg-nori_scale"); Slider render = ModelControl<Slider>(window, "ModelsDisplay_arg-nori_renderScale");
			scale.Value = 1.75; render.Value = 3.25;
			await RefreshModelsForTest(window); Assert.Equal(1.75, scale.Value); Assert.Equal(3.25, render.Value);
			ModelControl<ToggleSwitch>(window, "ModelsDisplay_arg-nori_shadow").IsChecked = false;
			ModelControl<ComboBox>(window, "ModelsDisplay_arg-nori_maxFps").SelectedIndex = 2;
			Assert.True(await window.FlushPendingSavesAsync());
			Assert.Equal("1.75", fixture._config.Get("l2d_scale_arg-nori")!.ToStorage()); Assert.Equal("3.25", fixture._config.Get("l2d_render_scale_arg-nori")!.ToStorage());
			Assert.False(fixture._config.GetBoolOr("l2d_shadow_arg-nori", true)); Assert.Equal("60", fixture._config.Get("l2d_max_fps_arg-nori")!.ToStorage());
			Assert.Equal("nori", fixture._config.GetStringOr(ConfigStore.KeySelectedModel, ""));
			fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("en-US")); fixture._runtime.InvalidateSnapshot("general"); await RefreshModelsForTest(window);
			await WaitUntilAsync(() => window.Title == "Nori · Models"); Assert.Same(scale, ModelControl<Slider>(window, "ModelsDisplay_arg-nori_scale"));
			window.Close(); await WaitUntilAsync(() => !window.IsVisible);
			window.Show(); await RefreshModelsForTest(window); Assert.Same(scale, ModelControl<Slider>(window, "ModelsDisplay_arg-nori_scale"));
			Assert.True(await window.CloseAdjustAsync()); await window.OpenAdjustAsync("nori"); window.UpdateLayout();
			ModelControl<Slider>(window, "ModelsDisplay_nori_scale").Value = 1.25;
			Assert.True(await window.CloseAdjustAsync()); Assert.Equal("1.25", fixture._config.Get("l2d_scale_nori")!.ToStorage());
			Assert.Equal("1.75", fixture._config.Get("l2d_scale_arg-nori")!.ToStorage());
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeModelsRegionsSupportCrudBindingsAndFailedCloseRetainsDraft() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true); SeedNativeModels(fixture);
		ModelsWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshModelsForTest(window); await window.OpenAdjustAsync("nori");
			ModelControl<Button>(window, "ModelsTabInteractions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
			window.AddRegion(); window.UpdateLayout();
			PetInteractionRegion added = Assert.Single(window.RegionOverlay.Regions); Assert.StartsWith("region_", added.Id); Assert.Equal(PetInteractionReactionMode.Local, added.ReactionMode);
			Assert.Equal(new PetInteractionRect { X = 0.25, Y = 0.25, Width = 0.5, Height = 0.5 }, added.Rect);
			TextBox name = ModelControl<TextBox>(window, "ModelsRegionName"); name.Text = "头部";
			ModelControl<ComboBox>(window, "ModelsMotionMode").SelectedIndex = 2;
			ModelControl<ComboBox>(window, "ModelsExpressionMode").SelectedIndex = 2;
			Assert.True(await window.FlushPendingSavesAsync());
			PetInteractionConfig saved = PetInteractionConfig.Parse(fixture._config.Get(PetInteractionConfig.StorageKey("nori"))!.ToStorage());
			Assert.Equal("tap_01", saved.Regions[0].Motion.Name); Assert.Equal("Smile", saved.Regions[0].Expression.Name);
			name.Text = ""; Assert.False(await window.CloseAdjustAsync()); Assert.Equal("nori", window.CurrentModelId); Assert.Equal("", name.Text);
			await Assert.ThrowsAsync<InvalidOperationException>(window.PrepareShutdownAsync); Assert.True(((Control)window.Content!).IsEnabled);
			name.Text = "修正后的名称"; Assert.True(await window.FlushPendingSavesAsync());
			window.AddRegion(); Assert.Equal(2, window.RegionOverlay.Regions.Count); window.DeleteRegion(added.Id); Assert.Single(window.RegionOverlay.Regions);
			window.ClearRegions(); Assert.Empty(window.RegionOverlay.Regions); Assert.True(await window.CloseAdjustAsync());
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeModelsSaveBarrierWaitsForPendingFieldAndPreservesFailedShutdown() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true); ModelsWindow window = new(fixture._services);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			window.Show(); await RefreshModelsForTest(window);
			MemorySettingDraft pending = new(0, _ => release.Task, () => { }); ModelDrafts(window)["synthetic"] = pending; pending.Set(1);
			Task<bool> flush = window.FlushPendingSavesAsync(); Assert.False(flush.IsCompleted); Assert.False(((Control)window.Content!).IsEnabled);
			release.SetResult(); Assert.True(await flush); Assert.True(((Control)window.Content!).IsEnabled);
			bool fail = true; MemorySettingDraft failed = new(2, _ => fail ? Task.FromException(new InvalidOperationException("合成保存失败")) : Task.CompletedTask, () => { }); ModelDrafts(window)["failed"] = failed; failed.Set(3);
			await Assert.ThrowsAsync<InvalidOperationException>(window.PrepareShutdownAsync); Assert.True(failed.Dirty); Assert.Equal(3, failed.Value); Assert.True(((Control)window.Content!).IsEnabled);
			failed.AcceptSnapshot(2); Assert.Equal(3, failed.Value); fail = false; Assert.True(await window.FlushPendingSavesAsync()); await window.PrepareShutdownAsync();
		}
		finally { release.TrySetResult(); window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeModelsDebouncePersistsBothFieldsWithoutExplicitFlush() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true); SeedNativeModels(fixture);
		ModelsWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshModelsForTest(window); await window.OpenAdjustAsync("arg-nori"); window.UpdateLayout();
			ModelControl<Slider>(window, "ModelsDisplay_arg-nori_scale").Value = 2.15;
			ModelControl<Slider>(window, "ModelsDisplay_arg-nori_renderScale").Value = 3.5;
			Assert.True(ModelDrafts(window)["arg-nori:scale"].Dirty); Assert.False(ModelDrafts(window)["arg-nori:scale"].Saving);
			await WaitUntilAsync(() => fixture._config.Get("l2d_scale_arg-nori")?.ToStorage() == "2.15" && fixture._config.Get("l2d_render_scale_arg-nori")?.ToStorage() == "3.5");
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeModelsEmptyActionsAreExplicitAndUnsavedRegionsBlockCrossModelNavigation() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true); fixture.InstallKnownModel("nori"); fixture.InstallKnownModel("arg-nori");
		ModelsWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshModelsForTest(window); await window.OpenAdjustAsync("arg-nori"); window.UpdateLayout();
			Assert.Single(ExpressionButtons(window));
			ModelControl<Button>(window, "ModelsTabInteractions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.AddRegion(); window.UpdateLayout();
			Assert.True(ModelControl<TextBlock>(window, "ModelsMotionEmpty").IsVisible); Assert.True(ModelControl<TextBlock>(window, "ModelsExpressionEmpty").IsVisible);
			Assert.False(((ComboBoxItem)ModelControl<ComboBox>(window, "ModelsMotionMode").Items[2]!).IsEnabled);
			Assert.False(ModelControl<ToggleButton>(window, "ModelsReactionAi").IsEnabled);
			TextBox name = ModelControl<TextBox>(window, "ModelsRegionName"); name.Text = "";
			await window.OpenAdjustAsync("nori"); Assert.Equal("arg-nori", window.CurrentModelId); Assert.Same(name, ModelControl<TextBox>(window, "ModelsRegionName"));
			name.Text = "保留的区域"; Assert.True(await window.CloseAdjustAsync());
			await window.OpenAdjustAsync("nori"); Assert.Equal("nori", window.CurrentModelId); Assert.Empty(window.RegionOverlay.Regions);
			Assert.Equal("保留的区域", PetInteractionConfig.Parse(fixture._config.Get(PetInteractionConfig.StorageKey("arg-nori"))!.ToStorage()).Regions[0].Name);
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeModelsClearAllRequiresConfirmationAndCancellationKeepsRegions() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true); SeedNativeModels(fixture);
		ModelsWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshModelsForTest(window); await window.OpenAdjustAsync("nori");
			ModelControl<Button>(window, "ModelsTabInteractions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.AddRegion(); window.UpdateLayout();
			Button clear = ModelControl<Button>(window, "ModelsRegionClear"); clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Window confirm = window.OwnedWindows.Single(); Assert.Equal(ThemeVariant.Dark, confirm.ActualThemeVariant);
			Task<bool> closingFlush = window.FlushPendingSavesAsync(); Assert.False(closingFlush.IsCompleted); Assert.True(confirm.IsEnabled);
			confirm.Close(false); Assert.True(await closingFlush);
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); Assert.Single(window.RegionOverlay.Regions);
			clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); confirm = window.OwnedWindows.Single(); confirm.Close(true);
			await WaitUntilAsync(() => window.RegionOverlay.Regions.Count == 0); Assert.True(await window.FlushPendingSavesAsync());
			await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	[Fact]
	public Task NativeModelsRemainDarkAcrossApplicationThemeChanges() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		Avalonia.Application app = Avalonia.Application.Current!; ThemeVariant? original = app.RequestedThemeVariant;
		ModelsWindow window = new(fixture._services);
		try
		{
			app.RequestedThemeVariant = ThemeVariant.Light; window.Show(); await RefreshModelsForTest(window); Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
			app.RequestedThemeVariant = ThemeVariant.Dark; app.RequestedThemeVariant = ThemeVariant.Light; Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
			Assert.Equal(720, window.MinWidth); Assert.Equal(480, window.MinHeight); await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); app.RequestedThemeVariant = original; }
	});

	[Fact]
	public Task NativeModelsAdjustmentStacksBelowEightHundredWithoutRemountingPreview() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true); SeedNativeModels(fixture);
		ModelsWindow window = new(fixture._services);
		try
		{
			window.Show(); await RefreshModelsForTest(window); await window.OpenAdjustAsync("arg-nori"); window.UpdateLayout();
			Grid layout = ModelControl<Grid>(window, "ModelsAdjustLayout");
			Border stage = ModelControl<Border>(window, "ModelsPreviewHost");
			Control preview = ModelControl<Control>(window, "ModelsPreview");
			ContentControl controls = layout.Children.OfType<ContentControl>().Single();
			ScrollViewer scroll = Assert.IsType<ScrollViewer>(controls.Content);
			Slider scale = ModelControl<Slider>(window, "ModelsDisplay_arg-nori_scale"); scale.Value = 1.35;
			int attachments = 0, detachments = 0;
			preview.AttachedToVisualTree += (_, _) => attachments++;
			preview.DetachedFromVisualTree += (_, _) => detachments++;
			foreach ((int width, int height) in new[] { (720, 480), (960, 640), (1920, 1080), (847, 640), (848, 640), (720, 480), (960, 640), (720, 480) })
			{
				window.Width = width; window.Height = height;
				await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
				bool stacked = layout.Bounds.Width < 800;
				Assert.Equal(width - 48, layout.Bounds.Width, 1); Assert.Equal(width - 48 < 800, stacked);
				Assert.Equal(stacked ? 1 : 2, layout.ColumnDefinitions.Count); Assert.Equal(stacked ? 2 : 1, layout.RowDefinitions.Count);
				Assert.Equal(0, stage.MinWidth); Assert.Equal(0, stage.MinHeight); Assert.Equal(0, controls.MinHeight);
				if (stacked)
				{
					Assert.Equal(stage.Bounds.Width, controls.Bounds.Width, 1);
					Assert.Equal(stage.Bounds.Bottom + 16, controls.Bounds.Top, 1);
					Assert.InRange(stage.Bounds.Height / controls.Bounds.Height, 0.65, 0.68);
				}
				else
				{
					Assert.Equal(stage.Bounds.Height, controls.Bounds.Height, 1);
					Assert.Equal(stage.Bounds.Right + 16, controls.Bounds.Left, 1);
					Assert.Equal(stage.Bounds.Width, controls.Bounds.Width, 1);
				}
				Assert.True(stage.Bounds.Bottom <= layout.Bounds.Height + 1); Assert.True(controls.Bounds.Bottom <= layout.Bounds.Height + 1);
				Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 2);
				Assert.Same(layout, ModelControl<Grid>(window, "ModelsAdjustLayout")); Assert.Same(stage, ModelControl<Border>(window, "ModelsPreviewHost"));
				Assert.Same(preview, ModelControl<Control>(window, "ModelsPreview")); Assert.Same(scroll, controls.Content);
				Assert.Same(scale, ModelControl<Slider>(window, "ModelsDisplay_arg-nori_scale")); Assert.Equal(1.35, scale.Value);
			}
			Assert.Equal(0, attachments); Assert.Equal(0, detachments);
			Assert.True(await window.CloseAdjustAsync()); await window.PrepareShutdownAsync();
		}
		finally { window.AllowClose = true; window.Close(); }
	});

	private static void SeedNativeModels(BridgeCommandsTests fixture)
	{
		foreach (string id in new[] { "arg-nori", "nori" })
		{
			string directory = fixture._services.Resources.ResourceDir(ResourceType.Live2D, id); Directory.CreateDirectory(directory);
			string[] expressions = id == "arg-nori" ? ["Default", "KiraKira", "Angry"] : ["Smile", "Tears"];
			File.WriteAllText(Path.Combine(directory, id + ".model3.json"), JsonSerializer.Serialize(new { FileReferences = new { Moc = "model.moc3", Textures = Array.Empty<string>(), Expressions = expressions.Select(name => new { Name = name, File = name + ".exp3.json" }), Motions = new Dictionary<string, object> { ["tap_body"] = new[] { new { File = "tap_01.motion3.json" }, new { File = "tap_02.motion3.json" } } } } }));
			// 合成资源只用于元数据和 UI 验证，不声称渲染了有效的 Cubism 模型。
			File.WriteAllText(Path.Combine(directory, "model.moc3"), "MOC3");
			foreach (string name in expressions) File.WriteAllText(Path.Combine(directory, name + ".exp3.json"), "{}");
			foreach (string name in new[] { "tap_01", "tap_02" }) File.WriteAllText(Path.Combine(directory, name + ".motion3.json"), "{}");
		}
		fixture._config.Set(ConfigStore.KeySelectedModel, new ConfigValue.Text("nori")); fixture._runtime.InvalidateSnapshot("models");
	}
	private static async Task RefreshModelsForTest(ModelsWindow window)
	{
		await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); await window.RefreshAsync(); await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
	}
	private static T ModelControl<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
	private static ToggleButton[] ExpressionButtons(ModelsWindow window) => window.GetVisualDescendants().OfType<ToggleButton>().Where(control => control.Name?.StartsWith("ModelsExpression_", StringComparison.Ordinal) == true).ToArray();
	private static Dictionary<string, MemorySettingDraft> ModelDrafts(ModelsWindow window) => (Dictionary<string, MemorySettingDraft>)typeof(ModelsWindow).GetField("_drafts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
}
