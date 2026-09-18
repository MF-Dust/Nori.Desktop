using Nori.Core.Voice.Audio;

namespace Nori.Core.Tests;

public sealed class PcmResamplerTests
{
	[Theory]
	[InlineData(48000, 16000, 1000)]
	[InlineData(48000, 16000, 6000)]
	[InlineData(44100, 16000, 4000)]
	[InlineData(22050, 48000, 4000)]
	public void 通带保持频率与幅度(int sourceRate, int targetRate, int frequency)
	{
		PcmAudio source = Tone(sourceRate, frequency);
		(float[] samples, float[] level) = PcmResampler.Prepare(source, new(targetRate, 1), CancellationToken.None);

		Assert.Equal(targetRate, samples.Length);
		Assert.Equal(samples, level);
		double error = 0;
		// 边界窗截断会产生瞬态，频响检查取远离边界的稳定区间。
		for (int frame = 256; frame < samples.Length - 256; frame++)
		{
			double expected = 0.5 * Math.Sin(2 * Math.PI * frequency * frame / targetRate);
			error += Math.Pow(samples[frame] - expected, 2);
		}
		Assert.InRange(Math.Sqrt(error / (samples.Length - 512)), 0, 0.002);
	}

	[Theory]
	[InlineData(48000, 16000, 8500)]
	[InlineData(48000, 16000, 12000)]
	[InlineData(44100, 16000, 10000)]
	[InlineData(48000, 22050, 14000)]
	public void 降采样抑制目标奈奎斯特频率以上的混叠(int sourceRate, int targetRate, int frequency)
	{
		(float[] samples, _) = PcmResampler.Prepare(Tone(sourceRate, frequency), new(targetRate, 1), CancellationToken.None);

		double energy = 0;
		for (int frame = 256; frame < samples.Length - 256; frame++)
			energy += samples[frame] * samples[frame];
		// 输入 RMS 为约 0.354，要求阻带至少衰减约 50 dB。
		Assert.InRange(Math.Sqrt(energy / (samples.Length - 512)), 0, 0.001);
	}

	[Theory]
	[InlineData(24000, 48000, 120001, 240002)]
	[InlineData(44100, 48000, 441007, 480007)]
	[InlineData(48000, 16000, 480007, 160002)]
	public void 长片段帧数精确且直流幅度稳定(int sourceRate, int targetRate, int sourceFrames, int targetFrames)
	{
		PcmAudio source = new() { Samples = Enumerable.Repeat(0.25f, sourceFrames).ToArray(), SampleRate = sourceRate, Channels = 1 };
		(float[] samples, float[] level) = PcmResampler.Prepare(source, new(targetRate, 1), CancellationToken.None);

		Assert.Equal(targetFrames, samples.Length);
		Assert.Equal(targetFrames, level.Length);
		Assert.All(samples, sample => Assert.Equal(0.25f, sample));
		Assert.Equal(samples, level);
	}

	[Fact]
	public void 升采样抑制源采样率产生的频谱镜像()
	{
		(float[] samples, _) = PcmResampler.Prepare(Tone(16000, 4000), new(48000, 1), CancellationToken.None);
		double real = 0;
		double imaginary = 0;
		// 4000 Hz 在 16000 Hz 采样后的首个镜像位于 12000 Hz。
		// 取整周期区间，避免泄漏把通带信号误计为镜像。
		for (int frame = 480; frame < samples.Length - 480; frame++)
		{
			double angle = 2 * Math.PI * 12000 * frame / 48000;
			real += samples[frame] * Math.Cos(angle);
			imaginary += samples[frame] * Math.Sin(angle);
		}
		double amplitude = 2 * Math.Sqrt(real * real + imaginary * imaginary) / (samples.Length - 960);
		Assert.InRange(amplitude, 0, 0.001);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(8)]
	public void 重采样保留左右相位及独立下混电平(int targetChannels)
	{
		PcmAudio mono = Tone(22050, 1000);
		float[] stereo = new float[mono.Samples.Length * 2];
		for (int frame = 0; frame < mono.FrameCount; frame++)
		{
			stereo[frame * 2] = mono.Samples[frame];
			stereo[frame * 2 + 1] = -mono.Samples[frame];
		}
		PcmAudio source = new() { Samples = stereo, SampleRate = mono.SampleRate, Channels = 2 };
		(float[] samples, float[] level) = PcmResampler.Prepare(source, new(48000, targetChannels), CancellationToken.None);

		Assert.Equal(48000 * targetChannels, samples.Length);
		Assert.All(level, value => Assert.Equal(0f, value));
		Assert.Contains(samples, value => Math.Abs(value) > 0.4);
		for (int frame = 0; frame < level.Length; frame++)
		{
			if (targetChannels > 1) Assert.Equal(samples[frame * targetChannels], -samples[frame * targetChannels + 1]);
			for (int channel = 2; channel < targetChannels; channel++)
				Assert.Equal(0f, samples[frame * targetChannels + channel]);
		}
	}

	[Theory]
	[InlineData(8000, 48000, 6)]
	[InlineData(48000, 8000, 1)]
	public void 单帧输入保留幅度并只铺前置声道(int sourceRate, int targetRate, int frames)
	{
		PcmAudio source = new() { Samples = [0.375f], SampleRate = sourceRate, Channels = 1 };
		(float[] samples, float[] level) = PcmResampler.Prepare(source, new(targetRate, 8), CancellationToken.None);

		Assert.Equal(frames, level.Length);
		Assert.All(level, sample => Assert.Equal(0.375f, sample));
		for (int frame = 0; frame < frames; frame++)
		{
			Assert.Equal(0.375f, samples[frame * 8]);
			Assert.Equal(0.375f, samples[frame * 8 + 1]);
			for (int channel = 2; channel < 8; channel++) Assert.Equal(0f, samples[frame * 8 + channel]);
		}
	}

	[Fact]
	public void 同采样率样本逐位不变且电平按源声道下混()
	{
		PcmAudio source = new() { Samples = [0.1f, -0.2f, 0.3f, 0.4f], SampleRate = 48000, Channels = 2 };
		(float[] samples, float[] level) = PcmResampler.Prepare(source, new(48000, 2), CancellationToken.None);

		Assert.Equal(source.Samples, samples);
		Assert.Equal((float) ((0.1f + (double) -0.2f) / 2), level[0]);
		Assert.Equal((float) ((0.3f + (double) 0.4f) / 2), level[1]);
	}

	[Fact]
	public void 空片段不生成输出()
	{
		PcmAudio source = new() { Samples = [], SampleRate = 22050, Channels = 1 };
		(float[] samples, float[] level) = PcmResampler.Prepare(source, new(48000, 2), CancellationToken.None);

		Assert.Empty(samples);
		Assert.Empty(level);
	}

	[Fact]
	public void 已取消的转换不分配输出()
	{
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();
		PcmAudio source = new() { Samples = [0.5f], SampleRate = 1, Channels = 1 };

		Assert.Throws<OperationCanceledException>(() => PcmResampler.Prepare(source, new(int.MaxValue, 8), cancellation.Token));
	}

	[Fact]
	public async Task 长片段转换途中可取消()
	{
		using CancellationTokenSource cancellation = new();
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PcmAudio source = new() { Samples = new float[48000 * 120], SampleRate = 48000, Channels = 1 };
		Task conversion = Task.Run(() =>
		{
			entered.SetResult();
			PcmResampler.Prepare(source, new(44100, 1), cancellation.Token);
		});
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversion.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	private static PcmAudio Tone(int sampleRate, int frequency)
	{
		float[] samples = new float[sampleRate];
		for (int frame = 0; frame < samples.Length; frame++)
			samples[frame] = (float) (0.5 * Math.Sin(2 * Math.PI * frequency * frame / sampleRate));
		return new PcmAudio { Samples = samples, SampleRate = sampleRate, Channels = 1 };
	}
}
