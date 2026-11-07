using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Core.Platform;
using Nori.Desktop.Appearance;
using Nori.Desktop.Chat;
using Nori.Desktop.Live2D;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;
using Nori.Startup;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task 强调色切换就地更新共享画刷与控件资源() => WithSettingsUiAsync(() =>
	{
		NoriAppearance appearance = Assert.IsType<NoriAppearance>(NoriAppearance.Current);
		Button button = new() {Content = "OK"};
		button.Classes.Add("accent");
		Window window = new() {Width = 320, Height = 200, Content = button};
		window.Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/")) {Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml")});
		try
		{
			window.Show();
			Application application = Assert.IsAssignableFrom<Application>(Application.Current);
			ISolidColorBrush teal = Assert.IsAssignableFrom<ISolidColorBrush>(ChatPalette.Teal);
			// 窗口级主题与全局控件主题都必须解析到应用级的同一个画刷，换色才能就地生效。
			Assert.Same(teal, application.FindResource("NoriNoriTealBrush"));
			Assert.Same(teal, button.FindResource("SettingsAccentBrush"));
			Assert.Same(teal, button.FindResource("AccentButtonBackground"));
			Assert.Same(teal, new SettingsBrushPalette(button)["SettingsAccentBrush"]);

			appearance.Apply(AppearancePreferences.Default with {Accent = "sakura"});

			Color sakura = NoriAccentPresets.Color("sakura", "nori-teal");
			Assert.Equal(sakura, teal.Color);
			Assert.Equal(sakura, Assert.IsAssignableFrom<ISolidColorBrush>(button.FindResource("ToggleSwitchFillOn")).Color);
			Assert.Equal(sakura, Assert.IsType<Color>(application.FindResource("SystemAccentColor")));
			Assert.Equal(NoriAccentPresets.Color("sakura", "bg-selection"),
				Assert.IsAssignableFrom<ISolidColorBrush>(button.FindResource("SettingsSelectionBrush")).Color);
			Assert.Equal(NoriAccentPresets.Color("sakura", "nori-teal-bright"), Assert.IsAssignableFrom<ISolidColorBrush>(ChatPalette.Accent).Color);
		}
		finally { window.Close(); }
		return Task.CompletedTask;
	});

	[Fact]
	public Task 界面缩放作用于普通窗口框架并放大最小尺寸() => WithSettingsUiAsync(() =>
	{
		NoriAppearance appearance = Assert.IsType<NoriAppearance>(NoriAppearance.Current);
		Window window = new() {Content = new TextBlock {Text = "Nori"}};
		NativeWindowSizing.Apply(window, NativeWindowSizing.DefaultSize);
		NativeWindowChrome.Attach(window);
		try
		{
			window.Show();
			LayoutTransformControl scaled = window.GetLogicalDescendants().OfType<LayoutTransformControl>()
				.Single(control => control.Name == "NativeWindowScale");
			Assert.Null(scaled.LayoutTransform);
			Assert.Equal(NativeWindowSizing.MinimumSize.Width, window.MinWidth);

			appearance.Apply(AppearancePreferences.Default with {UiScale = 125});

			Assert.Equal(1.25, Assert.IsAssignableFrom<ITransform>(scaled.LayoutTransform).Value.M11, 3);
			Assert.Equal(NativeWindowSizing.MinimumSize.Width * 1.25, window.MinWidth, 3);
			Assert.Equal(NativeWindowSizing.MinimumSize.Height * 1.25, window.MinHeight, 3);

			appearance.Apply(AppearancePreferences.Default);

			Assert.Null(scaled.LayoutTransform);
			Assert.Equal(NativeWindowSizing.MinimumSize.Width, window.MinWidth);
		}
		finally { window.Close(); }
		return Task.CompletedTask;
	});

	[Fact]
	public Task 对话气泡字体与字号随偏好即时更新() => WithSettingsUiAsync(() =>
	{
		NoriAppearance appearance = Assert.IsType<NoriAppearance>(NoriAppearance.Current);
		Control markdown = ChatMarkdown.Render("早上好 Nori，9:30 开会", _ => { });
		PetSpeechOverlay speech = new();
		Window window = new() {Width = 480, Height = 320, Content = new StackPanel {Children = {markdown, speech}}};
		try
		{
			window.Show();
			speech.ShowText("你好 Nori");
			TextBlock body = markdown.GetSelfAndLogicalDescendants().OfType<TextBlock>().First(text => !string.IsNullOrEmpty(text.Text) || text.Inlines?.Count > 0);
			TextBlock line = Assert.IsAssignableFrom<TextBlock>(speech.Child);
			Assert.Equal(NoriTypography.Conversation, body.FontFamily);
			Assert.Equal(NoriMetrics.FontBase, body.FontSize, 3);
			Assert.Equal(NoriMetrics.FontBase, line.FontSize, 3);

			appearance.Apply(AppearancePreferences.Default with {BubbleFont = AppearancePreferences.BubbleFontInterface, BubbleSize = AppearancePreferences.BubbleSizeLarge});

			Assert.Equal(NoriTypography.System, body.FontFamily);
			Assert.Equal(NoriMetrics.FontLg, body.FontSize, 3);
			Assert.Equal(NoriMetrics.FontLg * 1.625, body.LineHeight, 3);
			Assert.Equal(NoriTypography.System, line.FontFamily);
			Assert.Equal(NoriMetrics.FontLg, line.FontSize, 3);

			appearance.Apply(AppearancePreferences.Default with {BubbleFont = AppearancePreferences.BubbleFontSystem});
			// Skia 的默认字体在部分 Linux 桌面上是等宽字体，系统字体必须先落到具名的界面字体。
			Assert.Equal(NoriTypography.SystemUi, body.FontFamily);
			Assert.NotEqual(FontFamily.Default.Name, NoriTypography.SystemUi.FamilyNames[0]);
			Assert.Equal(NoriMetrics.FontBase, body.FontSize, 3);
		}
		finally { window.Close(); }
		return Task.CompletedTask;
	});

	[Fact]
	public Task 动效偏好的显式档位覆盖系统查询() => WithSettingsUiAsync(() =>
	{
		NoriAppearance appearance = Assert.IsType<NoriAppearance>(NoriAppearance.Current);
		MotionPreference.Override(true);
		try
		{
			Assert.Equal(MotionMode.System, MotionPreference.Mode);
			Assert.True(MotionPreference.AllowAnimation);
			Assert.Equal(PlatformServices.Current.PrefersReducedMotion, MotionPreference.PrefersReducedMotion);

			appearance.Apply(AppearancePreferences.Default with {Motion = MotionMode.Reduce});
			Assert.False(MotionPreference.AllowAnimation);
			Assert.True(MotionPreference.PrefersReducedMotion);

			appearance.Apply(AppearancePreferences.Default with {Motion = MotionMode.Full});
			Assert.True(MotionPreference.AllowAnimation);
			Assert.False(MotionPreference.PrefersReducedMotion);
		}
		finally
		{
			appearance.Apply(AppearancePreferences.Default);
			MotionPreference.Override(null);
		}
		return Task.CompletedTask;
	});

	[Fact]
	public void 外观偏好读取对异常值回退默认()
	{
		Assert.Equal(AppearancePreferences.Default, AppearancePreferences.Read(_config));
		_config.Set(ConfigStore.KeyUiAccent, new ConfigValue.Text("neon"));
		_config.Set(ConfigStore.KeyUiScale, new ConfigValue.Integer(133));
		_config.Set(ConfigStore.KeyBubbleFont, new ConfigValue.Text("comic"));
		_config.Set(ConfigStore.KeyBubbleSize, new ConfigValue.Text("huge"));
		_config.Set(ConfigStore.KeyMotion, new ConfigValue.Text("maybe"));
		Assert.Equal(AppearancePreferences.Default, AppearancePreferences.Read(_config));

		_config.Set(ConfigStore.KeyUiAccent, new ConfigValue.Text("lavender"));
		_config.Set(ConfigStore.KeyUiScale, new ConfigValue.Text("110"));
		_config.Set(ConfigStore.KeyBubbleFont, new ConfigValue.Text(AppearancePreferences.BubbleFontSystem));
		_config.Set(ConfigStore.KeyBubbleSize, new ConfigValue.Text(AppearancePreferences.BubbleSizeLarge));
		_config.Set(ConfigStore.KeyMotion, new ConfigValue.Text("full"));
		Assert.Equal(new AppearancePreferences("lavender", 110, AppearancePreferences.BubbleFontSystem, AppearancePreferences.BubbleSizeLarge, MotionMode.Full),
			AppearancePreferences.Read(_config));
	}

	[Fact]
	public Task 个性化外观写入配置并通知窗口() => WithSettingsUiAsync(async () =>
	{
		await CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_general",
			Args(new {accent = "sakura", uiScale = 125, bubbleFont = "system", bubbleSize = "large", motion = "reduce"}));
		await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

		AppearancePreferences expected = new("sakura", 125, AppearancePreferences.BubbleFontSystem, AppearancePreferences.BubbleSizeLarge, MotionMode.Reduce);
		Assert.Equal(expected, AppearancePreferences.Read(_config));
		Assert.Equal(expected, Assert.IsType<FakeWindowManager>(_services.Windows).AppearanceChanges.Last());
		JsonElement general = JsonSerializer.SerializeToElement(_runtime.BuildSnapshot()).GetProperty("general");
		Assert.Equal("sakura", general.GetProperty("accent").GetString());
		Assert.Equal(125, general.GetProperty("uiScale").GetInt32());
		Assert.Equal("system", general.GetProperty("bubbleFont").GetString());
		Assert.Equal("large", general.GetProperty("bubbleSize").GetString());
		Assert.Equal("reduce", general.GetProperty("motion").GetString());
	});

	/// <summary>
	/// 一个补丁里只要有一项不合法就整体拒绝。
	///
	/// 合法的 accent 与非法项放在同一个补丁里：只写进去一半的话，界面上看到的是「保存失败」，
	/// 而颜色其实已经变了。
	/// </summary>
	[Theory]
	[InlineData("""{"accent":"sakura","uiScale":133}""")]
	[InlineData("""{"accent":"sakura","uiScale":"125"}""")]
	[InlineData("""{"accent":"neon"}""")]
	[InlineData("""{"accent":"sakura","bubbleFont":"comic"}""")]
	[InlineData("""{"accent":"sakura","bubbleSize":"huge"}""")]
	[InlineData("""{"accent":"sakura","motion":"maybe"}""")]
	public async Task 个性化外观的非法取值整体拒绝(string json)
	{
		using JsonDocument args = JsonDocument.Parse(json);
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "settings_update_general", args.RootElement));
		Assert.Equal(AppearancePreferences.Default, AppearancePreferences.Read(_config));
		Assert.Empty(_windows.AppearanceChanges);
	}

	[Fact]
	public async Task 启动画面背景选择只接受主窗口来源且需要窗口实例()
	{
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			CreateCommands().InvokeAsync(new FakeBridgeSource(WindowLabels.Pet), "settings_pick_splash_background", Args(new { })));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			CreateCommands().InvokeAsync(new NativeSettingsCommandSource(), "settings_pick_splash_background", Args(new { })));
	}

	[Fact]
	public Task 个性化页包含全部字段并随启动画面开关联动() => WithSettingsUiAsync(async () =>
	{
		using SettingsService service = new(_services, new Window());
		using PersonalizationSettingsPage page = new(service);
		Dictionary<string, SettingsFieldViewModel> fields = page.Sections.SelectMany(section => section.Fields).ToDictionary(field => field.Key);
		Assert.Equal(
			["accent", "uiScale", "backgroundBlurEnabled", "motion", "bubbleFont", "bubbleSize", "bubblePreview", "splashEnabled", "splashTheme", "splashBackground"],
			fields.Keys);
		Assert.Equal(SettingsEditorKind.Swatches, fields["accent"].EditorKind);
		Assert.Equal(NoriAccentPresets.Ids, fields["accent"].Options.Select(option => option.Value));
		Assert.All(fields["accent"].Options, option => Assert.False(string.IsNullOrEmpty(option.Swatch)));
		Assert.Equal(["90", "100", "110", "125"], fields["uiScale"].Options.Select(option => option.Value));
		Assert.Equal(SettingsEditorKind.SplashLayouts, fields["splashTheme"].EditorKind);
		Assert.Equal(SplashOptions.Themes, fields["splashTheme"].Options.Select(option => option.Value));
		Assert.Equal(SettingsEditorKind.ImageFile, fields["splashBackground"].EditorKind);
		Assert.Equal(SettingsEditorKind.BubblePreview, fields["bubblePreview"].EditorKind);

		using JsonDocument disabled = JsonDocument.Parse("""{"general":{"splashEnabled":false,"splashTheme":"island","accent":"sky","uiScale":110,"motion":"reduce"}}""");
		page.ApplySnapshot(disabled.RootElement);
		Assert.Equal("sky", fields["accent"].Selected);
		Assert.Equal("110", fields["uiScale"].Selected);
		Assert.Equal("reduce", fields["motion"].Selected);
		Assert.True(fields["splashTheme"].IsReadOnly);
		Assert.True(fields["splashBackground"].IsReadOnly);
		Assert.False(fields["splashBackground"].IsVisible);

		fields["splashEnabled"].Boolean = true;
		Assert.False(fields["splashTheme"].IsReadOnly);
		Assert.False(fields["splashBackground"].IsReadOnly);
		fields["splashTheme"].Selected = "android-studio";
		Assert.True(fields["splashBackground"].IsVisible);
		fields["uiScale"].Selected = "125";

		Assert.True(await page.FlushPendingSavesAsync());
		Assert.True(_config.GetBoolOr("splash_enabled", false));
		Assert.Equal("android-studio", _config.GetStringOr("splash_theme", ""));
		Assert.Equal(125, AppearancePreferences.Read(_config).UiScale);
	});

	[Theory]
	[InlineData(720, 480)]
	[InlineData(1920, 1080)]
	public Task 个性化编辑器在常见窗口尺寸下可直接改选中值(int width, int height) => WithSettingsUiAsync(async () =>
	{
		SettingsWindow window = new() {Width = width, Height = height};
		using SettingsService service = new(_services, window);
		using SettingsViewModel viewModel = new(service);
		window.DataContext = viewModel;
		try
		{
			window.Show();
			viewModel.Navigate("personalization");
			await viewModel.RefreshSnapshotAsync();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			PersonalizationSettingsPage page = Assert.IsType<PersonalizationSettingsPage>(viewModel.CurrentPage);
			Dictionary<string, SettingsFieldViewModel> fields = page.Sections.SelectMany(section => section.Fields).ToDictionary(field => field.Key);
			T Editor<T>(string key) where T : Control => window.GetVisualDescendants().OfType<SettingsFieldPresenter>()
				.Single(presenter => ReferenceEquals(presenter.DataContext, fields[key]))
				.GetVisualDescendants().OfType<T>().Single();

			SwatchEditor swatches = Editor<SwatchEditor>("accent");
			Assert.True(swatches.Bounds.Width > 0);
			Button sakura = swatches.Children.OfType<Button>().ElementAt(NoriAccentPresets.Ids.ToList().IndexOf("sakura"));
			sakura.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
			Assert.Equal("sakura", fields["accent"].Selected);

			SegmentedEditor scale = Editor<SegmentedEditor>("uiScale");
			scale.Children.OfType<Avalonia.Controls.Primitives.ToggleButton>().Last().IsChecked = true;
			Assert.Equal("125", fields["uiScale"].Selected);

			SplashLayoutGallery gallery = Editor<SplashLayoutGallery>("splashTheme");
			Assert.True(gallery.ColumnDefinitions.Count >= 2, $"{width}x{height} 下只排出 {gallery.ColumnDefinitions.Count} 列");
			Button island = gallery.Children.OfType<Button>().ElementAt(SplashOptions.Themes.ToList().IndexOf("island"));
			island.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
			Assert.Equal("island", fields["splashTheme"].Selected);
			Assert.False(fields["splashBackground"].IsVisible);

			Assert.True(Editor<BubblePreviewEditor>("bubblePreview").Bounds.Height > 0);
			Assert.True(await viewModel.FlushPendingSavesAsync());
			Assert.Equal("sakura", AppearancePreferences.Read(_config).Accent);
			Assert.Equal(125, AppearancePreferences.Read(_config).UiScale);
			Assert.Equal("island", _config.GetStringOr("splash_theme", ""));
		}
		finally
		{
			window.DataContext = null;
			window.Close();
		}
	});

	[Fact]
	public Task 常规页不再承载外观与启动画面设置() => WithSettingsUiAsync(() =>
	{
		using SettingsService service = new(_services, new Window());
		using GeneralSettingsPage page = new(service);
		string[] keys = page.Sections.SelectMany(section => section.Fields).Select(field => field.Key).ToArray();
		Assert.DoesNotContain("backgroundBlurEnabled", keys);
		Assert.DoesNotContain("splashEnabled", keys);
		Assert.DoesNotContain("splashTheme", keys);
		Assert.DoesNotContain("splashBackground", keys);
		Assert.Contains("petAutoSummon", keys);
		return Task.CompletedTask;
	});
}
