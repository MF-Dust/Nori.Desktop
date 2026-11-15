namespace Nori.Core.Voice.Realtime;

/// <summary>一帧能量检测的结论。</summary>
public readonly record struct EnergyVoiceDecision(bool SpeechStarted, bool SpeechEnded, bool InSpeech, double Rms);

/// <summary>
/// 本地能量语音活动检测。
///
/// 只用样本均方根和一段噪声底，不联网，也不依赖模型。
/// 短脉冲要持续到 <see cref="RealtimeVoiceLimits.MinimumSpeechMilliseconds"/> 才算开口；
/// 噪声底会跟着环境慢慢走，持续的底噪不会每秒都报成一句话。
/// </summary>
public sealed class EnergyVoiceActivity
{
	private double _noiseFloor = 0.01;
	private int _voicedMilliseconds;
	private int _silentMilliseconds;
	private bool _latched;

	/// <summary>当前是否已经认定在说话。</summary>
	public bool InSpeech => _latched;

	/// <summary>清空开口状态，保留已经适应的噪声底。</summary>
	public void ResetSpeech()
	{
		_voicedMilliseconds = 0;
		_silentMilliseconds = 0;
		_latched = false;
	}

	/// <summary>连噪声底一起回到初始值。</summary>
	public void ResetAll()
	{
		ResetSpeech();
		_noiseFloor = 0.01;
	}

	/// <summary>
	/// 吃进一帧交错样本。时间按采样数推进，不读墙上时钟。
	/// <paramref name="holdOpen"/> 为真时，已经开口的话不会因为静音被切掉。
	/// </summary>
	public EnergyVoiceDecision Push(ReadOnlySpan<float> interleaved, int channels, int sampleRate, double sensitivity, int silenceMilliseconds, bool holdOpen)
	{
		int channelCount = Math.Max(1, channels);
		int frames = interleaved.Length / channelCount;
		int elapsed = sampleRate <= 0 || frames <= 0 ? 0 : (int)((long)frames * 1000 / sampleRate);
		double rms = RootMeanSquare(interleaved);
		if (elapsed <= 0) return new EnergyVoiceDecision(false, false, _latched, rms);

		double finiteSensitivity = double.IsFinite(sensitivity) ? sensitivity : RealtimeVoiceLimits.DefaultSensitivity;
		double clampedSensitivity = Math.Clamp(finiteSensitivity, 0, 1);
		int silence = Math.Clamp(silenceMilliseconds, RealtimeVoiceLimits.MinimumSilenceMilliseconds, RealtimeVoiceLimits.MaximumSilenceMilliseconds);
		double absolute = 0.09 - (clampedSensitivity * 0.075);
		double multiplier = 4.5 - (clampedSensitivity * 2.8);
		double threshold = Math.Max(absolute, _noiseFloor * multiplier);
		bool hot = rms >= threshold;
		// 开口期间不能把当前声音学成噪声，否则持续说话会被自己抬高的门槛切断。
		if (!hot) AdaptNoise(rms);

		if (hot)
		{
			_silentMilliseconds = 0;
			_voicedMilliseconds += elapsed;
			if (_latched || _voicedMilliseconds < RealtimeVoiceLimits.MinimumSpeechMilliseconds)
				return new EnergyVoiceDecision(false, false, _latched, rms);
			_latched = true;
			return new EnergyVoiceDecision(true, false, true, rms);
		}

		if (_latched && holdOpen)
		{
			_silentMilliseconds = 0;
			return new EnergyVoiceDecision(false, false, true, rms);
		}

		if (!_latched)
		{
			_voicedMilliseconds = 0;
			return new EnergyVoiceDecision(false, false, false, rms);
		}

		_silentMilliseconds += elapsed;
		if (_silentMilliseconds < silence) return new EnergyVoiceDecision(false, false, true, rms);
		ResetSpeech();
		return new EnergyVoiceDecision(false, true, false, rms);
	}

	private void AdaptNoise(double rms)
	{
		_noiseFloor = (_noiseFloor * 0.90) + (rms * 0.10);
		_noiseFloor = Math.Clamp(_noiseFloor, 0.001, 0.2);
	}

	private static double RootMeanSquare(ReadOnlySpan<float> samples)
	{
		if (samples.Length == 0) return 0;
		double sum = 0;
		for (int index = 0; index < samples.Length; index++)
		{
			double sample = samples[index];
			sum += sample * sample;
		}
		return Math.Sqrt(sum / samples.Length);
	}
}
