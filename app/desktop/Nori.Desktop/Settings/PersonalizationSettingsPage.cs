using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using Nori.Desktop.Appearance;
using Nori.Startup;

namespace Nori.Desktop.Settings;

/// <summary>主题色、界面缩放、对话气泡、动效与启动画面。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1854", Justification = "设置命令回调按 UI 约定显式丢弃受控后台 Task。")]
public sealed class PersonalizationSettingsPage : SettingsPageBase
{
	private static readonly string ScaleFallback = AppearancePreferences.DefaultUiScale.ToString(CultureInfo.InvariantCulture);

	private readonly SettingsFieldViewModel _splashEnabled;
	private readonly SettingsFieldViewModel _splashTheme;
	private readonly SettingsFieldViewModel _splashBackground;

	/// <summary>创建个性化设置页。</summary>
	public PersonalizationSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(
			service,
			"personalization",
			"app",
			new("个性化", "Personalization"),
			new("调整主题色、界面缩放、对话气泡、动效与启动画面。", "Customize accent colour, scale, chat bubbles, motion and the splash screen."),
			lifetimeToken)
	{
		SettingsSectionViewModel accent = AddSection(new("主题色", "Theme colour"));
		AddField(
			accent,
			"accent",
			new("强调色", "Accent colour"),
			new(
				"按钮、开关、选中项和窗口焦点描边使用这个颜色，立即生效；发丝线、文字和对话气泡配色保持不变。",
				"Buttons, switches, selected items and the window focus outline use this colour, and it applies immediately. Hairlines, text and chat bubble colours stay the same."),
			SettingsEditorKind.Swatches,
			snapshot => SettingsSnapshotReader.String(snapshot, NoriAccentPresets.Default, "general", "accent"),
			NoriAccentPresets.Default,
			(value, token) => ExecuteAsync("settings_update_general", new { accent = Convert.ToString(value) ?? NoriAccentPresets.Default }, token),
			options: AccentOptions());

		SettingsSectionViewModel appearance = AddSection(new("界面", "Interface"));
		AddField(
			appearance,
			"uiScale",
			new("界面缩放", "Interface scale"),
			new(
				"缩放主界面、设置、对话等普通窗口，立即生效。桌宠、快捷聊天和首次运行向导保持原大小，下拉菜单与提示不随之缩放。",
				"Scales ordinary windows such as the main window, settings and chat, and applies immediately. The desktop pet, Quick Chat and the first-run wizard keep their size, and menus and tooltips are not scaled."),
			SettingsEditorKind.Segmented,
			snapshot => SettingsSnapshotReader.String(snapshot, ScaleFallback, "general", "uiScale"),
			ScaleFallback,
			(value, token) => ExecuteAsync(
				"settings_update_general",
				new { uiScale = int.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ScaleFallback, CultureInfo.InvariantCulture) },
				token),
			options: ScaleOptions());
		AddField(
			appearance,
			"backgroundBlurEnabled",
			new("背景模糊", "Background blur"),
			new("为窗口启用系统背景模糊；不支持时使用深色背景。", "Use system background blur for windows, with a dark background when unavailable."),
			SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "general", "backgroundBlurEnabled"),
			true,
			(value, token) => ExecuteAsync("settings_update_general", new { backgroundBlurEnabled = Convert.ToBoolean(value) }, token));
		AddField(
			appearance,
			"motion",
			new("动效", "Motion"),
			new(
				"跟随系统时按系统的“减少动画”设置；减少动效会跳过页面过场、登录光环与快捷聊天的过渡。",
				"Follow system uses the system's \"reduce motion\" setting. Reduce motion skips page transitions, the sign-in halo and Quick Chat transitions."),
			SettingsEditorKind.Segmented,
			snapshot => SettingsSnapshotReader.String(snapshot, AppearancePreferences.MotionModes[0], "general", "motion"),
			AppearancePreferences.MotionModes[0],
			(value, token) => ExecuteAsync(
				"settings_update_general",
				new { motion = Convert.ToString(value) ?? AppearancePreferences.MotionModes[0] },
				token),
			options: MotionOptions());

		SettingsSectionViewModel bubbles = AddSection(new("对话气泡", "Chat bubbles"));
		AddField(
			bubbles,
			"bubbleFont",
			new("字体", "Font"),
			new(
				"作用于对话窗口、快捷聊天和桌宠短句，立即生效。圆润与界面字体只改变英文字母和数字，中文优先使用 Noto Sans SC；系统字体的英文与中文都使用系统界面字体。",
				"Applies to the chat window, Quick Chat and pet phrases immediately. Rounded and Interface change only Latin letters and digits, and Chinese prefers Noto Sans SC. System uses the system UI fonts for both Latin and Chinese text."),
			SettingsEditorKind.Segmented,
			snapshot => SettingsSnapshotReader.String(snapshot, AppearancePreferences.BubbleFontRounded, "general", "bubbleFont"),
			AppearancePreferences.BubbleFontRounded,
			(value, token) => ExecuteAsync(
				"settings_update_general",
				new { bubbleFont = Convert.ToString(value) ?? AppearancePreferences.BubbleFontRounded },
				token),
			options: BubbleFontOptions());
		AddField(
			bubbles,
			"bubbleSize",
			new("字号", "Size"),
			new("标准或大号，立即生效。", "Standard or large. Applies immediately."),
			SettingsEditorKind.Segmented,
			snapshot => SettingsSnapshotReader.String(snapshot, AppearancePreferences.BubbleSizeStandard, "general", "bubbleSize"),
			AppearancePreferences.BubbleSizeStandard,
			(value, token) => ExecuteAsync(
				"settings_update_general",
				new { bubbleSize = Convert.ToString(value) ?? AppearancePreferences.BubbleSizeStandard },
				token),
			options: BubbleSizeOptions());
		AddField(
			bubbles,
			"bubblePreview",
			new("预览", "Preview"),
			new("", ""),
			SettingsEditorKind.BubblePreview,
			_ => "",
			"",
			(_, _) => Task.FromResult(default(JsonElement)),
			readOnly: true);

		SettingsSectionViewModel startup = AddSection(new("启动画面", "Splash screen"));
		_splashEnabled = AddField(
			startup,
			"splashEnabled",
			new("显示启动画面", "Show splash screen"),
			new("下次启动时生效。", "Applies on the next launch."),
			SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "general", "splashEnabled"),
			true,
			(value, token) => ExecuteAsync("settings_update_general", new { splashEnabled = Convert.ToBoolean(value) }, token));
		_splashTheme = AddField(
			startup,
			"splashTheme",
			new("启动画面主题", "Splash theme"),
			new("参考布局统一使用 Nori 外观，作品区默认留白；下次启动时生效。", "Reference layouts use Nori styling with artwork areas left blank by default. Applies on the next launch."),
			SettingsEditorKind.SplashLayouts,
			snapshot => SettingsSnapshotReader.String(snapshot, SplashOptions.DefaultTheme, "general", "splashTheme"),
			SplashOptions.DefaultTheme,
			(value, token) => ExecuteAsync(
				"settings_update_general",
				new { splashTheme = Convert.ToString(value) ?? SplashOptions.DefaultTheme },
				token),
			options: SplashThemeOptions());
		_splashBackground = AddField(
			startup,
			"splashBackground",
			new("启动画面背景", "Splash background"),
			new(
				"Photoshop 作品区与 Android Studio 背景区可使用本地 PNG、JPG、JPEG 或 WebP 图片（最大 16 MiB），清除后保持留白。下次启动时生效。",
				"The Photoshop artwork area and the Android Studio background can use a local PNG, JPG, JPEG or WebP image (up to 16 MiB). Clearing it leaves the area blank. Applies on the next launch."),
			SettingsEditorKind.ImageFile,
			snapshot => SettingsSnapshotReader.String(snapshot, "", "general", "splashBackground"),
			"",
			(value, token) => ExecuteAsync("settings_update_general", new { splashBackground = Convert.ToString(value) ?? "" }, token));
		// 不写成 async lambda：那会变成 async void，异常逃到界面线程。
		_splashBackground.Command = new SettingsCommand(_ => _ = PickBackgroundAsync());
		_splashEnabled.PropertyChanged += OnSplashFieldChanged;
		_splashTheme.PropertyChanged += OnSplashFieldChanged;
		UpdateSplashLinkage();
	}

	/// <inheritdoc />
	internal override void ApplySnapshot(JsonElement snapshot)
	{
		base.ApplySnapshot(snapshot);
		UpdateSplashLinkage();
	}

	/// <inheritdoc />
	protected override void OnDisposed()
	{
		_splashEnabled.PropertyChanged -= OnSplashFieldChanged;
		_splashTheme.PropertyChanged -= OnSplashFieldChanged;
	}

	private void OnSplashFieldChanged(object? sender, PropertyChangedEventArgs args)
	{
		if (args.PropertyName is nameof(SettingsFieldViewModel.Boolean) or nameof(SettingsFieldViewModel.Selected))
			UpdateSplashLinkage();
	}

	/// <summary>关闭启动画面时布局和背景只读；背景只对带作品区的布局显示。</summary>
	private void UpdateSplashLinkage()
	{
		bool enabled = _splashEnabled.Boolean;
		_splashTheme.IsReadOnly = !enabled;
		_splashBackground.IsReadOnly = !enabled;
		_splashBackground.IsVisible = SplashOptions.SupportsBackground(_splashTheme.Selected);
	}

	/// <summary>
	/// 选择对话框只返回路径。写回字段后由字段自己的防抖保存与校验处理；用户取消时不改已有值。
	/// </summary>
	private async Task PickBackgroundAsync()
	{
		try
		{
			JsonElement picked = await ExecuteAsync(
				"settings_pick_splash_background", cancellationToken: LifetimeToken).ConfigureAwait(false);
			if (picked.ValueKind != JsonValueKind.Object
				|| !picked.TryGetProperty("path", out JsonElement pathElement)
				|| pathElement.ValueKind != JsonValueKind.String)
				return;
			string path = pathElement.GetString() ?? "";
			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (!LifetimeToken.IsCancellationRequested) _splashBackground.Text = path;
			});
		}
		catch (OperationCanceledException)
		{
			// 设置窗口关闭时正常取消，不报错。
		}
		catch (Exception exception)
		{
			try { Dispatcher.UIThread.Post(() => SetStatus(exception)); }
			catch (Exception nested) when (nested is not OutOfMemoryException) { }
		}
	}

	private static IReadOnlyList<SettingsOption> AccentOptions()
	{
		SettingsOption[] options = new SettingsOption[NoriAccentPresets.Ids.Count];
		for (int index = 0; index < NoriAccentPresets.Ids.Count; index++)
		{
			string id = NoriAccentPresets.Ids[index];
			options[index] = new SettingsOption(id, AccentLabel(id))
			{
				Swatch = NoriAccentPresets.Value(id, "nori-teal"),
			};
		}
		return options;
	}

	private static SettingsText AccentLabel(string id) => id switch
	{
		"sakura" => new("樱粉", "Sakura"),
		"lavender" => new("薰衣草", "Lavender"),
		"sky" => new("晴空蓝", "Sky blue"),
		"apricot" => new("暖杏", "Apricot"),
		_ => new("品牌青", "Nori teal"),
	};

	private static IReadOnlyList<SettingsOption> ScaleOptions()
	{
		SettingsOption[] options = new SettingsOption[AppearancePreferences.UiScales.Count];
		for (int index = 0; index < AppearancePreferences.UiScales.Count; index++)
		{
			string value = AppearancePreferences.UiScales[index].ToString(CultureInfo.InvariantCulture);
			options[index] = new SettingsOption(value, new(value + "%", value + "%"));
		}
		return options;
	}

	private static IReadOnlyList<SettingsOption> MotionOptions() =>
	[
		new(AppearancePreferences.MotionModes[0], new("跟随系统", "Follow system")),
		new(AppearancePreferences.MotionModes[1], new("减少动效", "Reduce motion")),
		new(AppearancePreferences.MotionModes[2], new("始终开启", "Always on")),
	];

	private static IReadOnlyList<SettingsOption> BubbleFontOptions() =>
	[
		new(AppearancePreferences.BubbleFontRounded, new("圆润", "Rounded")),
		new(AppearancePreferences.BubbleFontInterface, new("界面字体", "Interface")),
		new(AppearancePreferences.BubbleFontSystem, new("系统字体", "System")),
	];

	private static IReadOnlyList<SettingsOption> BubbleSizeOptions() =>
	[
		new(AppearancePreferences.BubbleSizeStandard, new("标准", "Standard")),
		new(AppearancePreferences.BubbleSizeLarge, new("大", "Large")),
	];

	private static IReadOnlyList<SettingsOption> SplashThemeOptions()
	{
		SettingsOption[] options = new SettingsOption[SplashOptions.Themes.Count];
		for (int index = 0; index < SplashOptions.Themes.Count; index++)
		{
			string theme = SplashOptions.Themes[index];
			options[index] = new SettingsOption(theme, SplashThemeLabel(theme));
		}
		return options;
	}

	private static SettingsText SplashThemeLabel(string theme) => theme switch
	{
		"android-studio" => new("Android Studio 布局", "Android Studio layout"),
		"microsoft-365" => new("Microsoft 365 布局", "Microsoft 365 layout"),
		"office-legacy" => new("旧版 Office 布局", "Legacy Office layout"),
		"island" => new("IslandOnTop 顶部条", "IslandOnTop bar"),
		"alls" => new("ALLS 全屏", "ALLS fullscreen"),
		"minimal" => new("简约", "Minimal"),
		_ => new("Photoshop 布局", "Photoshop layout"),
	};
}
