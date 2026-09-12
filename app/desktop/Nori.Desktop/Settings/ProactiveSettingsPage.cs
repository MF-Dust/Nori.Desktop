using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace Nori.Desktop.Settings;

/// <summary>主动互动与提醒设置页。</summary>
public sealed class ProactiveSettingsPage : SettingsPageBase
{
	private readonly SettingsFieldViewModel _newReminderText;
	private readonly SettingsFieldViewModel _delayMinutes;
	private readonly SettingsFieldViewModel _delayPreset;
	private readonly SettingsFieldViewModel _repeatDaily;
	private readonly SettingsFieldViewModel _safeModeNotice;
	private readonly SettingsSectionViewModel _behaviorSection;
	private readonly SettingsCommand _addCommand;
	private readonly SettingsCommand _refreshCommand;
	private bool _safeMode;
	private bool _adding;
	private bool _refreshing;
	private bool _cancelling;
	private long _reminderRevision;
	private readonly SemaphoreSlim _reminderGate = new(1, 1);

	/// <summary>创建主动互动设置页。</summary>
	public ProactiveSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "proactive", "perception", new("主动互动", "Proactive"), new("控制空闲互动、每日问候和提醒。", "Control idle interactions, daily greetings and reminders."), lifetimeToken)
	{
		SettingsSectionViewModel behavior = _behaviorSection = AddSection(new("自动互动", "Automatic interaction"));
		_safeModeNotice = AddField(behavior, "safeModeNotice", new("安全模式", "Safe mode"), new("安全模式下暂停主动互动和新提醒，可继续查看及取消已有提醒。", "Safe mode pauses proactive interaction and new reminders. Existing reminders can still be viewed or cancelled."), SettingsEditorKind.Text,
			_ => string.Empty, string.Empty, (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);
		_safeModeNotice.IsVisible = false;
		AddField(behavior, "idleEnabled", new("空闲互动", "Idle interaction"), new("长时间无操作时允许 Nori 主动说话。", "Allow Nori to speak after a period of inactivity."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "proactive", "idleEnabled"), true,
			(value, token) => SaveBehaviorAsync(new {idleEnabled = Convert.ToBoolean(value)}, token));
		AddField(behavior, "idleMinutes", new("空闲分钟数", "Idle minutes"), new("触发空闲互动前等待的分钟数。", "Minutes before an idle interaction is triggered."), SettingsEditorKind.Number,
			snapshot => SettingsSnapshotReader.Number(snapshot, 15, "proactive", "idleMinutes"), 15,
			(value, token) => SaveBehaviorAsync(new {idleMinutes = Convert.ToDouble(value)}, token), minimum: 1, maximum: 1440, increment: 1);
		AddField(behavior, "dailyGreeting", new("每日问候", "Daily greeting"), new("按本地时间在早晨、午餐和晚间问候。", "Greet at morning, lunchtime and evening in local time."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "proactive", "dailyGreeting"), true,
			(value, token) => SaveBehaviorAsync(new {dailyGreeting = Convert.ToBoolean(value)}, token));
		AddField(behavior, "dailySchedule", new("问候时刻", "Greeting schedule"), new("每日固定时段，应用运行时生效。", "Fixed daily times while the application is running."), SettingsEditorKind.Multiline,
			_ => Text("早安 08:30\n午餐 12:00\n晚安 23:00", "Morning 08:30\nLunch 12:00\nEvening 23:00"), string.Empty, (_, _) => Task.FromResult(default(JsonElement)), readOnly: true);

		SettingsSectionViewModel reminders = AddSection(new("添加提醒", "Add a reminder"));
		_newReminderText = AddField(reminders, "newReminderText", new("提醒内容", "Reminder text"), new("最多 200 个字符，提醒会持久保存并在重启后恢复。", "Up to 200 characters. Reminders are saved and restored after restart."), SettingsEditorKind.Text,
			_ => _newReminderText?.Text ?? string.Empty, string.Empty, (_, _) => Task.FromResult(default(JsonElement)));
		_delayPreset = AddField(reminders, "delayPreset", new("提醒时间", "Remind me in"), new("选择常用时间或输入自定义分钟数。", "Choose a preset or enter a custom delay."), SettingsEditorKind.Choice,
			_ => _delayPreset?.Selected ?? "15", "15", (_, _) => Task.FromResult(default(JsonElement)), options:
			[
				new("5", new("5 分钟", "5 minutes")), new("15", new("15 分钟", "15 minutes")),
				new("30", new("30 分钟", "30 minutes")), new("60", new("1 小时", "1 hour")),
				new("120", new("2 小时", "2 hours")), new("240", new("4 小时", "4 hours")),
				new("1440", new("1 天", "1 day")), new("custom", new("自定义", "Custom")),
			]);
		_delayMinutes = AddField(reminders, "delayMinutes", new("自定义分钟数", "Custom minutes"), new("允许 1 到 43200 分钟。", "Between 1 and 43200 minutes."), SettingsEditorKind.Number,
			_ => _delayMinutes?.Number ?? 15d, 15d, (_, _) => Task.FromResult(default(JsonElement)), minimum: 1, maximum: 43200, increment: 1);
		_delayMinutes.IsVisible = false;
		_delayPreset.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(SettingsFieldViewModel.Selected)) _delayMinutes.IsVisible = _delayPreset.Selected == "custom";
		};
		_repeatDaily = AddField(reminders, "repeatDaily", new("每天重复", "Repeat daily"), new("让提醒每天在相同时间触发。", "Repeat the reminder at the same time every day."), SettingsEditorKind.Boolean,
			_ => _repeatDaily?.Boolean ?? false, false, (_, _) => Task.FromResult(default(JsonElement)));
		_addCommand = new SettingsCommand(_ => _ = AddReminderAsync(), _ => !_safeMode && !_adding && !_refreshing && !_cancelling);
		_refreshCommand = new SettingsCommand(_ => _ = RefreshRemindersAsync(), _ => !_refreshing && !_adding && !_cancelling);
		AddAction(reminders, "addReminder", new("添加提醒", "Add reminder"), new("保存后会立即出现在下方列表。", "The reminder appears in the list after saving."), _addCommand);
		AddAction(reminders, "refreshReminders", new("刷新提醒", "Refresh reminders"), new("从运行时重新读取提醒列表。", "Read reminders from the runtime again."), _refreshCommand);
	}

	/// <summary>当前提醒列表。</summary>
	public ObservableCollection<ReminderItemViewModel> Reminders { get; } = [];

	/// <summary>提醒条目的稳定内容。</summary>
	public sealed record ReminderItemViewModel(string Id, string Content, DateTimeOffset TriggerAt, bool RepeatDaily, string Status);

	internal override void ApplySnapshot(JsonElement snapshot)
	{
		base.ApplySnapshot(snapshot);
		_safeMode = SettingsSnapshotReader.Boolean(snapshot, false, "app", "safeMode");
		_safeModeNotice.IsVisible = _safeMode;
		foreach (SettingsFieldViewModel field in _behaviorSection.Fields)
			if (field.Key is "idleEnabled" or "idleMinutes" or "dailyGreeting") field.IsReadOnly = _safeMode;
		UpdateDraftReadOnly();
		_addCommand.RaiseCanExecuteChanged();
		if (SettingsSnapshotReader.Get(snapshot, "proactive", "reminders") is { } reminders)
		{
			_reminderRevision++;
			ApplyReminders(reminders);
		}
	}

	private void UpdateDraftReadOnly()
	{
		foreach (SettingsFieldViewModel field in new[] {_newReminderText, _delayPreset, _delayMinutes, _repeatDaily})
			field.IsReadOnly = _safeMode || _adding;
	}

	private Task<JsonElement> SaveBehaviorAsync(object patch, CancellationToken cancellationToken)
	{
		if (_safeMode) throw new InvalidOperationException("安全模式下不能修改主动互动设置。");
		return ExecuteAsync("settings_update_proactive", patch, cancellationToken);
	}

	/// <summary>读取宿主毫秒时间戳，不把现代日期误当 Unix 秒。</summary>
	internal static IReadOnlyList<ReminderItemViewModel> ReadReminders(JsonElement value)
	{
		List<ReminderItemViewModel> result = [];
		if (value.ValueKind != JsonValueKind.Array) return result;
		foreach (JsonElement item in value.EnumerateArray())
		{
			string id = SettingsSnapshotReader.String(item, string.Empty, "id");
			if (id.Length == 0) continue;
			long unix = (long)SettingsSnapshotReader.Number(item, SettingsSnapshotReader.Number(item, 0, "triggerAt"), "triggerTime");
			long snoozed = (long)SettingsSnapshotReader.Number(item, 0, "snoozedUntil");
			if (snoozed > 0) unix = snoozed;
			DateTimeOffset trigger;
			try { trigger = DateTimeOffset.FromUnixTimeMilliseconds(unix); }
			catch (ArgumentOutOfRangeException) { continue; }
			result.Add(new(id, SettingsSnapshotReader.String(item, string.Empty, "content"), trigger,
				SettingsSnapshotReader.Boolean(item, false, "repeatDaily"), SettingsSnapshotReader.String(item, string.Empty, "status")));
		}
		return result;
	}

	private void ApplyReminders(JsonElement value)
	{
		IReadOnlyList<ReminderItemViewModel> next = ReadReminders(value);
		if (Reminders.SequenceEqual(next)) return;
		Reminders.Clear();
		foreach (ReminderItemViewModel item in next) Reminders.Add(item);
	}

	internal async Task AddReminderAsync()
	{
		if (_adding || _refreshing || _cancelling || _safeMode) return;
		string content = _newReminderText.Text.Trim();
		double delay = _delayPreset.Selected == "custom" ? _delayMinutes.Number
			: double.TryParse(_delayPreset.Selected, NumberStyles.None, CultureInfo.InvariantCulture, out double preset) ? preset : 0;
		bool repeat = _repeatDaily.Boolean;
		if (content.Length is 0 or > 200)
		{
			SetStatus(Text("提醒内容需要 1 到 200 个字符。", "Enter between 1 and 200 characters."));
			return;
		}
		if (!double.IsFinite(delay) || delay < 1 || delay > 43200)
		{
			SetStatus(Text("延迟时间需要在 1 到 43200 分钟之间。", "The delay must be between 1 and 43200 minutes."));
			return;
		}
		_adding = true;
		UpdateDraftReadOnly();
		RefreshActionAvailability();
		bool entered = false;
		try
		{
			await _reminderGate.WaitAsync(LifetimeToken).ConfigureAwait(true);
			entered = true;
			_reminderRevision++;
			await CreateReminderAsync(
				(command, args, token) => Service.ExecuteAsync(command, args, token),
				content, delay, repeat, LifetimeToken).ConfigureAwait(true);
			_newReminderText.Text = string.Empty;
			_delayPreset.Selected = "15";
			_delayMinutes.Number = 15;
			_repeatDaily.Boolean = false;
			long revision = _reminderRevision;
			JsonElement reminders = await ExecuteAsync("reminder_list", cancellationToken: LifetimeToken).ConfigureAwait(true);
			if (revision == _reminderRevision) ApplyReminders(reminders);
			SetStatus(Text("提醒已添加。", "Reminder added."));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception.Message); }
		finally
		{
			if (entered) _reminderGate.Release();
			_adding = false;
			UpdateDraftReadOnly();
			RefreshActionAvailability();
		}
	}

	/// <summary>重复规则保存失败时撤销新建提醒，避免悄悄留下单次提醒。</summary>
	internal static async Task CreateReminderAsync(
		Func<string, object?, CancellationToken, Task<JsonElement>> execute,
		string content, double delay, bool repeat, CancellationToken cancellationToken)
	{
		JsonElement result = await execute("reminder_add", new {content, delayMinutes = delay}, cancellationToken).ConfigureAwait(false);
		if (!repeat) return;
		string id = SettingsSnapshotReader.String(result, string.Empty, "id");
		if (id.Length == 0) throw new InvalidOperationException("新建提醒没有返回 ID，无法设置每日重复。");
		try { await execute("reminder_update", new {id, repeatDaily = true}, cancellationToken).ConfigureAwait(false); }
		catch
		{
			// 用户取消或窗口关闭时仍尽力撤销，避免保留与用户意图不符的单次提醒。
			try { await execute("reminder_cancel", new {id}, CancellationToken.None).ConfigureAwait(false); }
			catch { }
			throw;
		}
	}

	internal Task RefreshRemindersAsync() =>
		RefreshRemindersAsync(token => ExecuteAsync("reminder_list", cancellationToken: token));

	/// <summary>序列化列表读取，并拒绝覆盖读取期间收到的新快照。</summary>
	internal async Task RefreshRemindersAsync(Func<CancellationToken, Task<JsonElement>> read)
	{
		if (_refreshing || _adding || _cancelling) return;
		_refreshing = true;
		RefreshActionAvailability();
		bool entered = false;
		try
		{
			await _reminderGate.WaitAsync(LifetimeToken).ConfigureAwait(true);
			entered = true;
			long revision = _reminderRevision;
			JsonElement result = await read(LifetimeToken).ConfigureAwait(true);
			if (revision != _reminderRevision) return;
			ApplyReminders(result);
			SetStatus(Text($"已加载 {Reminders.Count} 条提醒。", $"{Reminders.Count} reminders loaded."));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception.Message); }
		finally
		{
			if (entered) _reminderGate.Release();
			_refreshing = false;
			RefreshActionAvailability();
		}
	}

	/// <summary>在界面完成确认后取消提醒，等待先前列表读取结束。</summary>
	internal async Task CancelReminderAsync(ReminderItemViewModel reminder)
	{
		if (_cancelling) return;
		_cancelling = true;
		RefreshActionAvailability();
		bool entered = false;
		try
		{
			await _reminderGate.WaitAsync(LifetimeToken).ConfigureAwait(true);
			entered = true;
			_reminderRevision++;
			await ExecuteAsync("reminder_cancel", new {id = reminder.Id}, LifetimeToken).ConfigureAwait(true);
			for (int index = Reminders.Count - 1; index >= 0; index--)
				if (Reminders[index].Id == reminder.Id) Reminders.RemoveAt(index);
			SetStatus(Text("提醒已取消。", "Reminder cancelled."));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception.Message); }
		finally
		{
			if (entered) _reminderGate.Release();
			_cancelling = false;
			RefreshActionAvailability();
		}
	}

	private void RefreshActionAvailability()
	{
		_addCommand.RaiseCanExecuteChanged();
		_refreshCommand.RaiseCanExecuteChanged();
	}

	internal void ReportActionFailure(Exception exception) => SetStatus(exception.Message);

	private static string Text(string chinese, string english) => SettingsLocalization.IsEnglish ? english : chinese;
}
