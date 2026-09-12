using System.Text.Json;

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

		AddField(startup, "hitThroughSupport", new("平台支持", "Platform support"), new("", ""), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, false, "platform", "supportsHitThrough")
				? "" : Localized(snapshot, "当前桌面环境不支持点击穿透。", "Click through is unavailable in this desktop environment."),
			"", (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);

		SettingsSectionViewModel privacy = AddSection(new("诊断与更新", "Diagnostics and updates"));
		AddField(privacy, "telemetryEnabled", new("发送匿名诊断", "Send anonymous diagnostics"), new("只发送经过脱敏的崩溃和性能信息。", "Only redacted crash and performance data is sent."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, false, "telemetry", "enabled"), false,
			(value, token) => ExecuteAsync("settings_update_general", new { telemetryEnabled = Convert.ToBoolean(value) }, token));
		AddField(privacy, "telemetryStatus", new("诊断状态", "Diagnostics status"), new("", ""), SettingsEditorKind.Text,
			TelemetryStatus, "", (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
		AddField(privacy, "autoCheckUpdates", new("自动检查更新", "Check for updates automatically"), new("启动后一段时间和每天定期检查稳定版更新。", "Check for stable releases after startup and periodically."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "general", "autoCheckUpdates"), true,
			(value, token) => ExecuteAsync("settings_update_general", new { autoCheckUpdates = Convert.ToBoolean(value) }, token));
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
