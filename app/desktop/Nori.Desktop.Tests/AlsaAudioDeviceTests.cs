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
		public bool Dropped { get; private set; }
		public int Waits { get; private set; }

		public int Wait(int milliseconds)
		{
			Waits++;
			if (_stopped.IsSet) return -1;
			if (Ready) return 1;
			_stopped.Wait(milliseconds);
			return _stopped.IsSet ? -1 : 0;
		}

		public int Write(ReadOnlySpan<short> interleaved)
		{
			Written.AddRange(interleaved.ToArray());
			return interleaved.Length / Math.Max(1, Format.Channels);
		}

		public int Read(Span<short> interleaved)
		{
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

		public void Drain() { }

		public void Dispose() => _stopped.Dispose();
	}
}
