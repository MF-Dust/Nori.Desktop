using System.Text.Json;

namespace Nori.Desktop.Settings;

/// <summary>
/// Nori 账户与云端同步。
///
/// ── 这一页为什么不把同步窗口整个搬进来 ────────────────────────────────────
/// 备份与恢复是两个一次性动作，放在设置里顺手；而**删除云端存档**和**冲突时二选一**
/// 都需要先看清云端那份是什么时候的、再做一个不可逆或有取舍的决定 —— 那是一整屏
/// 信息，塞进一行设置项只会让人在看不全的情况下按下去。它们留在同步窗口里，这一页
/// 给一个入口。
///
/// ── 为什么这一页显示的是「本机记的版本号」而不是云端状态 ──────────────────
/// 云端状态要发网络请求才知道。设置快照是同步构建且带缓存的，在那里发请求会让每次
/// 界面刷新都挂在网络上，断网时整个设置窗口转圈。所以这一页只显示本机侧的事实，
/// 云端那一侧由同步窗口按需去取。
/// </summary>
public sealed class AccountSettingsPage : SettingsPageBase
{
	private readonly Dictionary<string, SettingsFieldViewModel> _fields = new(StringComparer.Ordinal);
	private bool _signedIn;
	private bool _busy;
	private bool _hasResult;

	/// <summary>创建账户设置页。</summary>
	public AccountSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "account", "app",
			new("账户与同步", "Account & sync"),
			new("登录 Nori 账户，在多台设备之间同步偏好、记忆与提醒。", "Sign in to sync preferences, memories and reminders across devices."),
			lifetimeToken)
	{
		SettingsSectionViewModel account = AddSection(new("账户", "Account"));
		_fields["status"] = Read(account, "status", new("登录状态", "Status"), StatusLine);
		_fields["signIn"] = AddAction(account, "signIn", new("登录", "Sign in"),
			new("使用邮箱验证码或密码登录。", "Sign in with an email code or a password."),
			new SettingsCommand(_ => Fire("account_open"), _ => !_busy));
		_fields["signOut"] = AddAction(account, "signOut", new("退出登录", "Sign out"),
			new("清除本机保存的登录凭据。云端存档不受影响。", "Clears the credentials stored on this device. The cloud save is unaffected."),
			new SettingsCommand(_ => Fire("account_sign_out"), _ => !_busy));

		SettingsSectionViewModel sync = AddSection(new("云端同步", "Cloud sync"));
		Read(sync, "scope", new("同步范围", "What is synced"), _ => Text(
			"偏好设置、她记住的事、以及未完成的提醒。不含对话原文，也不含任何密钥。",
			"Preferences, her memories, and pending reminders. Conversation transcripts and secrets are never included."));
		_fields["revision"] = Read(sync, "revision", new("本机同步进度", "This device"), RevisionLine);
		_fields["backup"] = AddAction(sync, "backup", new("备份到云端", "Back up now"),
			new("把本机当前的数据打包上传。", "Package and upload the current data on this device."),
			new SettingsCommand(_ => Fire("cloud_backup"), _ => _signedIn && !_busy));
		_fields["restore"] = AddAction(sync, "restore", new("从云端恢复", "Restore from cloud"),
			new("合并到本机：偏好覆盖，记忆与提醒只增不删。", "Merged into this device: preferences are overwritten; memories and reminders are only added."),
			new SettingsCommand(_ => Fire("cloud_restore"), _ => _signedIn && !_busy));
		_fields["manage"] = AddAction(sync, "manage", new("查看云端存档", "Open cloud sync"),
			new("查看云端那一份的时间与大小，处理冲突，或删除它。", "See what is stored, resolve conflicts, or delete it."),
			new SettingsCommand(_ => Fire("cloud_open"), _ => _signedIn && !_busy));
		_fields["result"] = Read(sync, "result", new("最近一次操作", "Last operation"),
			snapshot => SettingsSnapshotReader.String(snapshot, "", "account", "lastSyncMessage"), multiline: true);
		// 设置页的字段只渲染文本，放不下标识图 —— 三扇原生窗口上是图文并排的那一版。
		// 这里只留字，但出处一样要说：这一页管的账户与存档都在 NCN 上。
		Read(sync, "poweredBy", new("服务提供方", "Service provider"),
			_ => Account.PoweredByNcn.Brand);

		UpdateVisibility();
	}

	/// <summary>只读字段：值全部来自快照，保存函数是空操作。</summary>
	private SettingsFieldViewModel Read(
		SettingsSectionViewModel section, string key, SettingsText label,
		Func<JsonElement, object?> read, bool multiline = false) =>
		AddField(section, key, label, new("", ""),
			multiline ? SettingsEditorKind.Multiline : SettingsEditorKind.Text,
			read, "", (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);

	private static string Text(string chinese, string english) =>
		SettingsLocalization.IsEnglish ? english : chinese;

	/// <inheritdoc />
	internal override void ApplySnapshot(JsonElement snapshot)
	{
		_signedIn = SettingsSnapshotReader.Boolean(snapshot, false, "account", "signedIn");
		// 还没做过任何操作时不要摆一个空的「最近一次操作」—— 空着的一行只是噪声，
		// 而且会让人以为哪里出错了。
		_hasResult = SettingsSnapshotReader.String(snapshot, "", "account", "lastSyncMessage").Length > 0;
		base.ApplySnapshot(snapshot);
		UpdateVisibility();
	}

	private string StatusLine(JsonElement snapshot)
	{
		if (!SettingsSnapshotReader.Boolean(snapshot, true, "account", "available"))
		{
			// 密钥库不可用。说出来，否则用户只看到「未登录」，登录一次还是「未登录」。
			return Localized(snapshot, "本机密钥库不可用，无法保存登录凭据。",
				"The platform key store is unavailable, so credentials cannot be saved.");
		}
		string email = SettingsSnapshotReader.String(snapshot, "", "account", "email");
		return SettingsSnapshotReader.Boolean(snapshot, false, "account", "signedIn") && email.Length > 0
			? email
			: Localized(snapshot, "未登录。不登录也能完整使用本机功能。",
				"Not signed in. Everything on this device works without an account.");
	}

	private static string RevisionLine(JsonElement snapshot)
	{
		int revision = (int)SettingsSnapshotReader.Number(snapshot, 0, "account", "cloudRevision");
		return revision > 0
			? Localized(snapshot, $"已同步到云端第 {revision} 版。", $"Synced with cloud revision {revision}.")
			: Localized(snapshot, "这台设备还没有同步过。", "This device has not synced yet.");
	}

	/// <summary>
	/// 跑一个桥命令，把结果落到「最近一次操作」那一行。
	///
	/// 动作期间锁住全部按钮：备份与恢复同时在跑会让版本号的判断失去意义。
	/// </summary>
	private async void Fire(string command)
	{
		if (_busy) return;
		_busy = true;
		RaiseCanExecuteChanged();
		try
		{
			// 结果不从这里取：命令会把它写进快照，刷新到来时那一行自己就变了。
			// 见 AppRuntime.LastCloudSyncMessage 的说明。
			await ExecuteAsync(command);
		}
		catch (OperationCanceledException)
		{
			// 窗口关掉了。没有可展示的地方，也不该当成失败。
		}
		catch (Exception error)
		{
			// 走到这里的是命令本身没跑起来（被白名单挡了、运行时还没就绪）。
			// 预期内的同步失败不会抛 —— 它们已经写进快照那一行了。
			SetStatus(error.Message);
		}
		finally
		{
			_busy = false;
			RaiseCanExecuteChanged();
		}
	}

	private void RaiseCanExecuteChanged()
	{
		foreach (SettingsFieldViewModel field in _fields.Values) (field.Command as SettingsCommand)?.RaiseCanExecuteChanged();
	}

	/// <summary>登录与退出互斥显示 —— 同时摆着两个，其中一个必定是无效的。</summary>
	private void UpdateVisibility()
	{
		if (_fields.TryGetValue("signIn", out SettingsFieldViewModel? signIn)) signIn.IsVisible = !_signedIn;
		if (_fields.TryGetValue("signOut", out SettingsFieldViewModel? signOut)) signOut.IsVisible = _signedIn;
		if (_fields.TryGetValue("result", out SettingsFieldViewModel? result)) result.IsVisible = _hasResult;
		RaiseCanExecuteChanged();
	}

	private static string Localized(JsonElement snapshot, string chinese, string english) =>
		new SettingsText(chinese, english).Resolve(SettingsSnapshotReader.String(snapshot, "zh-CN", "general", "language"));
}
