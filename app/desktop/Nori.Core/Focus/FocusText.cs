namespace Nori.Core.Focus;

/// <summary>设置页和快照共用的专注文案。计时数字来自单调时钟，不在这里重算。</summary>
public static class FocusText
{
	/// <summary>完成通知只准备这一句。宿主决定用气泡还是连同自动朗读一起说出来。</summary>
	public static string Completion(bool english) => english ? "This focus session is complete." : "这段专注结束了。";

	/// <summary>当前阶段的一行状态，包含剩余时间、本场选项和累计统计。</summary>
	public static string Status(FocusView view, bool english)
	{
		string phase = PhaseLabel(view.Phase, english);
		string totals = english
			? $"{view.CompletedCount} completed, {FormatDuration(view.TotalSeconds)} in total."
			: $"已完成 {view.CompletedCount} 次，累计 {FormatDuration(view.TotalSeconds)}。";
		if (view.Phase is not ("running" or "paused" or "completed"))
			return english ? $"{phase}. {totals}" : $"{phase}。{totals}";
		string notify = view.NotifyOnComplete
			? (english ? "completion notice on" : "完成通知开")
			: (english ? "completion notice off" : "完成通知关");
		string reminders = view.SilentReminders
			? (english ? "reminders quiet" : "提醒静默")
			: (english ? "reminders spoken" : "提醒照常");
		string clock = english
			? $"Remaining {FormatDuration(view.RemainingSeconds)} of {FormatDuration(view.DurationSeconds)}"
			: $"剩余 {FormatDuration(view.RemainingSeconds)}，本场 {FormatDuration(view.DurationSeconds)}";
		return english
			? $"{phase}. {clock}. {notify}, {reminders}. {totals}"
			: $"{phase}。{clock}。{notify}，{reminders}。{totals}";
	}

	/// <summary>最近记录。只有时长和是否走完，没有聊天或提醒正文。</summary>
	public static string Recent(FocusView view, bool english)
	{
		if (view.Recent.Count == 0) return english ? "No sessions yet." : "还没有专注记录。";
		return string.Join('\n', view.Recent.Select(item =>
		{
			string kind = item.Completed
				? (english ? "Completed" : "完成")
				: (english ? "Ended early" : "提前结束");
			return $"{kind} {FormatDuration(item.Seconds)}";
		}));
	}

	/// <summary>给快照和 focus_view 共用的只读结构。</summary>
	public static FocusDisplay Describe(FocusView view, bool english) => new(
		view.Phase,
		Status(view, english),
		view.RemainingSeconds,
		view.DurationSeconds,
		view.ElapsedSeconds,
		view.NotifyOnComplete,
		view.SilentReminders,
		view.CompletedCount,
		view.TotalSeconds,
		Recent(view, english));

	/// <summary>秒数格式化为 mm:ss 或 h:mm:ss。</summary>
	public static string FormatDuration(int seconds)
	{
		int safe = Math.Max(0, seconds);
		int hours = safe / 3600;
		int minutes = safe % 3600 / 60;
		int remain = safe % 60;
		return hours > 0
			? $"{hours}:{minutes:00}:{remain:00}"
			: $"{minutes:00}:{remain:00}";
	}

	private static string PhaseLabel(string phase, bool english) => phase switch
	{
		"running" => english ? "In progress" : "进行中",
		"paused" => english ? "Paused" : "已暂停",
		"completed" => english ? "Completed" : "已完成",
		_ => english ? "Not started" : "未开始",
	};
}

/// <summary>快照里的专注状态。字段名由桥接序列化成 camelCase。</summary>
public sealed record FocusDisplay(
	string Phase,
	string StatusText,
	int RemainingSeconds,
	int DurationSeconds,
	int ElapsedSeconds,
	bool NotifyOnComplete,
	bool SilentReminders,
	int CompletedCount,
	int TotalSeconds,
	string RecentText);
