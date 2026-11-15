using System.Collections.Frozen;
using System.Globalization;

namespace Nori.Core.Memory;

/// <summary>时间线的活跃/归档筛选。活跃指尚未归档，包含休眠、过期等真实状态。</summary>
public enum MemoryTimelineActivity
{
	All,
	Active,
	Archived,
}

/// <summary>按真实创建时间分页的时间线查询。不改变召回检索。</summary>
public sealed record MemoryTimelineQuery
{
	public const int DefaultLimit = 24;
	public const int MaxLimit = 50;
	public const int MaxKeywordLength = 200;

	public string? Keyword { get; init; }
	public string? Kind { get; init; }
	public MemoryTimelineActivity Activity { get; init; } = MemoryTimelineActivity.All;
	/// <summary>本地日历上的开始日期，含当天。空表示不限制。</summary>
	public DateOnly? From { get; init; }
	/// <summary>本地日历上的结束日期，含当天。空表示不限制。</summary>
	public DateOnly? To { get; init; }
	public int Limit { get; init; } = DefaultLimit;
	public int Offset { get; init; }

	/// <summary>整理筛选。无法识别的类型、颠倒的日期和过长关键词会拒绝，而不是猜测。</summary>
	public MemoryTimelineQuery Normalize()
	{
		string keyword = Keyword?.Trim() ?? "";
		if (keyword.Length > MaxKeywordLength) throw new InvalidOperationException("搜索关键词过长");
		if (!Enum.IsDefined(Activity)) throw new InvalidOperationException("归档筛选无效");
		if (From is { } from && To is { } to && from > to) throw new InvalidOperationException("时间范围的开始日期晚于结束日期");
		int limit = Limit <= 0 ? DefaultLimit : Math.Min(Limit, MaxLimit);
		return this with
		{
			Keyword = keyword,
			Kind = NormalizeKind(Kind),
			Limit = limit,
			Offset = Math.Max(0, Offset),
		};
	}

	/// <summary>把界面类型收成库存值。空白表示不筛选。</summary>
	public static string? NormalizeKind(string? kind)
	{
		if (string.IsNullOrWhiteSpace(kind)) return null;
		string trimmed = kind.Trim().ToLowerInvariant();
		if (!KnownKinds.Contains(trimmed)) throw new InvalidOperationException("记忆类型无效");
		return MemoryKindExtensions.Parse(trimmed).ToStorage();
	}

	private static readonly FrozenSet<string> KnownKinds = new[]
	{
		"general", "episodic", "event", "factual", "fact", "preference", "prefer",
		"relational", "relationship", "planned", "plan", "identity", "name",
	}.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>创建或更新时间的判定。未知不会用别的字段填上。</summary>
public readonly record struct MemoryCreatedStamp
{
	public bool Known { get; init; }
	/// <summary>日期来自带时区的时刻。仅有日历日期时为 false。</summary>
	public bool HasTime { get; init; }
	public DateOnly? Date { get; init; }
	public DateTimeOffset? Instant { get; init; }
}

/// <summary>时间线上的一条记忆，只带创建时间判定。</summary>
public sealed record MemoryTimelineEntry
{
	public required MemoryItem Item { get; init; }
	public required MemoryCreatedStamp Created { get; init; }
}

/// <summary>一页时间线。Total 是筛选后的条数，Items 只含当前页。</summary>
public sealed record MemoryTimelinePage
{
	public required IReadOnlyList<MemoryTimelineEntry> Items { get; init; }
	public required int Total { get; init; }
	public required int Limit { get; init; }
	public required int Offset { get; init; }
}

/// <summary>库里已经写明的记忆关系。解析不到目标时不补正文。</summary>
public sealed record MemoryTimelineRelation
{
	public required long Id { get; init; }
	/// <summary>superseded-by 表示对方替代了本条；supersedes 表示本条替代了对方。</summary>
	public required string Link { get; init; }
	public required bool Resolved { get; init; }
	public string? Content { get; init; }
	public string? CanonicalSummary { get; init; }
	public string? Kind { get; init; }
	public string? Status { get; init; }
	public string? CreatedAt { get; init; }
}

/// <summary>时间线详情只汇总库中读到的字段。</summary>
public sealed record MemoryTimelineDetail
{
	public required MemoryItem Item { get; init; }
	public required MemoryCreatedStamp Created { get; init; }
	public required MemoryCreatedStamp Updated { get; init; }
	public required IReadOnlyList<MemorySource> Sources { get; init; }
	public required IReadOnlyList<MemoryTimelineRelation> Related { get; init; }
	public required bool RelatedTruncated { get; init; }
}

/// <summary>判断记忆时间字符串。没有时区的时刻和无法解析的文本都算未知。</summary>
public static class MemoryTimelineTime
{
	public static MemoryCreatedStamp Classify(string? value, TimeZoneInfo zone)
	{
		ArgumentNullException.ThrowIfNull(zone);
		if (string.IsNullOrWhiteSpace(value)) return default;
		string text = value.Trim();
		if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly dateOnly))
			return new MemoryCreatedStamp { Known = true, HasTime = false, Date = dateOnly };
		if (!HasExplicitOffset(text) || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset instant))
			return default;
		DateTime local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
		return new MemoryCreatedStamp
		{
			Known = true,
			HasTime = true,
			Date = DateOnly.FromDateTime(local),
			Instant = instant,
		};
	}

	public static bool InRange(MemoryCreatedStamp stamp, DateOnly? from, DateOnly? to)
	{
		if (from is null && to is null) return true;
		if (!stamp.Known || stamp.Date is not { } date) return false;
		if (from is { } start && date < start) return false;
		if (to is { } end && date > end) return false;
		return true;
	}

	public static int Compare(MemoryCreatedStamp left, long leftId, MemoryCreatedStamp right, long rightId)
	{
		if (left.Known != right.Known) return left.Known ? -1 : 1;
		if (!left.Known) return rightId.CompareTo(leftId);
		int date = right.Date!.Value.CompareTo(left.Date!.Value);
		if (date != 0) return date;
		if (left.HasTime != right.HasTime) return left.HasTime ? -1 : 1;
		if (left.HasTime && right.HasTime)
		{
			int instant = right.Instant!.Value.CompareTo(left.Instant!.Value);
			if (instant != 0) return instant;
		}
		return rightId.CompareTo(leftId);
	}

	private static bool HasExplicitOffset(string text)
	{
		if (text.EndsWith('Z') || text.EndsWith('z')) return text.Contains('T') || text.Contains(' ');
		int separator = text.IndexOf('T');
		if (separator < 0) separator = text.IndexOf(' ');
		if (separator < 0) return false;
		int sign = Math.Max(text.LastIndexOf('+'), text.LastIndexOf('-'));
		return sign > separator;
	}
}
