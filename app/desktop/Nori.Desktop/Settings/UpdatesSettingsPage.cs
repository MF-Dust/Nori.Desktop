using System.Text.Json;
using Nori.Desktop.Settings.Pages;

namespace Nori.Desktop.Settings;

/// <summary>软件更新信息、下载进度和安装动作。</summary>
public sealed class UpdatesSettingsPage : SettingsPageBase
{
	private readonly Dictionary<string, SettingsFieldViewModel> _fields = new(StringComparer.Ordinal);
	private string _state = "idle";
	private string _manualUrl = "";
	private bool _available;
	private bool _pending;
	private bool _cancelPending;

	/// <summary>创建更新设置页。</summary>
	public UpdatesSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "updates", "system", new("软件更新", "Updates"), new("查看最新版本与发布说明，管理下载和安装。", "View releases and manage downloads and installation."), lifetimeToken)
	{
		SettingsSectionViewModel version = AddSection(new("版本与更新", "Version and updates"));
		Read(version, "currentVersion", new("当前版本", "Current version"), snapshot => SettingsSnapshotReader.String(snapshot, "Dev", "app", "productVersion"));
		Read(version, "lastCheckedAt", new("上次检查", "Last checked"), snapshot => Value(snapshot, "lastCheckedAt"));
		Read(version, "state", new("更新状态", "Update status"), snapshot => StateLabel(Value(snapshot, "state")));
		Read(version, "unavailableReason", new("更新不可用", "Updates unavailable"), snapshot => Value(snapshot, "unavailableReason"));
		_fields["autoCheck"] = AddField(version, "autoCheck", new("自动检查更新", "Check automatically"),
			new("启动后与每天定期检查稳定版。", "Check stable releases after startup and periodically."),
			SettingsEditorKind.Boolean, snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "general", "autoCheckUpdates"), true,
			(value, token) => ExecuteAsync("settings_update_general", new {autoCheckUpdates = Convert.ToBoolean(value)}, token));
		Action(version, "check", new("立即检查", "Check now"), new("", ""), () => RunAsync("updater_check", CanCheck));
		Action(version, "manual", new("手动下载", "Download manually"),
			new("在默认浏览器中打开宿主提供的下载页面。", "Open the download page supplied by the host."),
			() => RunAsync("open_url", _manualUrl.Length > 0, new {url = _manualUrl}));

		SettingsSectionViewModel release = AddSection(new("可用版本", "Available release"));
		Read(release, "availableVersion", new("新版本", "New version"), snapshot => Value(snapshot, "availableVersion"));
		Read(release, "releaseNotes", new("发布说明", "Release notes"), snapshot => Value(snapshot, "releaseNotes"), multiline: true);
		_fields["progress"] = AddField(release, "progress", new("下载进度", "Download progress"), new("", ""),
			SettingsEditorKind.Progress, snapshot => Math.Clamp(SettingsJson.Double(SettingsJson.Object(snapshot, "updater"), "progress") * 100, 0, 100),
			0d, (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
		Read(release, "downloadSize", new("已下载", "Downloaded"), snapshot =>
		{
			JsonElement updater = SettingsJson.Object(snapshot, "updater");
			return $"{SettingsJson.Long(updater, "downloadedBytes") / 1048576d:F1} / {SettingsJson.Long(updater, "totalBytes") / 1048576d:F1} MiB";
		});
		Read(release, "message", new("详细状态", "Details"), snapshot => Value(snapshot, "message"), multiline: true);
		Read(release, "installNotice", new("安装提示", "Installation note"), _ => Text(
			"更新会下载并校验到独立目录，提交阶段不可取消；完成后重启生效。",
			"The update is downloaded and verified in a separate directory. The commit stage cannot be cancelled; restart to apply."));
		Action(release, "install", new("下载并安装", "Download and install"), new("", ""), () => RunAsync("updater_install", CanInstall));
		Action(release, "cancel", new("取消更新", "Cancel update"), new("", ""), CancelAsync);
		Action(release, "restart", new("立即重启", "Restart now"), new("", ""), () => RunAsync("updater_restart", CanRestart));
		UpdateVisibility();
	}

	private bool Busy => _pending || _state is "checking" or "downloading" or "verifying" or "installing";
	private bool CanCheck => _available && !Busy && _state != "readytorestart";
	private bool CanInstall => _available && !_pending && _state == "available";
	private bool CanRestart => !_pending && _state == "readytorestart";

	/// <inheritdoc />
	internal override void ApplySnapshot(JsonElement snapshot)
	{
		_state = Value(snapshot, "state").ToLowerInvariant();
		_manualUrl = Value(snapshot, "manualDownloadUrl");
		_available = SettingsJson.Object(snapshot, "updater").ValueKind == JsonValueKind.Object && Value(snapshot, "unavailableReason").Length == 0;
		base.ApplySnapshot(snapshot);
		UpdateVisibility();
	}

	private void UpdateVisibility()
	{
		if (_fields.Count == 0) return;
		_fields["check"].IsReadOnly = !CanCheck;
		_fields["autoCheck"].IsReadOnly = !_available;
		_fields["install"].IsVisible = _state == "available";
		_fields["install"].IsReadOnly = !CanInstall;
		_fields["restart"].IsVisible = _state == "readytorestart";
		_fields["restart"].IsReadOnly = !CanRestart;
		_fields["cancel"].IsVisible = Busy;
		_fields["cancel"].IsReadOnly = _cancelPending || _state == "installing";
		_fields["progress"].IsVisible = _state == "downloading";
		_fields["downloadSize"].IsVisible = _state == "downloading";
		_fields["manual"].IsVisible = _manualUrl.Length > 0;
		_fields["lastCheckedAt"].IsVisible = _fields["lastCheckedAt"].Text.Length > 0;
		_fields["unavailableReason"].IsVisible = !_available;
		_fields["availableVersion"].IsVisible = _fields["availableVersion"].Text.Length > 0;
		_fields["releaseNotes"].IsVisible = _fields["releaseNotes"].Text.Length > 0;
		_fields["message"].IsVisible = _fields["message"].Text.Length > 0;
		_fields["installNotice"].IsVisible = _state is "available" or "downloading" or "verifying" or "installing" or "readytorestart";
		Sections[1].IsVisible = Sections[1].Fields.Any(field => field.IsVisible);
	}

	private async Task RunAsync(string command, bool allowed, object? args = null)
	{
		if (!allowed) return;
		_pending = true;
		UpdateVisibility();
		try
		{
			await ExecuteAsync(command, args, LifetimeToken).ConfigureAwait(true);
			SetStatus("");
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception.Message); }
		finally
		{
			_pending = false;
			UpdateVisibility();
		}
	}

	private async Task CancelAsync()
	{
		if (!Busy || _cancelPending || _state == "installing") return;
		_cancelPending = true;
		UpdateVisibility();
		try { await ExecuteAsync("updater_cancel", cancellationToken: LifetimeToken).ConfigureAwait(true); }
		catch (Exception exception) { SetStatus(exception.Message); }
		finally { _cancelPending = false; UpdateVisibility(); }
	}

	private void Read(SettingsSectionViewModel section, string key, SettingsText label, Func<JsonElement, object?> read, bool multiline = false) =>
		_fields[key] = AddField(section, key, label, new("", ""), multiline ? SettingsEditorKind.Multiline : SettingsEditorKind.Text,
			read, "", (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);

	private void Action(SettingsSectionViewModel section, string key, SettingsText label, SettingsText description, Func<Task> action) =>
		_fields[key] = AddAction(section, key, label, description, new SettingsCommand(_ => _ = action()));

	private static string Value(JsonElement snapshot, string name) => SettingsSnapshotReader.String(snapshot, "", "updater", name);
	private static string Text(string chinese, string english) => SettingsLocalization.IsEnglish ? english : chinese;
	private static string StateLabel(string state) => state.ToLowerInvariant() switch
	{
		"checking" => Text("正在检查", "Checking"),
		"available" => Text("发现新版本", "Update available"),
		"uptodate" => Text("已是最新版本", "Up to date"),
		"downloading" => Text("正在下载", "Downloading"),
		"verifying" => Text("正在校验", "Verifying"),
		"installing" => Text("正在安装", "Installing"),
		"readytorestart" => Text("更新就绪，重启后生效", "Ready to restart"),
		"cancelled" => Text("已取消", "Cancelled"),
		"error" => Text("更新失败", "Update failed"),
		_ => Text("尚未检查", "Not checked yet"),
	};
}
