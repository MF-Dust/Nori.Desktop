using Nori.Core.Voice;
using Nori.Core.Voice.Audio;

namespace Nori.Core.Tests;

/// <summary>
/// 原生录音的编排层。
///
/// 和播放那边一样：只有设备需要原生实现，攒缓冲、下混、重采样、封 WAV 都是普通
/// 托管代码，用假设备测完。这一族尤其要盯住**产出的格式** —— 它直接决定上传给
/// Whisper 的体积，也决定识别质量。
/// </summary>
public sealed class NativeMicrophoneRecorderTests
{
	/// <summary>按给定格式吐固定样本的假麦克风。</summary>
	private sealed class FakeMicrophone(AudioFormat format, float[] samples) : IAudioCaptureDevice
	{
		private int _at;
		private volatile bool _stopped;

		public bool Opened { get; private set; }
		public bool DisposedOnce { get; private set; }

		/// <summary>读完之后是否继续阻塞（模拟「还没说完」），而不是直接结束。</summary>
		public bool BlockWhenDrained { get; set; }

		public AudioFormat Open()
		{
			Opened = true;
			return format;
		}

		public int Read(Span<float> buffer, CancellationToken cancellationToken)
		{
			if (_stopped) return 0;
			if (_at >= samples.Length)
			{
				if (!BlockWhenDrained) return 0;
				// 真麦克风在没有新数据时就是这样等着。
				while (!_stopped && !cancellationToken.IsCancellationRequested) Thread.Sleep(5);
				return 0;
			}
			int take = Math.Min(buffer.Length, samples.Length - _at);
			samples.AsSpan(_at, take).CopyTo(buffer);
			_at += take;
			return take;
		}

		public void Stop() => _stopped = true;

		public void Dispose() => DisposedOnce = true;
	}

	private static float[] Tone(int frames, int channels, float amplitude = 0.4f)
	{
		float[] samples = new float[frames * channels];
		for (int index = 0; index < samples.Length; index++)
			samples[index] = amplitude * MathF.Sin(index * 0.03f);
		return samples;
	}

	[Fact]
	public async Task 录完产出十六千单声道的WAV()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 2), Tone(48000, 2));
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		await recorder.StartAsync();
		await Task.Delay(60);
		RecordedAudio recorded = await recorder.StopAsync();

		Assert.Equal("audio/wav", recorded.Mime);
		Assert.EndsWith(".wav", recorded.FileName, StringComparison.Ordinal);

		PcmAudio decoded = WaveDecoder.Decode(recorded.Bytes);
		Assert.Equal(NativeMicrophoneRecorder.TargetSampleRate, decoded.SampleRate);
		Assert.Equal(1, decoded.Channels);
		Assert.True(microphone.Opened);
		Assert.True(microphone.DisposedOnce);
	}

	/// <summary>
	/// 体积是这一步的重点：48 kHz 立体声直传的话，30 秒就是 5.6 MB。
	/// 降到 16 kHz 单声道之后应当只有约九分之一。
	/// </summary>
	[Fact]
	public async Task 上传体积按十六千单声道算()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 2), Tone(48000, 2));   // 1 秒
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		await recorder.StartAsync();
		await Task.Delay(60);
		RecordedAudio recorded = await recorder.StopAsync();

		// 1 秒 × 16000 × 2 字节 + 44 字节头。允许边界上几帧的出入。
		Assert.InRange(recorded.Bytes.Length, 31000, 33000);
	}

	[Fact]
	public async Task 录音状态起落()
	{
		FakeMicrophone microphone = new(new AudioFormat(44100, 1), Tone(4410, 1)) {BlockWhenDrained = true};
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		Assert.False(recorder.IsRecording);
		await recorder.StartAsync();
		Assert.True(recorder.IsRecording);

		await recorder.StopAsync();
		Assert.False(recorder.IsRecording);
	}

	/// <summary>Stop 要能把**卡在 Read 里**的采集放出来，而不是等它超时。</summary>
	[Fact]
	public async Task 停止能打断等待中的读取()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 1), Tone(4800, 1)) {BlockWhenDrained = true};
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		await recorder.StartAsync();
		await Task.Delay(80);

		Task<RecordedAudio> stopping = recorder.StopAsync();
		RecordedAudio recorded = await stopping.WaitAsync(TimeSpan.FromSeconds(3));

		Assert.NotEmpty(recorded.Bytes);
	}

	[Fact]
	public async Task 没在录的时候停会报错()
	{
		using NativeMicrophoneRecorder recorder = new(() => new FakeMicrophone(new AudioFormat(48000, 1), []));

		await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StopAsync());
	}

	[Fact]
	public async Task 重复开始会报错()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 1), Tone(4800, 1)) {BlockWhenDrained = true};
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		await recorder.StartAsync();
		await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync());
		await recorder.StopAsync();
	}

	/// <summary>一段没说话的录音也要是合法 WAV，不能让上传端拿到空文件。</summary>
	[Fact]
	public async Task 没采到样本也产出合法WAV()
	{
		FakeMicrophone microphone = new(new AudioFormat(48000, 1), []);
		using NativeMicrophoneRecorder recorder = new(() => microphone);

		await recorder.StartAsync();
		RecordedAudio recorded = await recorder.StopAsync();

		Assert.True(WaveDecoder.IsWave(recorded.Bytes));
		PcmAudio decoded = WaveDecoder.Decode(recorded.Bytes);
		Assert.Equal(NativeMicrophoneRecorder.TargetSampleRate, decoded.SampleRate);
	}

	// ── 下混与重采样 ───────────────────────────────────────────────────────

	[Fact]
	public void 立体声下混成单声道取两边平均()
	{
		PcmAudio stereo = new()
		{
			Samples = [1f, 0f, 0.5f, -0.5f],      // 两帧：(1,0) 与 (0.5,−0.5)
			SampleRate = 16000,
			Channels = 2,
		};

		PcmAudio mono = NativeMicrophoneRecorder.Downmix(stereo, 16000);

		Assert.Equal(1, mono.Channels);
		Assert.Equal(2, mono.Samples.Length);
		Assert.Equal(0.5f, mono.Samples[0], 3);
		Assert.Equal(0f, mono.Samples[1], 3);
	}

	/// <summary>
	/// 降采样用**区间平均**不用最近邻。
	///
	/// 48000 → 16000 是三取一：最近邻等于直接丢掉三分之二的样本，高频会折回来变成
	/// 嘶声。这条用一段每三个样本里只有一个非零的信号来钉住 —— 最近邻会时而全丢
	/// 时而全取，平均则处处得到同一个值。
	/// </summary>
	[Fact]
	public void 降采样是区间平均而不是丢样本()
	{
		float[] spikes = new float[48];
		for (int index = 0; index < spikes.Length; index += 3) spikes[index] = 0.9f;

		PcmAudio mono = NativeMicrophoneRecorder.Downmix(
			new PcmAudio {Samples = spikes, SampleRate = 48000, Channels = 1}, 16000);

		Assert.Equal(16, mono.Samples.Length);
		// 每一窗覆盖三个源样本，其中恰有一个是 0.9 → 平均 0.3。
		Assert.All(mono.Samples, sample => Assert.Equal(0.3f, sample, 2));
	}

	[Fact]
	public void 采样率一致时长度不变()
	{
		PcmAudio source = new() {Samples = [0.1f, 0.2f, 0.3f], SampleRate = 16000, Channels = 1};

		PcmAudio mono = NativeMicrophoneRecorder.Downmix(source, 16000);

		Assert.Equal(3, mono.Samples.Length);
		Assert.Equal(0.2f, mono.Samples[1], 3);
	}

	[Fact]
	public void 空输入下混之后仍是合法形状()
	{
		PcmAudio empty = NativeMicrophoneRecorder.Downmix(
			new PcmAudio {Samples = [], SampleRate = 48000, Channels = 2}, 16000);

		Assert.Empty(empty.Samples);
		Assert.Equal(1, empty.Channels);
		Assert.Equal(16000, empty.SampleRate);
	}
}
