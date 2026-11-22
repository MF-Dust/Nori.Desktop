namespace Nori.Core.Voice.Audio;

/// <summary>
/// 跨块有状态线性插值重采样器。
///
/// 使用有理数相位步进（<c>SourceSampleRate / TargetSampleRate</c> 约分后累加），
/// 并在块间保留上一块末尾样本与分数相位，确保任意块大小切分下波形连续、
/// 无块边界跳变，且长时间运行不产生浮点累计时长漂移。
/// </summary>
public sealed class StatefulLinearResampler
{
	private readonly int _stepNumerator;
	private readonly int _stepDenominator;
	private long _pos;
	private int _phaseNumerator;
	private float _prevSample;
	private bool _hasPrev;

	/// <summary>创建重采样器。</summary>
	public StatefulLinearResampler(int sourceSampleRate, int targetSampleRate)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSampleRate);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSampleRate);
		SourceSampleRate = sourceSampleRate;
		TargetSampleRate = targetSampleRate;
		int gcd = GreatestCommonDivisor(sourceSampleRate, targetSampleRate);
		_stepNumerator = sourceSampleRate / gcd;
		_stepDenominator = targetSampleRate / gcd;
	}

	/// <summary>输入采样率（Hz）。</summary>
	public int SourceSampleRate { get; }

	/// <summary>输出采样率（Hz）。</summary>
	public int TargetSampleRate { get; }

	/// <summary>当前分数相位（0 ≤ Phase &lt; 1）。</summary>
	public double Phase => (double)_phaseNumerator / _stepDenominator;

	/// <summary>累计已处理输入帧数。</summary>
	public long TotalInputFrames { get; private set; }

	/// <summary>累计已输出帧数。</summary>
	public long TotalOutputFrames { get; private set; }

	/// <summary>清空跨块状态与统计计数。</summary>
	public void Reset()
	{
		_pos = 0;
		_phaseNumerator = 0;
		_prevSample = 0;
		_hasPrev = false;
		TotalInputFrames = 0;
		TotalOutputFrames = 0;
	}

	/// <summary>估算处理指定输入帧数所需的最小输出缓冲容量。</summary>
	public int EstimateMaxOutputFrames(int inputFrameCount)
	{
		if (inputFrameCount <= 0) return 0;
		if (_stepNumerator == _stepDenominator) return inputFrameCount;
		return checked((int)(((long)inputFrameCount * _stepDenominator) / _stepNumerator + 2));
	}

	/// <summary>处理一块单声道输入样本并返回重采样后的新数组。</summary>
	public float[] Process(ReadOnlySpan<float> monoInput)
	{
		if (monoInput.IsEmpty) return [];
		int maxFrames = EstimateMaxOutputFrames(monoInput.Length);
		float[] buffer = new float[maxFrames];
		int written = Process(monoInput, buffer);
		return written == buffer.Length ? buffer : buffer[..written];
	}

	/// <summary>处理一块单声道输入样本并写入调用方提供的输出缓冲区，返回写入帧数。</summary>
	public int Process(ReadOnlySpan<float> monoInput, Span<float> monoOutput)
	{
		if (monoInput.IsEmpty) return 0;

		if (_stepNumerator == _stepDenominator)
		{
			if (monoOutput.Length < monoInput.Length)
				throw new ArgumentException("输出缓冲区容量不足", nameof(monoOutput));
			for (int index = 0; index < monoInput.Length; index++)
				monoOutput[index] = Sanitize(monoInput[index]);
			_prevSample = monoOutput[monoInput.Length - 1];
			_hasPrev = true;
			_pos = 0;
			TotalInputFrames += monoInput.Length;
			TotalOutputFrames += monoInput.Length;
			return monoInput.Length;
		}

		int written = 0;
		while (true)
		{
			if (_phaseNumerator == 0)
			{
				if (_pos >= monoInput.Length) break;
				if (written >= monoOutput.Length)
					throw new ArgumentException("输出缓冲区容量不足", nameof(monoOutput));
				float sample = _pos < 0 ? _prevSample : Sanitize(monoInput[(int)_pos]);
				monoOutput[written++] = sample;
			}
			else
			{
				if (_pos + 1 >= monoInput.Length) break;
				if (written >= monoOutput.Length)
					throw new ArgumentException("输出缓冲区容量不足", nameof(monoOutput));
				double d0 = _pos < 0 ? _prevSample : Sanitize(monoInput[(int)_pos]);
				double d1 = Sanitize(monoInput[(int)(_pos + 1)]);
				double frac = (double)_phaseNumerator / _stepDenominator;
				double interpolated = d0 + ((d1 - d0) * frac);
				monoOutput[written++] = (float)Math.Clamp(interpolated, -float.MaxValue, float.MaxValue);
			}

			long nextPhase = (long)_phaseNumerator + _stepNumerator;
			_pos += nextPhase / _stepDenominator;
			_phaseNumerator = (int)(nextPhase % _stepDenominator);
		}

		_pos -= monoInput.Length;
		_prevSample = Sanitize(monoInput[^1]);
		_hasPrev = true;
		TotalInputFrames += monoInput.Length;
		TotalOutputFrames += written;
		return written;
	}

	/// <summary>在有限音频流结束时冲刷跨块末尾尚未输出的半相位样本。</summary>
	public float[] Flush()
	{
		if (!_hasPrev || _pos >= 0)
		{
			_hasPrev = false;
			_prevSample = 0;
			return [];
		}

		int maxFrames = checked((int)(_stepDenominator / _stepNumerator + 2));
		float[] buffer = new float[maxFrames];
		int written = Flush(buffer);
		return written == buffer.Length ? buffer : buffer[..written];
	}

	/// <summary>在有限音频流结束时冲刷跨块末尾尚未输出的半相位样本到指定缓冲区。</summary>
	public int Flush(Span<float> monoOutput)
	{
		if (!_hasPrev) return 0;

		int written = 0;
		while (_pos < 0)
		{
			if (written >= monoOutput.Length)
				throw new ArgumentException("输出缓冲区容量不足", nameof(monoOutput));
			monoOutput[written++] = _prevSample;
			long nextPhase = (long)_phaseNumerator + _stepNumerator;
			_pos += nextPhase / _stepDenominator;
			_phaseNumerator = (int)(nextPhase % _stepDenominator);
		}

		_hasPrev = false;
		_prevSample = 0;
		TotalOutputFrames += written;
		return written;
	}

	private static float Sanitize(float sample) => float.IsFinite(sample) ? sample : 0f;

	private static int GreatestCommonDivisor(int a, int b)
	{
		while (b != 0)
		{
			int remainder = a % b;
			a = b;
			b = remainder;
		}
		return a;
	}
}
