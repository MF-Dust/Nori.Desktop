using System.Collections.ObjectModel;
using System.Text.Json;

namespace Nori.Desktop.Settings;

/// <summary>主动互动与提醒设置页。</summary>
public sealed class ProactiveSettingsPage : SettingsPageBase
{
	private readonly SettingsFieldViewModel _newReminderText;
	private readonly SettingsFieldViewModel _delayMinutes;
	private readonly SettingsFieldViewModel _repeatDaily;
	private readonly SettingsSectionViewModel _remindersSection;

	/// <summary>创建主动互动设置页。</summary>
	public ProactiveSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "proactive", "perception", new("主动互动", "Proactive"), new("控制空闲互动、每日问候和提醒。", "Control idle interactions, daily greetings and reminders."), lifetimeToken)
	{
		SettingsSectionViewModel behavior = AddSection(new("自动互动", "Automatic interaction"));
		AddField(behavior, "idleEnabled", new("空闲互动", "Idle interaction"), new("长时间无操作时允许 Nori 主动说话。", "Allow Nori to speak after a period of inactivity."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "proactive", "idleEnabled"), true,
			(value, token) => ExecuteAsync("settings_update_proactive", new { idleEnabled = Convert.ToBoolean(value) }, token));
		AddField(behavior, "idleMinutes", new("空闲分钟数", "Idle minutes"), new("触发空闲互动前等待的分钟数。", "Minutes before an idle interaction is triggered."), SettingsEditorKind.Number,
			snapshot => SettingsSnapshotReader.Number(snapshot, 15, "proactive", "idleMinutes"), 15,
			(value, token) => ExecuteAsync("settings_update_proactive", new { idleMinutes = Convert.ToDouble(value) }, token),
			minimum: 1, maximum: 1440, increment: 1);
		AddField(behavior, "dailyGreeting", new("每日问候", "Daily greeting"), new("每天首次启动时发送问候。", "Send a greeting on the first activity of each day."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "proactive", "dailyGreeting"), true,
			(value, token) => ExecuteAsync("settings_update_proactive", new { dailyGreeting = Convert.ToBoolean(value) }, token));

		_remindersSection = AddSection(new("提醒", "Reminders"));
		_newReminderText = AddField(_remindersSection, "newReminderText", new("提醒内容", "Reminder text"), new("最多 200 个字符。", "Up to 200 characters."), SettingsEditorKind.Text,
			_ => string.Empty, string.Empty, (_, _) => Task.FromResult(default(JsonElement)));
		_delayMinutes = AddField(_remindersSection, "delayMinutes", new("延迟分钟数", "Delay minutes"), new("从现在起等待的分钟数。", "Minutes from now."), SettingsEditorKind.Number,
			_ => 15d, 15d, (_, _) => Task.FromResult(default(JsonElement)), minimum: 1, maximum: 43200, increment: 1);
		_repeatDaily = AddField(_remindersSection, "repeatDaily", new("每天重复", "Repeat daily"), new("让提醒每天在相同时间触发。", "Repeat the reminder at the same time every day."), SettingsEditorKind.Boolean,
			_ => false, false, (_, _) => Task.FromResult(default(JsonElement)));
		AddAction(_remindersSection, "addReminder", new("添加提醒", "Add reminder"), new("保存后会立即出现在下方列表。", "The reminder appears in the list after saving."), new SettingsCommand(_ => _ = AddReminderAsync()));
		AddAction(_remindersSection, "refreshReminders", new("刷新提醒", "Refresh reminders"), new("从运行时重新读取提醒列表。", "Read reminders from the runtime again."), new SettingsCommand(_ => _ = RefreshRemindersAsync()));
	}

	/// <summary>当前提醒列表。</summary>
	public ObservableCollection<ReminderItemViewModel> Reminders { get; } = [];

	/// <summary>提醒条目。</summary>
	public sealed class ReminderItemViewModel : SettingsObservableObject
	{
		private string _status;

		/// <summary>创建提醒条目。</summary>
		public ReminderItemViewModel(string id, string content, DateTimeOffset triggerAt, bool repeatDaily, string status)
		{
			Id = id;
			Content = content;
			TriggerAt = triggerAt;
			RepeatDaily = repeatDaily;
			_status = status;
		}

		/// <summary>提醒 ID。</summary>
		public string Id { get; }
		/// <summary>提醒内容。</summary>
		public string Content { get; }
		/// <summary>触发时间。</summary>
		public DateTimeOffset TriggerAt { get; }
		/// <summary>是否每日重复。</summary>
		public bool RepeatDaily { get; }
		/// <summary>提醒状态。</summary>
		public string Status { get => _status; private set => SetProperty(ref _status, value); }
		/// <summary>取消提醒。</summary>
		public SettingsCommand? CancelCommand { get; internal set; }
	}

	private async Task AddReminderAsync()
	{
		string content = _newReminderText.Text.Trim();
		if (content.Length == 0)
		{
			SetStatus("请输入提醒内容。 / Enter reminder text.");
			return;
		}
		try
		{
			JsonElement result = await ExecuteAsync("reminder_add", new
			{
				content,
				delayMinutes = _delayMinutes.Number,
			}, LifetimeToken).ConfigureAwait(false);
			if (_repeatDaily.Boolean && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("id", out JsonElement id))
				await ExecuteAsync("reminder_update", new { id = id.GetString(), repeatDaily = true }, LifetimeToken).ConfigureAwait(false);
			_newReminderText.Text = string.Empty;
			SetStatus("提醒已添加。 / Reminder added.");
			await RefreshRemindersAsync().ConfigureAwait(false);
		}
		catch (Exception exception) { SetStatus(exception.Message); }
	}

	private async Task RefreshRemindersAsync()
	{
		try
		{
			JsonElement result = await ExecuteAsync("reminder_list", cancellationToken: LifetimeToken).ConfigureAwait(false);
			Reminders.Clear();
			if (result.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in result.EnumerateArray())
				{
					string id = SettingsSnapshotReader.String(item, string.Empty, "id");
					string content = SettingsSnapshotReader.String(item, string.Empty, "content");
					long unix = (long)SettingsSnapshotReader.Number(item, 0, "triggerTime");
					DateTimeOffset trigger = unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.Now;
					bool repeat = SettingsSnapshotReader.Boolean(item, false, "repeatDaily");
					string status = SettingsSnapshotReader.String(item, string.Empty, "status");
					ReminderItemViewModel reminder = new(id, content, trigger, repeat, status);
					reminder.CancelCommand = new SettingsCommand(_ => _ = CancelReminderAsync(reminder));
					Reminders.Add(reminder);
				}
			}
			SetStatus($"已加载 {Reminders.Count} 条提醒。 / {Reminders.Count} reminders loaded.");
		}
		catch (Exception exception) { SetStatus(exception.Message); }
	}

	private async Task CancelReminderAsync(ReminderItemViewModel reminder)
	{
		try
		{
			await ExecuteAsync("reminder_cancel", new { id = reminder.Id }, LifetimeToken).ConfigureAwait(false);
			Reminders.Remove(reminder);
			SetStatus("提醒已取消。 / Reminder cancelled.");
		}
		catch (Exception exception) { SetStatus(exception.Message); }
	}
}
