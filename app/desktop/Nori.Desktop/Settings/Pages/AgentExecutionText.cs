using System.Globalization;
using System.Text;
using Nori.Core.Agent;

namespace Nori.Desktop.Settings.Pages;

/// <summary>执行时间线上的一行。文本只来自已经脱敏的字段。</summary>
public sealed record AgentExecutionLine(long Sequence, string Text, bool Attention);

/// <summary>调试页上的执行记录文案。</summary>
public sealed record AgentExecutionPresentation(
	string Summary,
	IReadOnlyList<AgentExecutionLine> Lines,
	string CopyText);

/// <summary>把执行快照格式化成当前界面语言。缺失的用量保持不可用。</summary>
public static class AgentExecutionText
{
	public static AgentExecutionPresentation Present(AgentExecutionSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		string unavailable = NativeSettingsResources.Get("exec.unavailable");
		AgentExecutionSummary summary = snapshot.Summary;
		string model = summary.ModelDurationMs is long duration
			? string.Format(CultureInfo.InvariantCulture, NativeSettingsResources.Get("exec.ms"), duration)
			: unavailable;
		string usage = summary.TotalTokens is int tokens
			? tokens.ToString(CultureInfo.InvariantCulture)
			: unavailable;
		string headline = string.Format(
			CultureInfo.InvariantCulture,
			NativeSettingsResources.Get("exec.summary"),
			summary.RealExecutions,
			summary.Deduplicated,
			model,
			usage);
		List<AgentExecutionLine> lines = new(snapshot.Events.Count);
		foreach (AgentExecutionEvent ev in snapshot.Events)
			lines.Add(new AgentExecutionLine(ev.Sequence, Line(ev), ev.Status is AgentExecutionStatus.Failed or AgentExecutionStatus.Denied));
		StringBuilder copy = new();
		copy.AppendLine(headline);
		foreach (AgentExecutionLine line in lines) copy.AppendLine(line.Text);
		return new AgentExecutionPresentation(headline, lines, copy.ToString().TrimEnd());
	}

	private static string Line(AgentExecutionEvent ev)
	{
		string time = ev.OccurredAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
		string label = Label(ev);
		string tool = string.IsNullOrEmpty(ev.ToolName) ? "" : " " + ev.ToolName;
		string duration = ev.DurationMs is long ms
			? " " + string.Format(CultureInfo.InvariantCulture, NativeSettingsResources.Get("exec.ms"), ms)
			: "";
		string usage = Usage(ev);
		string category = Category(ev);
		return string.Join(' ', new[] { time, label + tool, duration.Trim(), usage, category }.Where(part => part.Length > 0));
	}

	private static string Label(AgentExecutionEvent ev)
	{
		string prefix = "";
		if (ev.Late) prefix += NativeSettingsResources.Get("exec.late") + " · ";
		if (ev.Deduplicated) prefix += NativeSettingsResources.Get("exec.dedup") + " · ";
		string body = ev.Kind switch
		{
			AgentExecutionKind.Request => NativeSettingsResources.Get("exec.kind.request") + " · " + Status(ev.Status),
			AgentExecutionKind.Model => NativeSettingsResources.Get("exec.kind.model") + " · " + Status(ev.Status),
			AgentExecutionKind.Session => NativeSettingsResources.Get("exec.kind.session") + " · " + Status(ev.Status),
			AgentExecutionKind.Permission => Permission(ev),
			_ => Status(ev.Status),
		};
		string round = ev.Round is int value ? " #" + value.ToString(CultureInfo.InvariantCulture) : "";
		return prefix + body + round;
	}

	private static string Permission(AgentExecutionEvent ev) => ev.FailureCategory switch
	{
		"requested" => NativeSettingsResources.Get("exec.permission.requested"),
		"allowed" => NativeSettingsResources.Get("exec.permission.allowed"),
		"denied" => NativeSettingsResources.Get("exec.permission.denied"),
		"cancelled" => NativeSettingsResources.Get("exec.permission.cancelled"),
		"approval_timeout" => NativeSettingsResources.Get("exec.permission.timeout"),
		"approval_unavailable" => NativeSettingsResources.Get("exec.permission.unavailable"),
		"approval_failed" => NativeSettingsResources.Get("exec.permission.failed"),
		_ => Status(ev.Status),
	};

	private static string Status(AgentExecutionStatus status) => status switch
	{
		AgentExecutionStatus.Pending => NativeSettingsResources.Get("exec.status.pending"),
		AgentExecutionStatus.Running => NativeSettingsResources.Get("exec.status.running"),
		AgentExecutionStatus.Success => NativeSettingsResources.Get("exec.status.success"),
		AgentExecutionStatus.Failed => NativeSettingsResources.Get("exec.status.failed"),
		AgentExecutionStatus.Cancelled => NativeSettingsResources.Get("exec.status.cancelled"),
		AgentExecutionStatus.Denied => NativeSettingsResources.Get("exec.status.denied"),
		_ => NativeSettingsResources.Get("exec.unavailable"),
	};

	private static string Usage(AgentExecutionEvent ev)
	{
		if (ev.Kind != AgentExecutionKind.Model) return "";
		if (ev.Usage is not { } usage) return NativeSettingsResources.Get("exec.usage.none");
		string tokens = string.Format(CultureInfo.InvariantCulture, NativeSettingsResources.Get("exec.tokens"), usage.PromptTokens, usage.CompletionTokens);
		string cache = usage.CacheHitRate is double rate
			? string.Format(CultureInfo.InvariantCulture, NativeSettingsResources.Get("exec.cache"), rate.ToString("0.#", CultureInfo.InvariantCulture))
			: NativeSettingsResources.Get("exec.cache.none");
		return tokens + " · " + cache;
	}

	private static string Category(AgentExecutionEvent ev)
	{
		if (ev.FailureCategory is not { Length: > 0 } category) return "";
		if (category is "duplicate" or "requested" or "allowed" or "denied" or "cancelled" or "not_started"
			or "approval_timeout" or "approval_unavailable" or "approval_failed") return "";
		if (category == "unclassified") return NativeSettingsResources.Get("exec.unclassified");
		return category;
	}
}
