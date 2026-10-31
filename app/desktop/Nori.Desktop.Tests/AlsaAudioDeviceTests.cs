using Nori.Core.Voice.Audio;
using Nori.Desktop.Audio.Linux;

namespace Nori.Desktop.Tests;

/// <summary>用 PCM 替身校验 ALSA 设备的格式、音量缩放和停止打断。</summary>
public sealed class AlsaAudioDeviceTests
{
	[Fact]
	public void 输出返回协商后的格式并按音量写成十六位()
	{
		FakePcm pcm = new(new AudioFormat(48_000, 1));
		using AlsaAudioDevice device = new(new FakeFactory(pcm));
		device.Volume = 0.5;

		AudioFormat format = device.Open(16_000, 2);

		Assert.Equal(new AudioFormat(48_000, 1), format);
		Assert.Equal(2, device.Write([1f, -1f], CancellationToken.None));
		Assert.Equal((short) Math.Round(0.5 * 32767), pcm.Written[0]);
		Assert.Equal((short) Math.Round(-0.5 * 32767), pcm.Written[1]);
	}

	[Fact]
	public async Task 输出等待被停止打断()
	{
		FakePcm pcm = new(new AudioFormat(48_000, 1)) {Ready = false};
		using AlsaAudioDevice device = new(new FakeFactory(pcm));
		device.Open(48_000, 1);
		Task<int> pending = Task.Run(() => device.Write([1f, 2f, 3f, 4f], CancellationToken.None));
		await WaitUntilAsync(() => pcm.Waits > 0);
		device.Stop();
		int written = await pending.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(0, written);
		Assert.True(pcm.Dropped);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task 播放收尾可由停止或取消打断(bool cancel)
	{
		FakePcm pcm = new(new AudioFormat(48_000, 1)) { DrainPendingAttempts = int.MaxValue };
		using AlsaAudioDevice device = new(new FakeFactory(pcm));
		using CancellationTokenSource cancellation = new();
		device.Open(48_000, 1);
		Task pending = Task.Run(() => device.Drain(cancellation.Token));
		await WaitUntilAsync(() => pcm.DrainAttempts > 0);

		if (cancel) cancellation.Cancel();
		else await Task.Run(device.Stop).WaitAsync(TimeSpan.FromSeconds(2));

		await pending.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(!cancel, pcm.Dropped);
	}

	[Fact]
	public void 播放收尾等待剩余缓冲且不丢弃样本()
	{
		FakePcm pcm = new(new AudioFormat(48_000, 1)) { DrainPendingAttempts = 2 };
		using AlsaAudioDevice device = new(new FakeFactory(pcm));
		device.Open(48_000, 1);

		device.Drain(CancellationToken.None);

		Assert.Equal(3, pcm.DrainAttempts);
		Assert.False(pcm.Dropped);
	}

	[Fact]
	public void 播放收尾失败不会伪装成完成()
	{
		FakePcm pcm = new(new AudioFormat(48_000, 1)) { DrainError = true };
		using AlsaAudioDevice device = new(new FakeFactory(pcm));
		device.Open(48_000, 1);

		Assert.Throws<AudioDeviceException>(() => device.Drain(CancellationToken.None));
	}

	[Fact]
	public void 输出重试暂不可写并接续部分写入()
	{
		FakePcm pcm = new(new AudioFormat(48_000, 2)) { WriteRetries = 1, MaxWriteFrames = 1 };
		using AlsaAudioDevice device = new(new FakeFactory(pcm));
		device.Open(48_000, 2);

		Assert.Equal(4, device.Write([1f, -1f, 0.5f, -0.5f], CancellationToken.None));
		Assert.Equal(new short[] { 32767, -32767, 16384, -16384 }, pcm.Written);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task 输入输出等待可被取消(bool capture)
	{
		FakePcm pcm = new(new AudioFormat(48_000, 1)) { Ready = false };
		using AlsaAudioDevice? output = capture ? null : new(new FakeFactory(pcm));
		using AlsaCaptureDevice? input = capture ? new(new FakeFactory(pcm)) : null;
		using CancellationTokenSource cancellation = new();
		if (capture) input!.Open();
		else output!.Open(48_000, 1);
		Task<int> pending = Task.Run(() => capture
			? input!.Read(new float[2], cancellation.Token)
			: output!.Write([1f, -1f], cancellation.Token));
		await WaitUntilAsync(() => pcm.Waits > 0);

		cancellation.Cancel();

		Assert.Equal(0, await pending.WaitAsync(TimeSpan.FromSeconds(2)));
	}

	[Fact]
	public void 输入把十六位样本还原成浮点()
	{
		FakePcm pcm = new(new AudioFormat(44_100, 1));
		pcm.Captured.AddRange([(short) 16384, (short) -16384]);
		using AlsaCaptureDevice device = new(new FakeFactory(pcm));
		Assert.Equal(new AudioFormat(44_100, 1), device.Open());
		float[] got = new float[2];
		Assert.Equal(2, device.Read(got, CancellationToken.None));
		Assert.True(got[0] > 0.4f);
		Assert.True(got[1] < -0.4f);
	}

	[Fact]
	public void 输入先触发采集再等待数据就绪()
	{
		FakePcm pcm = new(new AudioFormat(48_000, 1)) { RequireCaptureStart = true };
		pcm.Captured.AddRange([(short)16384, (short)-16384]);
		using AlsaCaptureDevice device = new(new FakeFactory(pcm));
		using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(2));
		device.Open();
		float[] samples = new float[2];

		Assert.Equal(2, device.Read(samples, cancellation.Token));
		Assert.True(pcm.Waits > 0);
		Assert.True(samples[0] > 0.4f);
		Assert.True(samples[1] < -0.4f);
	}

	[Fact]
	public async Task 输入等待被停止打断()
	{
		FakePcm pcm = new(new AudioFormat(48_000, 1)) {Ready = false};
		using AlsaCaptureDevice device = new(new FakeFactory(pcm));
		device.Open();
		Task<int> pending = Task.Run(() => device.Read(new float[2], CancellationToken.None));
		await WaitUntilAsync(() => pcm.Waits > 0);
		device.Stop();
		Assert.Equal(0, await pending.WaitAsync(TimeSpan.FromSeconds(2)));
	}

	[Fact]
	public void 不可恢复的等待错误不会伪装成停止()
	{
		FakePcm pcm = new(new AudioFormat(48_000, 1)) { WaitError = true };
		using AlsaAudioDevice output = new(new FakeFactory(pcm));
		output.Open(48_000, 1);
		Assert.Throws<AudioDeviceException>(() => output.Write([1f], CancellationToken.None));

		FakePcm inputPcm = new(new AudioFormat(48_000, 1)) { WaitError = true };
		using AlsaCaptureDevice input = new(new FakeFactory(inputPcm));
		input.Open();
		Assert.Throws<AudioDeviceException>(() => input.Read(new float[1], CancellationToken.None));
	}

	[Fact]
	public void 非Linux进程打不开系统库时同样提示安装()
	{
		if (System.OperatingSystem.IsLinux()) return;
		using AlsaAudioDevice device = new();
		AudioDeviceException error = Assert.Throws<AudioDeviceException>(() => device.Open(48_000, 1));
		Assert.Contains("libasound2", error.Message, StringComparison.Ordinal);
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
		while (!condition())
			await Task.Delay(10, timeout.Token);
	}

	private sealed class FakeFactory(FakePcm pcm) : IAlsaFactory
	{
		public IAlsaPcm OpenOutput(int sampleRate, int channels)
		{
			_ = sampleRate;
			_ = channels;
			return pcm;
		}

		public IAlsaPcm OpenInput(int sampleRate, int channels)
		{
			_ = sampleRate;
			_ = channels;
			return pcm;
		}
	}

	private sealed class FakePcm(AudioFormat format) : IAlsaPcm
	{
		private readonly ManualResetEventSlim _stopped = new(false);
		public AudioFormat Format { get; } = format;
		public List<short> Written { get; } = [];
		public List<short> Captured { get; } = [];
		public bool Ready { get; set; } = true;
		public bool RequireCaptureStart { get; set; }
		private bool _captureStarted;
		public bool WaitError { get; set; }
		public bool Dropped { get; private set; }
		public int Waits { get; private set; }
		public int WriteRetries { get; set; }
		public int MaxWriteFrames { get; set; } = int.MaxValue;
		public int DrainPendingAttempts { get; set; }
		public bool DrainError { get; set; }
		private int _drainAttempts;
		public int DrainAttempts => Volatile.Read(ref _drainAttempts);

		public int Wait(int milliseconds)
		{
			Waits++;
			if (WaitError) return -1;
			if (_stopped.IsSet) return -1;
			if (Ready && (!RequireCaptureStart || _captureStarted)) return 1;
			_stopped.Wait(milliseconds);
			return _stopped.IsSet ? -1 : 0;
		}

		public int Write(ReadOnlySpan<short> interleaved)
		{
			if (WriteRetries > 0)
			{
				WriteRetries--;
				return 0;
			}
			int frames = Math.Min(interleaved.Length / Format.Channels, MaxWriteFrames);
			Written.AddRange(interleaved[..(frames * Format.Channels)].ToArray());
			return frames;
		}

		public int Read(Span<short> interleaved)
		{
			if (RequireCaptureStart && !_captureStarted)
			{
				_captureStarted = true;
				return 0;
			}
			int take = Math.Min(interleaved.Length, Captured.Count);
			for (int index = 0; index < take; index++) interleaved[index] = Captured[index];
			Captured.RemoveRange(0, take);
			return take / Math.Max(1, Format.Channels);
		}

		public void Drop()
		{
			Dropped = true;
			_stopped.Set();
		}

		public bool TryDrain()
		{
			if (DrainError) throw new AudioDeviceException("等待 ALSA 播放完成失败");
			return Interlocked.Increment(ref _drainAttempts) > DrainPendingAttempts;
		}

		public void Dispose() => _stopped.Dispose();
	}
}
