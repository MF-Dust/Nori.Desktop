using Nori.Core.Voice.Realtime;

namespace Nori.Core.Tests;

/// <summary>本地能量检测。时间只由样本数推进。</summary>
public sealed class EnergyVoiceActivityTests
{
	private const int SampleRate = 16000;

	[Fact]
	public void 静音和低噪声不会开口()
	{
		EnergyVoiceActivity silence = new();
		EnergyVoiceActivity noise = new();
		int silenceEnds = CountEnds(silence, 5_000, 0, 0.5, 700);
		int noiseEnds = CountEnds(noise, 5_000, 0.004f, 0.5, 700);
		Assert.Equal(0, silenceEnds);
		Assert.Equal(0, noiseEnds);
		Assert.False(silence.InSpeech);
		Assert.False(noise.InSpeech);
	}

	[Fact]
	public void 短脉冲不当成一句话()
	{
		EnergyVoiceActivity activity = new();
		int ends = CountEnds(activity, 40, 0.4f, 0.5, 700);
		Assert.Equal(0, ends);
		Assert.False(activity.InSpeech);
	}

	[Fact]
	public void 说话后静音结束且灵敏度会改变门槛()
	{
		EnergyVoiceActivity dull = new();
		int dullEnds = CountEnds(dull, 400, 0.05f, 0, 700);
		Assert.Equal(0, dullEnds);

		EnergyVoiceActivity sensitive = new();
		int starts = 0;
		int ends = 0;
		Feed(sensitive, 300, 0.05f, 1, 700, ref starts, ref ends);
		Feed(sensitive, 800, 0, 1, 700, ref starts, ref ends);
		Assert.Equal(1, starts);
		Assert.Equal(1, ends);

		EnergyVoiceActivity normal = new();
		starts = 0;
		ends = 0;
		Feed(normal, 300, 0.4f, 0.5, 700, ref starts, ref ends);
		Feed(normal, 400, 0, 0.5, 700, ref starts, ref ends);
		Assert.Equal(1, starts);
		Assert.Equal(0, ends);
		Feed(normal, 400, 0, 0.5, 700, ref starts, ref ends);
		Assert.Equal(1, ends);
	}

	[Fact]
	public void 持续说话不会被噪声底提前切断()
	{
		EnergyVoiceActivity activity = new();
		int starts = 0;
		int ends = 0;
		Feed(activity, 30_000, 0.4f, 0.5, 700, ref starts, ref ends);
		Assert.Equal(1, starts);
		Assert.Equal(0, ends);
		Assert.True(activity.InSpeech);
	}

	[Fact]
	public void 持续噪声最多收成一句()
	{
		EnergyVoiceActivity activity = new();
		int ends = CountEnds(activity, 4_000, 0.08f, 0.5, 700);
		Assert.InRange(ends, 0, 1);
	}

	[Fact]
	public void 按住时已开口的静音不会被切断()
	{
		EnergyVoiceActivity activity = new();
		int starts = 0;
		int ends = 0;
		Feed(activity, 300, 0.4f, 0.5, 700, ref starts, ref ends, holdOpen: true);
		Feed(activity, 2_000, 0, 0.5, 700, ref starts, ref ends, holdOpen: true);
		Assert.Equal(1, starts);
		Assert.Equal(0, ends);
		Assert.True(activity.InSpeech);
	}

	[Fact]
	public void 连续短促键盘鼠标脉冲不会误触发开口且可配置确认窗口()
	{
		EnergyVoiceActivity activity = new();
		int starts = 0;
		int ends = 0;
		for (int click = 0; click < 10; click++)
		{
			Feed(activity, 30, 0.9f, 0.5, 700, ref starts, ref ends);
			Feed(activity, 40, 0f, 0.5, 700, ref starts, ref ends);
		}
		Feed(activity, 800, 0f, 0.5, 700, ref starts, ref ends);
		Assert.Equal(0, starts);
		Assert.Equal(0, ends);
		Assert.False(activity.InSpeech);

		EnergyVoiceActivity fastAttack = new(new EnergyVoiceActivityOptions { MinimumSpeechMilliseconds = 80 });
		starts = 0;
		ends = 0;
		Feed(fastAttack, 40, 0.9f, 0.5, 700, ref starts, ref ends);
		Feed(fastAttack, 60, 0f, 0.5, 700, ref starts, ref ends);
		Assert.Equal(0, starts);

		Feed(fastAttack, 100, 0.4f, 0.5, 700, ref starts, ref ends);
		Assert.Equal(1, starts);
		Assert.True(fastAttack.InSpeech);
	}

	[Fact]
	public void 双阈值迟滞使开口后轻声续说不中断()
	{
		// 0.055f 正弦波 RMS 约为 0.0389，位于灵敏度 0.5 的 ContinueThreshold(0.0315) 与 StartThreshold(0.0525) 之间。
		EnergyVoiceActivity baseline = new();
		int baseStarts = 0;
		int baseEnds = 0;
		Feed(baseline, 1_000, 0.055f, 0.5, 700, ref baseStarts, ref baseEnds);
		Assert.Equal(0, baseStarts);

		EnergyVoiceActivity activity = new();
		int starts = 0;
		int ends = 0;
		Feed(activity, 260, 0.2f, 0.5, 700, ref starts, ref ends);
		Assert.Equal(1, starts);
		Assert.True(activity.StartThreshold > activity.ContinueThreshold);

		Feed(activity, 1_000, 0.055f, 0.5, 700, ref starts, ref ends);
		Assert.Equal(0, ends);
		Assert.True(activity.InSpeech);

		Feed(activity, 800, 0f, 0.5, 700, ref starts, ref ends);
		Assert.Equal(1, ends);
		Assert.False(activity.InSpeech);
	}

	[Fact]
	public void 句中短停顿不拆句且超过静音阈值后切段()
	{
		EnergyVoiceActivity activity = new();
		int starts = 0;
		int ends = 0;
		Feed(activity, 300, 0.4f, 0.5, 700, ref starts, ref ends);
		Feed(activity, 300, 0f, 0.5, 700, ref starts, ref ends);
		Assert.Equal(1, starts);
		Assert.Equal(0, ends);
		Assert.True(activity.InSpeech);

		Feed(activity, 300, 0.4f, 0.5, 700, ref starts, ref ends);
		Assert.Equal(1, starts);
		Assert.Equal(0, ends);

		Feed(activity, 800, 0f, 0.5, 700, ref starts, ref ends);
		Assert.Equal(1, starts);
		Assert.Equal(1, ends);
		Assert.False(activity.InSpeech);
	}

	[Fact]
	public void 两轮连续讲话边界清晰且重置语义明确()
	{
		EnergyVoiceActivity activity = new();
		int starts = 0;
		int ends = 0;
		Feed(activity, 300, 0.4f, 0.5, 700, ref starts, ref ends);
		Feed(activity, 800, 0f, 0.5, 700, ref starts, ref ends);
		Assert.Equal(1, starts);
		Assert.Equal(1, ends);

		Feed(activity, 300, 0.4f, 0.5, 700, ref starts, ref ends);
		Feed(activity, 800, 0f, 0.5, 700, ref starts, ref ends);
		Assert.Equal(2, starts);
		Assert.Equal(2, ends);

		Feed(activity, 300, 0.03f, 0.5, 700, ref starts, ref ends);
		double adaptedNoise = activity.NoiseFloor;
		Assert.NotEqual(0.01, adaptedNoise);
		activity.ResetSpeech();
		Assert.Equal(adaptedNoise, activity.NoiseFloor);
		activity.ResetAll();
		Assert.Equal(0.01, activity.NoiseFloor);
	}

	[Fact]
	public void 非有限样本不污染噪声底且奇异分块长度准确推进时间()
	{
		EnergyVoiceActivity activity = new();
		float[] dirty = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.01f];
		EnergyVoiceDecision dirtyDecision = activity.Push(dirty, 1, SampleRate, 0.5, 700, false);
		Assert.True(double.IsFinite(dirtyDecision.Rms));
		Assert.True(double.IsFinite(activity.NoiseFloor));

		const int rate441 = 44100;
		const int oddChunk = 137;
		int starts = 0;
		int ends = 0;
		int speechSamples = rate441 * 260 / 1000;
		for (int offset = 0; offset < speechSamples; offset += oddChunk)
		{
			int count = Math.Min(oddChunk, speechSamples - offset);
			float[] samples = new float[count];
			for (int index = 0; index < count; index++)
				samples[index] = 0.4f * MathF.Sin((offset + index) * 0.1f);
			EnergyVoiceDecision decision = activity.Push(samples, 1, rate441, 0.5, 700, false);
			if (decision.SpeechStarted) starts++;
			if (decision.SpeechEnded) ends++;
		}
		int silenceSamples = rate441 * 760 / 1000;
		for (int offset = 0; offset < silenceSamples; offset += oddChunk)
		{
			int count = Math.Min(oddChunk, silenceSamples - offset);
			EnergyVoiceDecision decision = activity.Push(new float[count], 1, rate441, 0.5, 700, false);
			if (decision.SpeechStarted) starts++;
			if (decision.SpeechEnded) ends++;
		}
		Assert.Equal(1, starts);
		Assert.Equal(1, ends);
	}

	[Fact]
	public void 精确帧阈值16kHz逐样本3200帧刚好触发开口()
	{
		EnergyVoiceActivity activity = new();
		float[] singleSample = [0.25f];
		int starts = 0;
		for (int frame = 1; frame <= 3199; frame++)
		{
			EnergyVoiceDecision decision = activity.Push(singleSample, 1, SampleRate, 0.5, 700, false);
			if (decision.SpeechStarted) starts++;
			Assert.False(activity.InSpeech);
		}
		Assert.Equal(0, starts);

		EnergyVoiceDecision trigger = activity.Push(singleSample, 1, SampleRate, 0.5, 700, false);
		Assert.True(trigger.SpeechStarted);
		Assert.True(trigger.InSpeech);
		Assert.True(activity.InSpeech);
	}

	[Fact]
	public void 大块内短时1ms满幅脉冲不会把整块21ms误判为连续发声()
	{
		const int rate48k = 48000;
		const int blockFrames = 1024; // ~21.33ms
		const int pulseFrames = 48;   // 1ms @ 48kHz
		EnergyVoiceActivity activity = new();
		int starts = 0;
		int ends = 0;

		float[] block = new float[blockFrames];
		for (int i = 0; i < pulseFrames; i++)
			block[i] = 1.0f;

		// 即使连续喂入 30 个这样的 1024 帧块（总计 >600ms），每块仅前 1ms 有脉冲，
		// 按 10ms 固定分析子窗评估后，静音子窗会清零累计发声，绝不会误触发开口。
		for (int round = 0; round < 30; round++)
		{
			EnergyVoiceDecision decision = activity.Push(block, 1, rate48k, 0.5, 700, false);
			if (decision.SpeechStarted) starts++;
			if (decision.SpeechEnded) ends++;
		}

		Assert.Equal(0, starts);
		Assert.Equal(0, ends);
		Assert.False(activity.InSpeech);
	}

	private static int CountEnds(EnergyVoiceActivity activity, int milliseconds, float amplitude, double sensitivity, int silence)
	{
		int starts = 0;
		int ends = 0;
		Feed(activity, milliseconds, amplitude, sensitivity, silence, ref starts, ref ends);
		return ends;
	}

	private static void Feed(
		EnergyVoiceActivity activity,
		int milliseconds,
		float amplitude,
		double sensitivity,
		int silence,
		ref int starts,
		ref int ends,
		bool holdOpen = false)
	{
		int frame = SampleRate / 50;
		int total = Math.Max(frame, SampleRate * milliseconds / 1000);
		bool silent = amplitude <= 0f;
		for (int offset = 0; offset < total; offset += frame)
		{
			int count = Math.Min(frame, total - offset);
			float[] samples = new float[count];
			for (int index = 0; index < count; index++)
				samples[index] = silent ? 0f : amplitude * MathF.Sin((offset + index) * 0.17f);
			EnergyVoiceDecision decision = activity.Push(samples, 1, SampleRate, sensitivity, silence, holdOpen);
			if (decision.SpeechStarted) starts++;
			if (decision.SpeechEnded) ends++;
		}
	}
}
