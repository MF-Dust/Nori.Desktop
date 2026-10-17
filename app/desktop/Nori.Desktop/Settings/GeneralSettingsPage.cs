using System.Text.Json;

namespace Nori.Desktop.Settings;

/// <summary>界面语言、启动行为与诊断设置页。</summary>
public sealed class GeneralSettingsPage : SettingsPageBase
{
	/// <summary>创建常规设置页。</summary>
	public GeneralSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "general", "app", new("常规", "General"), new("选择语言、启动行为和诊断偏好。", "Choose language, startup behavior and diagnostics preferences."), lifetimeToken)
	{
		SettingsSectionViewModel language = AddSection(new("语言", "Language"));
		AddField(language, "language", new("界面语言", "Interface language"), new("切换后立即更新设置窗口文案。", "The settings window updates immediately after switching."), SettingsEditorKind.Choice,
			snapshot => SettingsSnapshotReader.String(snapshot, "zh-CN", "general", "language"), "zh-CN",
			(value, token) => ExecuteAsync("settings_update_general", new { language = Convert.ToString(value) ?? "zh-CN" }, token),
			options:
			[
				new("zh-CN", new("简体中文", "Simplified Chinese")),
				new("en-US", new("English", "English")),
			]);

		SettingsSectionViewModel appearance = AddSection(new("外观", "Appearance"));
		AddField(appearance, "backgroundBlurEnabled", new("背景模糊", "Background blur"), new("为窗口启用系统背景模糊；不支持时使用深色背景。", "Use system background blur for windows, with a dark background when unavailable."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "general", "backgroundBlurEnabled"), true,
			(value, token) => ExecuteAsync("settings_update_general", new { backgroundBlurEnabled = Convert.ToBoolean(value) }, token));

		SettingsSectionViewModel startup = AddSection(new("启动与窗口", "Startup and window"));
		AddField(startup, "splashEnabled", new("显示启动画面", "Show splash screen"), new("下次启动时生效。", "Applies on the next launch."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "general", "splashEnabled"), true,
			(value, token) => ExecuteAsync("settings_update_general", new { splashEnabled = Convert.ToBoolean(value) }, token));
		AddField(startup, "splashTheme", new("启动画面主题", "Splash theme"), new("参考布局统一使用 Nori 外观，作品区默认留白；下次启动时生效。", "Reference layouts use Nori styling with artwork areas left blank by default. Applies on the next launch."), SettingsEditorKind.Choice,
			snapshot => SettingsSnapshotReader.String(snapshot, "character", "general", "splashTheme"), "character",
			(value, token) => ExecuteAsync("settings_update_general", new { splashTheme = Convert.ToString(value) ?? "character" }, token),
			options:
			[
				new("character", new("Photoshop 布局", "Photoshop layout")),
				new("android-studio", new("Android Studio 布局", "Android Studio layout")),
				new("microsoft-365", new("Microsoft 365 布局", "Microsoft 365 layout")),
				new("office-legacy", new("旧版 Office 布局", "Legacy Office layout")),
				new("island", new("IslandOnTop 顶部条", "IslandOnTop bar")),
				new("alls", new("ALLS 全屏", "ALLS fullscreen")),
				new("minimal", new("简约", "Minimal")),
			]);
		AddField(startup, "splashBackground", new("启动画面背景", "Splash background"), new("Photoshop 作品区 / Android Studio 背景：本地 PNG/JPG/JPEG/WebP 绝对路径（最大 16 MiB），不支持网络路径；留空保持空白。下次启动时生效。", "Photoshop artwork / Android Studio background: absolute path to a local PNG/JPG/JPEG/WebP image (up to 16 MiB). No network paths. Leave empty for a blank panel. Applies on the next launch."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, "", "general", "splashBackground"), "",
			(value, token) => ExecuteAsync("settings_update_general", new { splashBackground = Convert.ToString(value) ?? "" }, token));
		AddField(startup, "petAutoSummon", new("启动时显示伴侣", "Show pet on startup"), new("启动后自动显示桌面伴侣窗口。", "Show the desktop pet automatically after startup."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "general", "petAutoSummon"), true,
			(value, token) => ExecuteAsync("settings_update_general", new { petAutoSummon = Convert.ToBoolean(value) }, token));
		AddField(startup, "quickChatEnabled", new("快捷聊天", "Quick Chat"), new("显示跟随伴侣的快捷输入栏，并使用上半身构图。关闭后恢复普通桌宠。", "Show a quick input bar beside the pet with an upper-body view. Turn off to restore the ordinary pet."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "general", "quickChatEnabled"), true,
			(value, token) => ExecuteAsync("settings_update_general", new { quickChatEnabled = Convert.ToBoolean(value) }, token));
		AddField(startup, "clickThrough", new("点击穿透", "Click through"), new("启用后可让鼠标穿过伴侣窗口。", "Allow the pointer to pass through the pet window."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, false, "behaviors", "clickThrough"), false,
			(value, token) => ExecuteAsync("model_set_behavior", new { clickThrough = Convert.ToBoolean(value) }, token));

		AddField(startup, "hitThroughSupport", new("平台支持", "Platform support"), new("", ""), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, false, "platform", "supportsHitThrough")
				? "" : Localized(snapshot, "当前桌面环境不支持点击穿透。", "Click through is unavailable in this desktop environment."),
			"", (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);

		SettingsSectionViewModel diagnostic = AddSection(new("诊断", "Diagnostics"));
		AddField(diagnostic, "telemetryEnabled", new("发送匿名诊断", "Send anonymous diagnostics"), new("只发送经过脱敏的崩溃和性能信息。", "Only redacted crash and performance data is sent."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, false, "telemetry", "enabled"), false,
			(value, token) => ExecuteAsync("settings_update_general", new { telemetryEnabled = Convert.ToBoolean(value) }, token));
		AddField(diagnostic, "telemetryStatus", new("诊断状态", "Diagnostics status"), new("", ""), SettingsEditorKind.Text,
			TelemetryStatus, "", (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
	}
	/// <inheritdoc />
	internal override void ApplySnapshot(JsonElement snapshot)
	{
		base.ApplySnapshot(snapshot);
		bool supported = SettingsSnapshotReader.Boolean(snapshot, false, "platform", "supportsHitThrough");
		SettingsFieldViewModel[] fields = Sections.SelectMany(section => section.Fields).ToArray();
		fields.Single(field => field.Key == "clickThrough").IsReadOnly = !supported;
		fields.Single(field => field.Key == "hitThroughSupport").IsVisible = !supported;
	}

	private static string TelemetryStatus(JsonElement snapshot)
	{
		if (!SettingsSnapshotReader.Boolean(snapshot, false, "telemetry", "available"))
			return Localized(snapshot, "当前构建未配置诊断服务。", "Diagnostics are not available in this build.");
		if (SettingsSnapshotReader.String(snapshot, "unset", "telemetry", "consent") == "unset")
			return Localized(snapshot, "等待你的选择，尚未发送诊断数据。", "Awaiting your choice; diagnostics have not been enabled.");
		return SettingsSnapshotReader.Boolean(snapshot, false, "telemetry", "enabled")
			? Localized(snapshot, "已开启匿名诊断。", "Anonymous diagnostics are enabled.")
			: Localized(snapshot, "已关闭匿名诊断。", "Anonymous diagnostics are disabled.");
	}

	private static string Localized(JsonElement snapshot, string chinese, string english) =>
		new SettingsText(chinese, english).Resolve(SettingsSnapshotReader.String(snapshot, "zh-CN", "general", "language"));

}
