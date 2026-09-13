using System.Globalization;
using System.Text;

namespace Nori.Core.Observation;

/// <summary>负载分档。</summary>
public enum LoadLevel
{
	/// <summary>取不到该项读数。</summary>
	Unknown,

	/// <summary>空闲。</summary>
	Idle,

	/// <summary>一般。</summary>
	Moderate,

	/// <summary>繁忙。</summary>
	Busy,
}

/// <summary>占用分档。</summary>
public enum UsageLevel
{
	/// <summary>取不到该项读数。</summary>
	Unknown,

	/// <summary>充足。</summary>
	Ample,

	/// <summary>偏紧。</summary>
	Tight,

	/// <summary>告急。</summary>
	Critical,
}

/// <summary>用户在不在操作。</summary>
public enum PresenceLevel
{
	/// <summary>取不到该项读数。</summary>
	Unknown,

	/// <summary>正在操作。</summary>
	Active,

	/// <summary>短暂离开。</summary>
	Away,

	/// <summary>久未操作。</summary>
	Absent,
}

/// <summary>显卡读数；取不到时整体为 null。</summary>
public sealed record GpuState
{
	/// <summary>型号名。</summary>
	public required string Name { get; init; }

	/// <summary>核心温度，摄氏度。</summary>
	public required int TemperatureCelsius { get; init; }

	/// <summary>核心利用率百分比。</summary>
	public required int UtilizationPercent { get; init; }

	/// <summary>已用显存，MB。</summary>
	public required int MemoryUsedMb { get; init; }

	/// <summary>显存总量，MB。</summary>
	public required int MemoryTotalMb { get; init; }
}

/// <summary>
/// 这台机器此刻的状态。
///
/// 取不到的项一律为 null 或 Unknown，不用 0 兜底 —— 「显存 0 MB」和「读不到显存」在提示词
/// 里会被模型当成两件完全不同的事。
/// </summary>
public sealed record MachineState
{
	/// <summary>已用内存，MB。</summary>
	public int? MemoryUsedMb { get; init; }

	/// <summary>内存总量，MB。</summary>
	public int? MemoryTotalMb { get; init; }

	/// <summary>Nori 自己占用的内存，MB。</summary>
	public int? OwnMemoryMb { get; init; }

	/// <summary>系统整体 CPU 利用率百分比。</summary>
	public int? CpuPercent { get; init; }

	/// <summary>显卡读数。</summary>
	public GpuState? Gpu { get; init; }

	/// <summary>开机时长。</summary>
	public TimeSpan? Uptime { get; init; }

	/// <summary>键鼠空闲时长。</summary>
	public TimeSpan? Idle { get; init; }

	/// <summary>什么都没取到的空状态。</summary>
	public static MachineState Empty { get; } = new();

	/// <summary>有没有任何一项可用。全空时调用方不应注入提示词。</summary>
	public bool HasAnything =>
		MemoryTotalMb is not null || CpuPercent is not null || Gpu is not null || Uptime is not null || Idle is not null;
}

/// <summary>
/// 把读数折成分档，并渲染成注入提示词的一段。
///
/// **提示词里只放分档，不放具体数字。** 两条理由：
///
/// 1. 缓存。系统提示词在整个请求的最前面，其中任何一个字节变化都会使整段前缀的缓存失效。
///    内存和 CPU 的精确读数每一轮都不一样，直接写进去等于每轮都全量重算 —— 这是实打实的
///    成本回退，而 <c>AgentUsage</c> 里就带着 CacheHitRate，说明这个项目在意它。分档之后
///    取值只在状态真正变化时才动，绝大多数轮次前缀不变。
/// 2. 目的。这一段的用途是让机器状态**影响她的状态**，不是让她报数。「显存告急」足以让她
///    有反应，「18234 MB / 24564 MB」多出来的精度没有任何行为上的差别。
///
/// 要精确数字的场景走工具，那条路上的返回值不进系统提示词。
/// </summary>
public static class MachineStateText
{
	/// <summary>
	/// 占用超过这个比例算偏紧。
	///
	/// 实测校准过：本机 32 GB 用掉 19.5 GB（60.6%）时还剩 12 GB，判「偏紧」明显过早 ——
	/// Windows 空载常年就在 40–60%，按 0.6 分档她会天天喊内存紧张，这一段就失去意义了。
	/// </summary>
	public const double TightRatio = 0.75;

	/// <summary>内存占用超过这个比例算告急。</summary>
	public const double CriticalRatio = 0.90;

	/// <summary>CPU 利用率低于这个值算空闲。</summary>
	public const int IdlePercent = 20;

	/// <summary>CPU 利用率高于这个值算繁忙。</summary>
	public const int BusyPercent = 60;

	/// <summary>显卡温度高于这个值算热。</summary>
	public const int HotCelsius = 80;

	/// <summary>空闲超过这个时长算短暂离开。</summary>
	public static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(1);

	/// <summary>空闲超过这个时长算久未操作。</summary>
	public static readonly TimeSpan AbsentAfter = TimeSpan.FromMinutes(10);

	/// <summary>按已用比例折成占用分档。</summary>
	public static UsageLevel Usage(int? used, int? total) =>
		used is not {} usedValue || total is not {} totalValue || totalValue <= 0
			? UsageLevel.Unknown
			: ((double)usedValue / totalValue) switch
			{
				>= CriticalRatio => UsageLevel.Critical,
				>= TightRatio => UsageLevel.Tight,
				_ => UsageLevel.Ample,
			};

	/// <summary>按利用率折成负载分档。</summary>
	public static LoadLevel Load(int? percent) => percent switch
	{
		null => LoadLevel.Unknown,
		> BusyPercent => LoadLevel.Busy,
		>= IdlePercent => LoadLevel.Moderate,
		_ => LoadLevel.Idle,
	};

	/// <summary>按空闲时长折成在场分档。</summary>
	public static PresenceLevel Presence(TimeSpan? idle) => idle switch
	{
		null => PresenceLevel.Unknown,
		{} value when value >= AbsentAfter => PresenceLevel.Absent,
		{} value when value >= AwayAfter => PresenceLevel.Away,
		_ => PresenceLevel.Active,
	};

	/// <summary>
	/// 渲染注入用的一段文字；无任何读数时返回空串。
	///
	/// 开机时长按小时粗粒度取整，同样是为了让取值稳定 —— 精确到分钟的话每一轮都在变。
	/// </summary>
	public static string Render(MachineState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		if (!state.HasAnything) return "";

		List<string> parts = [];

		if (Usage(state.MemoryUsedMb, state.MemoryTotalMb) is not UsageLevel.Unknown and var memory)
		{
			parts.Add("内存" + Describe(memory));
		}

		if (Load(state.CpuPercent) is not LoadLevel.Unknown and var cpu) parts.Add("CPU " + Describe(cpu));

		if (state.Gpu is {} gpu)
		{
			parts.Add(
				"显卡" + Describe(Load(gpu.UtilizationPercent))
					+ "、显存" + Describe(Usage(gpu.MemoryUsedMb, gpu.MemoryTotalMb))
					+ (gpu.TemperatureCelsius > HotCelsius ? "、温度偏高" : ""));
		}

		if (state.Uptime is {} uptime) parts.Add(Uptime(uptime));
		if (Presence(state.Idle) is not PresenceLevel.Unknown and var presence) parts.Add(Describe(presence));

		return parts.Count == 0
			? ""
			: new StringBuilder("【这台机器现在的状态】：")
				.Append(string.Join("，", parts))
				.Append("。这是环境信息，用来让你的语气贴合当下，不是让你复述这些指标。")
				.ToString();
	}

	private static string Describe(UsageLevel level) => level switch
	{
		UsageLevel.Ample => "充足",
		UsageLevel.Tight => "偏紧",
		UsageLevel.Critical => "告急",
		_ => "未知",
	};

	private static string Describe(LoadLevel level) => level switch
	{
		LoadLevel.Idle => "空闲",
		LoadLevel.Moderate => "负载一般",
		LoadLevel.Busy => "繁忙",
		_ => "未知",
	};

	private static string Describe(PresenceLevel level) => level switch
	{
		PresenceLevel.Active => "你正在操作",
		PresenceLevel.Away => "你刚离开一会儿",
		PresenceLevel.Absent => "你已经很久没动过键鼠",
		_ => "未知",
	};

	private static string Uptime(TimeSpan uptime)
	{
		int hours = (int)uptime.TotalHours;
		return hours < 1
			? "刚开机不久"
			: "已开机 " + hours.ToString(CultureInfo.InvariantCulture) + " 小时左右";
	}
}
