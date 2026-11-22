namespace Nori.Core.Voice.Realtime;

/// <summary>一帧能量检测的结论。</summary>
public readonly record struct EnergyVoiceDecision(bool SpeechStarted, bool SpeechEnded, bool InSpeech, double Rms);

/// <summary>本地能量 VAD 参数配置。</summary>
public sealed record EnergyVoiceActivityOptions
{
	/// <summary>默认配置。</summary>
	public static EnergyVoiceActivityOptions Default { get; } = new();

	/// <summary>确认开口所需的最短连续语音时长（毫秒）。短于此窗口的瞬态脉冲不触发开口。</summary>
	public int MinimumSpeechMilliseconds { get; init; } = RealtimeVoiceLimits.MinimumSpeechMilliseconds;

	/// <summary>维持阈值相对开口阈值的比例（0.1–1.0）。开口后轻声续说可借助迟滞保持不中断。</summary>
	public double ContinueThresholdRatio { get; init; } = 0.6;

	/// <summary>噪声底自适应学习率（0.0–0.5）。</summary>
	public double NoiseAdaptRate { get; init; } = 0.10;
}

/// <summary>
/// 本地能量语音活动检测。
///
/// 只用样本均方根和自适应噪声底，不联网，也不依赖模型。
/// 按固定 10 ms 短分析子窗跨块计算 RMS，并用整数帧分子比较（<c>frames * 1000 ≥ ms * sampleRate</c>）
/// 推进发声与静音时长，消除大块内单次脉冲污染整块以及小块/逐样本截断误差。
/// </summary>
public sealed class EnergyVoiceActivity
{
	/// <summary>固定短分析子窗时长（毫秒）。</summary>
	public const int AnalysisWindowMilliseconds = 10;

	private readonly EnergyVoiceActivityOptions _options;
	private readonly int _minimumSpeechMilliseconds;
	private readonly double _continueThresholdRatio;
	private readonly double _noiseAdaptRate;

	private double _noiseFloor = 0.01;
	private long _voicedFrames;
	private long _silentFrames;
	private int _windowSampleRate;
	private int _windowFrames;
	private int _windowSampleCount;
	private double _windowSumSq;
	private bool _latched;

	/// <summary>使用默认参数创建本地能量检测器。</summary>
	public EnergyVoiceActivity()
		: this(EnergyVoiceActivityOptions.Default)
	{
	}

	/// <summary>使用自定义参数创建本地能量检测器。</summary>
	public EnergyVoiceActivity(EnergyVoiceActivityOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options;
		_minimumSpeechMilliseconds = Math.Clamp(options.MinimumSpeechMilliseconds, 20, 2_000);
		double ratio = double.IsFinite(options.ContinueThresholdRatio) ? options.ContinueThresholdRatio : 0.6;
		_continueThresholdRatio = Math.Clamp(ratio, 0.1, 1.0);
		double adapt = double.IsFinite(options.NoiseAdaptRate) ? options.NoiseAdaptRate : 0.10;
		_noiseAdaptRate = Math.Clamp(adapt, 0.0, 0.5);
	}

	/// <summary>当前生效的配置。</summary>
	public EnergyVoiceActivityOptions Options => _options;

	/// <summary>确认开口所需的最短连续语音时长（毫秒）。</summary>
	public int MinimumSpeechMilliseconds => _minimumSpeechMilliseconds;

	/// <summary>当前适应后的噪声底。</summary>
	public double NoiseFloor => _noiseFloor;

	/// <summary>最近一帧使用的开口阈值。</summary>
	public double StartThreshold { get; private set; }

	/// <summary>最近一帧使用的维持阈值。</summary>
	public double ContinueThreshold { get; private set; }

	/// <summary>当前是否已经认定在说话。</summary>
	public bool InSpeech => _latched;

	/// <summary>清空开口状态，保留已经适应的噪声底。</summary>
	public void ResetSpeech()
	{
		_voicedFrames = 0;
		_silentFrames = 0;
		_latched = false;
		ResetWindowAccumulators();
	}

	/// <summary>连噪声底一起回到初始值。</summary>
	public void ResetAll()
	{
		ResetSpeech();
		_noiseFloor = 0.01;
		_windowSampleRate = 0;
		StartThreshold = 0;
		ContinueThreshold = 0;
	}

	/// <summary>
	/// 吃进一帧交错样本。按固定 10 ms 子窗和精确整数帧数推进时间，不读墙上时钟。
	/// <paramref name="holdOpen"/> 为真时，已经开口的话不会因为静音被切掉。
	/// </summary>
	public EnergyVoiceDecision Push(ReadOnlySpan<float> interleaved, int channels, int sampleRate, double sensitivity, int silenceMilliseconds, bool holdOpen)
	{
		int channelCount = Math.Max(1, channels);
		int totalFrames = interleaved.Length / channelCount;
		if (sampleRate <= 0 || totalFrames <= 0)
			return new EnergyVoiceDecision(false, false, _latched, 0);

		if (_windowSampleRate != sampleRate)
		{
			if (_windowSampleRate > 0)
			{
				_voicedFrames = _voicedFrames * sampleRate / _windowSampleRate;
				_silentFrames = _silentFrames * sampleRate / _windowSampleRate;
			}
			_windowSampleRate = sampleRate;
			ResetWindowAccumulators();
		}

		double finiteSensitivity = double.IsFinite(sensitivity) ? sensitivity : RealtimeVoiceLimits.DefaultSensitivity;
		double clampedSensitivity = Math.Clamp(finiteSensitivity, 0, 1);
		int silence = Math.Clamp(silenceMilliseconds, RealtimeVoiceLimits.MinimumSilenceMilliseconds, RealtimeVoiceLimits.MaximumSilenceMilliseconds);

		double absoluteStart = 0.09 - (clampedSensitivity * 0.075);
		double startMultiplier = 4.5 - (clampedSensitivity * 2.8);
		double continueMultiplier = Math.Max(1.2, startMultiplier * _continueThresholdRatio);

		int windowFrames = Math.Max(1, (int)((long)sampleRate * AnalysisWindowMilliseconds / 1000L));
		bool speechStarted = false;
		bool speechEnded = false;
		bool evaluatedWindow = false;
		double lastWindowRms = 0;
		double chunkSumSq = 0;
		int chunkSampleCount = 0;

		int frameOffset = 0;
		while (frameOffset < totalFrames)
		{
			int needed = windowFrames - _windowFrames;
			int take = Math.Min(needed, totalFrames - frameOffset);
			int startSample = frameOffset * channelCount;
			int sampleLen = take * channelCount;
			for (int index = 0; index < sampleLen; index++)
			{
				float raw = interleaved[startSample + index];
				if (!float.IsFinite(raw)) continue;
				double sample = raw;
				double sq = sample * sample;
				_windowSumSq += sq;
				chunkSumSq += sq;
			}
			_windowSampleCount += sampleLen;
			_windowFrames += take;
			chunkSampleCount += sampleLen;
			frameOffset += take;

			if (_windowFrames < windowFrames) continue;

			double windowRms = _windowSampleCount > 0 ? Math.Sqrt(_windowSumSq / _windowSampleCount) : 0;
			int evaluatedFrames = _windowFrames;
			ResetWindowAccumulators();
			lastWindowRms = windowRms;
			evaluatedWindow = true;

			(double startThreshold, double continueThreshold) = UpdateThresholds(absoluteStart, startMultiplier, continueMultiplier);
			double activeThreshold = _latched ? continueThreshold : startThreshold;
			bool voiced = windowRms >= activeThreshold;

			// 开口期间与语音子窗内不学习噪声底，防止持续说话或句中轻声把门槛抬高切断自己。
			if (!_latched && !voiced) AdaptNoise(windowRms);

			if (voiced)
			{
				_silentFrames = 0;
				_voicedFrames += evaluatedFrames;
				if (!_latched && _voicedFrames * 1000L >= (long)_minimumSpeechMilliseconds * sampleRate)
				{
					_latched = true;
					speechStarted = true;
				}
			}
			else if (_latched && holdOpen)
			{
				_silentFrames = 0;
			}
			else if (!_latched)
			{
				_voicedFrames = 0;
			}
			else
			{
				_silentFrames += evaluatedFrames;
				if (_silentFrames * 1000L >= (long)silence * sampleRate)
				{
					_voicedFrames = 0;
					_silentFrames = 0;
					_latched = false;
					speechEnded = true;
				}
			}
		}

		if (!evaluatedWindow)
			UpdateThresholds(absoluteStart, startMultiplier, continueMultiplier);

		double decisionRms = evaluatedWindow
			? lastWindowRms
			: (chunkSampleCount > 0 ? Math.Sqrt(chunkSumSq / chunkSampleCount) : 0);
		return new EnergyVoiceDecision(speechStarted, speechEnded, _latched, decisionRms);
	}

	private (double StartThreshold, double ContinueThreshold) UpdateThresholds(
		double absoluteStart,
		double startMultiplier,
		double continueMultiplier)
	{
		double startThreshold = Math.Max(absoluteStart, _noiseFloor * startMultiplier);
		double continueThreshold = Math.Min(startThreshold, Math.Max(absoluteStart * _continueThresholdRatio, _noiseFloor * continueMultiplier));
		StartThreshold = startThreshold;
		ContinueThreshold = continueThreshold;
		return (startThreshold, continueThreshold);
	}

	private void ResetWindowAccumulators()
	{
		_windowFrames = 0;
		_windowSampleCount = 0;
		_windowSumSq = 0;
	}

	private void AdaptNoise(double rms)
	{
		if (!double.IsFinite(rms)) return;
		_noiseFloor = (_noiseFloor * (1.0 - _noiseAdaptRate)) + (rms * _noiseAdaptRate);
		_noiseFloor = Math.Clamp(_noiseFloor, 0.001, 0.2);
	}
}
