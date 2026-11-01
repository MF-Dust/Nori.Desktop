using System.Diagnostics;

namespace Nori.Desktop.Audio.Linux;

/// <summary>非阻塞播放收尾每一轮要执行的动作。</summary>
internal enum AlsaDrainStep
{
	/// <summary>缓冲已播完，或已到等待上限。</summary>
	Done,

	/// <summary>仍在播放，稍后再查询。</summary>
	Pending,

	/// <summary>短音频停在 PREPARED，需要显式启动。</summary>
	Start,

	/// <summary>设备已挂起，需要尝试恢复后继续收尾。</summary>
	Recover,

	/// <summary>读取剩余帧失败且不可恢复。</summary>
	Fail,
}

/// <summary>
/// ALSA 播放收尾判定；不调用原生接口，便于单测。
///
/// PipeWire 的 drain 回调可能忽略非阻塞模式，因此按 <c>snd_pcm_delay</c> 报告的剩余帧推进：
/// 短音频显式启动；PipeWire 欠载后延迟读数停止更新，按首次报告的帧数等完尾音。
/// 任何插件都可能让剩余帧停在正值，总等待以首次报告的剩余时长加余量为上限，
/// 到期即视为完成，由释放设备丢弃残余缓冲。
/// </summary>
internal sealed class AlsaDrainTracker
{
	/// <summary>单次报告的剩余时长上限；缓冲约 200 ms，更大的读数视为插件异常。</summary>
	internal static readonly TimeSpan MaxReportedTail = TimeSpan.FromSeconds(2);

	/// <summary>在剩余时长之外允许下游再延迟的时间。</summary>
	internal static readonly TimeSpan Slack = TimeSpan.FromSeconds(1);

	private long? _tailDeadline;
	private long? _deadline;

	/// <summary>又写入了新样本，此前的收尾期限作废。</summary>
	public void Reset()
	{
		_tailDeadline = null;
		_deadline = null;
	}

	/// <param name="delayCode"><c>snd_pcm_delay</c> 的返回值。</param>
	/// <param name="remainingFrames"><c>snd_pcm_delay</c> 报告的剩余帧。</param>
	/// <param name="state"><paramref name="delayCode"/> 非负时读取的 <c>snd_pcm_state</c>。</param>
	/// <param name="sampleRate">设备实际采样率。</param>
	/// <param name="timestamp"><see cref="Stopwatch.GetTimestamp"/> 时间戳。</param>
	public AlsaDrainStep Next(int delayCode, long remainingFrames, int state, int sampleRate, long timestamp)
	{
		// hw/dmix 欠载时缓冲已经播空。
		if (delayCode == AlsaPcm.Underrun) return AlsaDrainStep.Done;
		bool suspended = delayCode == AlsaPcm.Suspended;
		bool interrupted = delayCode == AlsaPcm.Interrupted;
		if (delayCode < 0 && !suspended && !interrupted) return AlsaDrainStep.Fail;
		long remaining = delayCode < 0 ? 0 : Math.Max(0, remainingFrames);
		if (delayCode >= 0 && remaining == 0) return AlsaDrainStep.Done;

		_deadline ??= timestamp + Ticks(remaining, sampleRate) + (long) Math.Ceiling(Slack.TotalSeconds * Stopwatch.Frequency);
		if (timestamp >= _deadline.Value) return AlsaDrainStep.Done;
		// 信号中断没有有效延迟读数；留到下一轮重试，不准备设备或重置期限。
		if (interrupted) return AlsaDrainStep.Pending;
		if (suspended) return AlsaDrainStep.Recover;
		if (state == AlsaPcm.StatePrepared) return AlsaDrainStep.Start;
		if (state != AlsaPcm.StateUnderrun) return AlsaDrainStep.Pending;

		_tailDeadline ??= timestamp + Ticks(remaining, sampleRate);
		return timestamp >= _tailDeadline.Value ? AlsaDrainStep.Done : AlsaDrainStep.Pending;
	}

	private static long Ticks(long frames, int sampleRate)
	{
		double seconds = sampleRate > 0 ? (double) frames / sampleRate : MaxReportedTail.TotalSeconds;
		return (long) Math.Ceiling(Math.Min(seconds, MaxReportedTail.TotalSeconds) * Stopwatch.Frequency);
	}
}
