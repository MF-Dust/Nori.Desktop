namespace Nori.Desktop.Settings;

/// <summary>界面语言、启动行为与隐私设置页。</summary>
public sealed class GeneralSettingsPage : SettingsPageBase
{
	/// <summary>创建常规设置页。</summary>
	public GeneralSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "general", "system", new("常规", "General"), new("选择语言、启动行为和诊断偏好。", "Choose language, startup behavior and diagnostics preferences."), lifetimeToken)
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

		SettingsSectionViewModel startup = AddSection(new("启动与窗口", "Startup and window"));
		AddField(startup, "petAutoSummon", new("启动时显示伴侣", "Show pet on startup"), new("启动后自动显示桌面伴侣窗口。", "Show the desktop pet automatically after startup."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "general", "petAutoSummon"), true,
			(value, token) => ExecuteAsync("settings_update_general", new { petAutoSummon = Convert.ToBoolean(value) }, token));
		AddField(startup, "clickThrough", new("点击穿透", "Click through"), new("启用后可让鼠标穿过伴侣窗口。", "Allow the pointer to pass through the pet window."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, false, "behaviors", "clickThrough"), false,
			(value, token) => ExecuteAsync("model_set_behavior", new { clickThrough = Convert.ToBoolean(value) }, token));

		SettingsSectionViewModel privacy = AddSection(new("诊断与更新", "Diagnostics and updates"));
		AddField(privacy, "telemetryEnabled", new("发送匿名诊断", "Send anonymous diagnostics"), new("只发送经过脱敏的崩溃和性能信息。", "Only redacted crash and performance data is sent."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, false, "telemetry", "enabled"), false,
			(value, token) => ExecuteAsync("settings_update_general", new { telemetryEnabled = Convert.ToBoolean(value) }, token));
		AddField(privacy, "autoCheckUpdates", new("自动检查更新", "Check for updates automatically"), new("启动后一段时间和每天定期检查稳定版更新。", "Check for stable releases after startup and periodically."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "general", "autoCheckUpdates"), true,
			(value, token) => ExecuteAsync("settings_update_general", new { autoCheckUpdates = Convert.ToBoolean(value) }, token));
	}
}
