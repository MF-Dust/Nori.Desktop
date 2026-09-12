using System.Text.Json;

namespace Nori.Desktop.Settings;

/// <summary>软件更新设置页。</summary>
public sealed class UpdatesSettingsPage : SettingsPageBase
{
	/// <summary>创建更新设置页。</summary>
	public UpdatesSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "updates", "system", new("软件更新", "Updates"), new("检查稳定版更新并管理安装进度。", "Check stable releases and manage installation progress."), lifetimeToken)
	{
		SettingsSectionViewModel status = AddSection(new("当前版本", "Current version"));
		AddField(status, "currentVersion", new("当前版本", "Current version"), new("正在运行的 Nori 版本。", "The Nori version currently running."), SettingsEditorKind.Text,
			snapshot => FirstString(snapshot, "Dev", ["app", "productVersion"], ["app", "appVersion"]), "Dev",
			(_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
		AddField(status, "state", new("更新状态", "Update status"), new("检查、下载和安装会在后台进行。", "Checks, downloads and installation run in the background."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, "unknown", "updater", "state"), "unknown",
			(_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
		AddField(status, "autoCheckUpdates", new("自动检查更新", "Check automatically"), new("启动后一段时间和每天定期检查稳定版。", "Check for stable releases after startup and periodically."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "general", "autoCheckUpdates"), true,
			(value, token) => ExecuteAsync("settings_update_general", new { autoCheckUpdates = Convert.ToBoolean(value) }, token));

		AddAction(status, "check", new("立即检查", "Check now"), new("检查 GitHub 稳定版发布。", "Check stable GitHub releases now."), new SettingsCommand(_ => _ = CheckAsync()));
		AddAction(status, "install", new("下载并安装", "Download and install"), new("发现新版本后执行安装。", "Install the available release."), new SettingsCommand(_ => _ = InstallAsync()));
		AddAction(status, "cancel", new("取消更新", "Cancel update"), new("取消当前检查或安装任务。", "Cancel the current check or installation."), new SettingsCommand(_ => _ = CancelAsync()));
		AddAction(status, "restart", new("立即重启", "Restart now"), new("应用已准备好更新时重启 Nori。", "Restart Nori when an update is ready."), new SettingsCommand(_ => _ = RestartAsync()));
	}

	private async Task CheckAsync()
	{
		try
		{
			await ExecuteAsync("updater_check", cancellationToken: LifetimeToken).ConfigureAwait(false);
			SetStatus("检查完成。 / Update check complete.");
		}
		catch (Exception exception) { SetStatus(exception.Message); }
	}

	private async Task InstallAsync()
	{
		try
		{
			await ExecuteAsync("updater_install", cancellationToken: LifetimeToken).ConfigureAwait(false);
			SetStatus("安装任务已开始。 / Installation started.");
		}
		catch (Exception exception) { SetStatus(exception.Message); }
	}

	private async Task CancelAsync()
	{
		try
		{
			await ExecuteAsync("updater_cancel", cancellationToken: LifetimeToken).ConfigureAwait(false);
			SetStatus("更新已取消。 / Update cancelled.");
		}
		catch (Exception exception) { SetStatus(exception.Message); }
	}

	private async Task RestartAsync()
	{
		try
		{
			await ExecuteAsync("updater_restart", cancellationToken: LifetimeToken).ConfigureAwait(false);
			SetStatus("正在重启。 / Restarting.");
		}
		catch (Exception exception) { SetStatus(exception.Message); }
	}

	private static string FirstString(JsonElement root, string fallback, params string[][] paths)
	{
		foreach (string[] path in paths)
		{
			string value = SettingsSnapshotReader.String(root, string.Empty, path);
			if (value.Length > 0) return value;
		}
		return fallback;
	}
}
