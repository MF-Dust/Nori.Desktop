using System.Diagnostics;

namespace Nori.Core.Focus;

/// <summary>专注计时使用的单调时钟。实现不得读取会回拨的系统墙钟。</summary>
public interface IFocusClock
{
	/// <summary>当前单调时间戳，只和同一时钟的另一次读取比较。</summary>
	long Now { get; }

	/// <summary>两个时间戳之间的秒数。终点不晚于起点时记为 0，避免回拨把已用时长减回去。</summary>
	double SecondsBetween(long start, long end);
}

/// <summary>生产环境时钟，基于 <see cref="Stopwatch"/> 的单调计数，不跟随系统校时。</summary>
public sealed class SystemFocusClock : IFocusClock
{
	/// <inheritdoc />
	public long Now => Stopwatch.GetTimestamp();

	/// <inheritdoc />
	public double SecondsBetween(long start, long end)
	{
		if (end <= start || Stopwatch.Frequency <= 0) return 0;
		return (end - start) / (double)Stopwatch.Frequency;
	}
}
