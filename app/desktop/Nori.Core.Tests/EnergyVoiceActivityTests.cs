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
