namespace Nori.Core.Voice.Metrics;

public readonly record struct VoiceLatencyStatistics(double P50, double P95, double Max, int Count);

public readonly record struct VoiceWorkingSetBytes(long Current, long Peak);

/// <summary>只包含可导出的数值，不持有会话数据或样本集合。</summary>
public sealed record VoiceMetricsSnapshot(
	VoiceLatencyStatistics FirstDeltaLatencyMs,
	VoiceLatencyStatistics FinalTranscriptLatencyMs,
	VoiceLatencyStatistics AgentFirstTokenLatencyMs,
	VoiceLatencyStatistics TtsFirstSegmentLatencyMs,
	long PeakAudioBufferBytes,
	VoiceWorkingSetBytes WorkingSetBytes,
	double CpuUtilizationPercent,
	long DeviceRecoveryFailureCount);

/// <summary>进程内语音统计，每项延迟仅保留最近 256 个数值样本。</summary>
public sealed class VoiceMetricsTracker
{
	private const int WindowCapacity = 256;
	private readonly Lock _gate = new();
	private readonly LatencyWindow _firstDelta = new();
	private readonly LatencyWindow _finalTranscript = new();
	private readonly LatencyWindow _agentFirstToken = new();
	private readonly LatencyWindow _ttsFirstSegment = new();
	private long _peakAudioBufferBytes;
	private VoiceWorkingSetBytes _workingSetBytes;
	private double _cpuUtilizationPercent;
	private long _deviceRecoveryFailureCount;

	public void RecordFirstDeltaLatency(TimeSpan latency) => Record(_firstDelta, latency);
	public void RecordFinalTranscriptLatency(TimeSpan latency) => Record(_finalTranscript, latency);
	public void RecordAgentFirstTokenLatency(TimeSpan latency) => Record(_agentFirstToken, latency);
	public void RecordTtsFirstSegmentLatency(TimeSpan latency) => Record(_ttsFirstSegment, latency);

	public void ObserveAudioBufferBytes(long bytes)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(bytes);
		lock (_gate) _peakAudioBufferBytes = Math.Max(bytes, _peakAudioBufferBytes);
	}

	/// <summary>记录当前常驻内存和按逻辑处理器数量归一化的 CPU 使用率。</summary>
	public void ObserveProcessResources(long workingSetBytes, double cpuUtilizationPercent)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(workingSetBytes);
		if (!double.IsFinite(cpuUtilizationPercent) || cpuUtilizationPercent is < 0 or > 100)
			throw new ArgumentOutOfRangeException(nameof(cpuUtilizationPercent), "CPU 使用率必须在 0 到 100 之间");
		lock (_gate)
		{
			_workingSetBytes = new(workingSetBytes, Math.Max(workingSetBytes, _workingSetBytes.Peak));
			_cpuUtilizationPercent = cpuUtilizationPercent;
		}
	}

	public void RecordDeviceRecoveryFailure()
	{
		lock (_gate)
			if (_deviceRecoveryFailureCount < long.MaxValue) _deviceRecoveryFailureCount++;
	}

	public VoiceMetricsSnapshot CaptureSnapshot()
	{
		lock (_gate)
			return new(_firstDelta.Summarize(), _finalTranscript.Summarize(), _agentFirstToken.Summarize(),
				_ttsFirstSegment.Summarize(), _peakAudioBufferBytes, _workingSetBytes, _cpuUtilizationPercent,
				_deviceRecoveryFailureCount);
	}

	private void Record(LatencyWindow window, TimeSpan latency)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(latency, TimeSpan.Zero);
		lock (_gate) window.Add(latency.TotalMilliseconds);
	}

	private sealed class LatencyWindow
	{
		private readonly double[] _samples = new double[WindowCapacity];
		private int _next;
		private int _count;

		public void Add(double milliseconds)
		{
			_samples[_next] = milliseconds;
			_next = (_next + 1) % _samples.Length;
			_count = Math.Min(_count + 1, _samples.Length);
		}

		public VoiceLatencyStatistics Summarize()
		{
			if (_count == 0) return default;
			Span<double> sorted = stackalloc double[_count];
			_samples.AsSpan(0, _count).CopyTo(sorted);
			sorted.Sort();
			// 最近秩百分位：结果总是窗口内实际记录过的值。
			return new(sorted[(int)Math.Ceiling(_count * 0.50) - 1],
				sorted[(int)Math.Ceiling(_count * 0.95) - 1], sorted[^1], _count);
		}
	}
}
