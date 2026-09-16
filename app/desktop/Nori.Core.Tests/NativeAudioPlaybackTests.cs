using Nori.Core.Voice;
using Nori.Core.Voice.Audio;

namespace Nori.Core.Tests;

/// <summary>
/// 原生播放的编排层。
///
/// 整条链路里只有设备那一层需要原生代码；解码、切块、发电平、停止、收尾都是普通
/// 托管代码。这一族拿一个假设备把它们全测掉 —— 不必等真声卡，也就不会出现
/// 「只能在某台机器上验」的逻辑。
/// </summary>
public sealed class NativeAudioPlaybackTests
{
	/// <summary>记账用的假设备。可以按需在 Write 上阻塞，用来测打断。</summary>
	private sealed class FakeDevice : IAudioDevice
	{
		private readonly ManualResetEventSlim _released = new(true);
		private volatile bool _stopped;

		public List<float> Written { get; } = [];
		public AudioFormat Opened { get; private set; }
		public bool Drained { get; private set; }
		public bool DisposedOnce { get; private set; }
		public double Volume { get; set; } = 1.0;

		/// <summary>设备实际接受的格式；不设就原样接受调用方要的。</summary>
		public AudioFormat? Force { get; set; }

		/// <summary>让 Write 阻塞，直到 Stop 或 <see cref="Release"/>。</summary>
		public bool BlockWrites { get; set; }

		public AudioFormat Open(int sampleRate, int channels)
		{
			Opened = Force ?? new AudioFormat(sampleRate, channels);
			return Opened;
		}

		public int Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
		{
			if (BlockWrites)
			{
				_released.Reset();
				// 真设备在缓冲满时就是这样等着；Stop 要能把它放出来。
				_released.Wait(TimeSpan.FromSeconds(5), cancellationToken);
				if (_stopped) return 0;
			}
			Written.AddRange(samples.ToArray());
			return samples.Length;
		}

		public void Drain(CancellationToken cancellationToken) => Drained = true;

		public void Stop()
		{
			_stopped = true;
			_released.Set();
		}

		public void Release() => _released.Set();

		public void Dispose()
		{
			DisposedOnce = true;
			_released.Dispose();
		}
	}

	private static PcmAudio Tone(int frames = 4410, int sampleRate = 44100, int channels = 1, float amplitude = 0.5f)
	{
		float[] samples = new float[frames * channels];
		for (int index = 0; index < samples.Length; index++)
			samples[index] = amplitude * MathF.Sin(index * 0.05f);
		return new PcmAudio {Samples = samples, SampleRate = sampleRate, Channels = channels};
	}

	private static NativeAudioPlayback Playback(FakeDevice device, PcmAudio audio) =>
		new(() => device, (_, _) => audio);

	private static EncodedAudio Bytes() => new([1, 2, 3], "audio/wav");

	[Fact]
	public async Task 整段样本都写进设备()
	{
		FakeDevice device = new();
		PcmAudio audio = Tone();
		using NativeAudioPlayback playback = Playback(device, audio);

		await playback.PlayAsync(Bytes(), CancellationToken.None);

		Assert.Equal(audio.Samples.Length, device.Written.Count);
		Assert.Equal(new AudioFormat(44100, 1), device.Opened);
		Assert.True(device.Drained);
		Assert.True(device.DisposedOnce);
	}

	[Fact]
	public async Task 播放状态起落各一次()
	{
		FakeDevice device = new();
		using NativeAudioPlayback playback = Playback(device, Tone(frames: 441));
		List<bool> states = [];
		playback.PlayingChanged += states.Add;

		await playback.PlayAsync(Bytes(), CancellationToken.None);

		Assert.Equal([true, false], states);
		Assert.False(playback.IsPlaying);
	}

	/// <summary>电平是驱动口型的输入，必须一窗一条地出来。</summary>
	[Fact]
	public async Task 每一窗发一条电平()
	{
		FakeDevice device = new();
		PcmAudio audio = Tone(frames: 44100);      // 1 秒
		using NativeAudioPlayback playback = Playback(device, audio);
		List<double> levels = [];
		playback.VolumeSampled += levels.Add;

		await playback.PlayAsync(Bytes(), CancellationToken.None);

		int window = PcmLevel.WindowSamples(44100, 1);
		int expected = (int) Math.Ceiling((double) audio.Samples.Length / window);
		// 收尾那一条 0 也算在内。
		Assert.Equal(expected + 1, levels.Count);
		Assert.All(levels, level => Assert.InRange(level, 0, 1));
	}

	/// <summary>
	/// 收尾必须把嘴合上。少这一条，播放被打断时口型会停在张开的那一帧，
	/// 而用户看到的是「她卡住了」。
	/// </summary>
	[Fact]
	public async Task 收尾时电平回到零()
	{
		FakeDevice device = new();
		using NativeAudioPlayback playback = Playback(device, Tone(frames: 4410, amplitude: 0.9f));
		List<double> levels = [];
		playback.VolumeSampled += levels.Add;

		await playback.PlayAsync(Bytes(), CancellationToken.None);

		Assert.Equal(0, levels[^1]);
		Assert.Contains(levels.Take(levels.Count - 1), level => level > 0.1);
	}

	/// <summary>静音不该让嘴动。</summary>
	[Fact]
	public async Task 静音段的电平始终为零()
	{
		FakeDevice device = new();
		PcmAudio silence = new() {Samples = new float[8820], SampleRate = 44100, Channels = 1};
		using NativeAudioPlayback playback = Playback(device, silence);
		List<double> levels = [];
		playback.VolumeSampled += levels.Add;

		await playback.PlayAsync(Bytes(), CancellationToken.None);

		Assert.All(levels, level => Assert.Equal(0, level, 6));
	}

	// ── 打断 ───────────────────────────────────────────────────────────────

	/// <summary>Stop 要能把**卡在 Write 里**的播放放出来，而不是等它自己超时。</summary>
	[Fact]
	public async Task 停止能打断卡在写入里的播放()
	{
		FakeDevice device = new() {BlockWrites = true};
		using NativeAudioPlayback playback = Playback(device, Tone(frames: 44100));

		Task playing = playback.PlayAsync(Bytes(), CancellationToken.None);
		// 等它真的进到阻塞里。
		await Task.Delay(80);
		Assert.True(playback.IsPlaying);

		playback.Stop();
		await playing.WaitAsync(TimeSpan.FromSeconds(3));

		Assert.False(playback.IsPlaying);
		Assert.True(device.Written.Count < Tone(frames: 44100).Samples.Length);
	}

	[Fact]
	public async Task 调用方取消也能收尾()
	{
		FakeDevice device = new() {BlockWrites = true};
		using NativeAudioPlayback playback = Playback(device, Tone(frames: 44100));
		using CancellationTokenSource cancelling = new();

		Task playing = playback.PlayAsync(Bytes(), cancelling.Token);
		await Task.Delay(80);
		await cancelling.CancelAsync();
		device.Release();

		await playing.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.False(playback.IsPlaying);
	}

	/// <summary>空音频直接返回，不该去开设备。</summary>
	[Fact]
	public async Task 空音频不开设备()
	{
		FakeDevice device = new();
		PcmAudio empty = new() {Samples = [], SampleRate = 44100, Channels = 1};
		using NativeAudioPlayback playback = Playback(device, empty);

		await playback.PlayAsync(Bytes(), CancellationToken.None);

		Assert.Equal(default, device.Opened);
		Assert.Empty(device.Written);
	}

	// ── 格式协商 ───────────────────────────────────────────────────────────

	/// <summary>
	/// 多数声卡固定跑 48000，而语音合成常给 22050。设备说它只接受什么，
	/// 上层就得转成什么 —— 不转会变调。
	/// </summary>
	[Fact]
	public async Task 采样率不同就重采样()
	{
		FakeDevice device = new() {Force = new AudioFormat(48000, 1)};
		PcmAudio audio = Tone(frames: 22050, sampleRate: 22050);
		using NativeAudioPlayback playback = Playback(device, audio);

		await playback.PlayAsync(Bytes(), CancellationToken.None);

		// 1 秒 22050 → 1 秒 48000，允许取整误差。
		Assert.InRange(device.Written.Count, 47000, 48100);
	}

	/// <summary>单声道铺成立体声：两个声道要一样，不能只有一边响。</summary>
	[Fact]
	public async Task 单声道铺成立体声()
	{
		FakeDevice device = new() {Force = new AudioFormat(44100, 2)};
		PcmAudio mono = Tone(frames: 100, channels: 1);
		using NativeAudioPlayback playback = Playback(device, mono);

		await playback.PlayAsync(Bytes(), CancellationToken.None);

		Assert.Equal(200, device.Written.Count);
		for (int frame = 0; frame < 100; frame++)
			Assert.Equal(device.Written[frame * 2], device.Written[frame * 2 + 1]);
	}

	/// <summary>
	/// 环绕设备上只占前置左右，其余留空。
	///
	/// 实测这台机器的默认输出就是 48000 Hz / 8 声道。早先那版「源声道不够就重复
	/// 最后一个」会把语音同时送进重低音和环绕音箱 —— 低频被 LFE 轰一遍，
	/// 后方也在说话。这条守着它。
	/// </summary>
	[Fact]
	public async Task 环绕设备上只占前置左右()
	{
		FakeDevice device = new() {Force = new AudioFormat(44100, 8)};
		PcmAudio mono = Tone(frames: 50, channels: 1, amplitude: 0.8f);
		using NativeAudioPlayback playback = Playback(device, mono);

		await playback.PlayAsync(Bytes(), CancellationToken.None);

		Assert.Equal(400, device.Written.Count);
		for (int frame = 0; frame < 50; frame++)
		{
			// 前置左右拿到同一份信号。
			Assert.Equal(device.Written[frame * 8], device.Written[frame * 8 + 1]);
			// 中置、重低音、四个环绕一律静音。
			for (int channel = 2; channel < 8; channel++)
				Assert.Equal(0f, device.Written[frame * 8 + channel]);
		}
		// 而且确实有信号，不是整段静音。
		Assert.Contains(device.Written, sample => Math.Abs(sample) > 0.1f);
	}

	/// <summary>
	/// **同一段音频，在立体声设备和 7.1 设备上给出的电平必须一样。**
	///
	/// 实机撞出来的：只铺前置两个声道之后，8 声道设备上的 RMS 正好是立体声设备的
	/// 一半（sqrt(2/8)），也就是嘴张多大取决于用户的音响是几声道。电平因此改成
	/// 取自一条与设备格式无关的单声道轨。
	/// </summary>
	[Fact]
	public async Task 电平不随设备声道数变()
	{
		async Task<double> PeakOn(int channels)
		{
			FakeDevice device = new() {Force = new AudioFormat(44100, channels)};
			using NativeAudioPlayback playback = Playback(device, Tone(frames: 4410, amplitude: 0.5f));
			double peak = 0;
			playback.VolumeSampled += level => peak = Math.Max(peak, level);
			await playback.PlayAsync(Bytes(), CancellationToken.None);
			return peak;
		}

		double stereo = await PeakOn(2);
		double surround = await PeakOn(8);

		Assert.True(stereo > 0.1, $"立体声下应当有可观的电平，实际 {stereo}");
		Assert.Equal(stereo, surround, 3);
	}

	[Fact]
	public async Task 格式一致时不动样本()
	{
		FakeDevice device = new();
		PcmAudio audio = Tone(frames: 50);
		using NativeAudioPlayback playback = Playback(device, audio);

		await playback.PlayAsync(Bytes(), CancellationToken.None);

		Assert.Equal(audio.Samples, device.Written);
	}

	// ── 音量 ───────────────────────────────────────────────────────────────

	[Fact]
	public async Task 音量透传给设备并夹在零到一()
	{
		FakeDevice device = new();
		NativeAudioPlayback playback = new(() => device, (_, _) => Tone(frames: 50));
		playback.SetDeviceVolume(0.3);

		await playback.PlayAsync(Bytes(), CancellationToken.None);
		Assert.Equal(0.3, device.Volume, 3);

		playback.SetDeviceVolume(5);
		FakeDevice second = new();
		NativeAudioPlayback louder = new(() => second, (_, _) => Tone(frames: 50));
		louder.SetDeviceVolume(5);
		await louder.PlayAsync(Bytes(), CancellationToken.None);
		Assert.Equal(1.0, second.Volume, 3);

		playback.Dispose();
		louder.Dispose();
	}

	/// <summary>解不动的格式要把异常抬出去，让调用方说「这段放不了」而不是静默无声。</summary>
	[Fact]
	public async Task 解码失败原样抛出()
	{
		FakeDevice device = new();
		using NativeAudioPlayback playback = new(
			() => device, (_, _) => throw new AudioDecodeException("不支持的格式: audio/aac"));

		AudioDecodeException failure = await Assert.ThrowsAsync<AudioDecodeException>(
			() => playback.PlayAsync(Bytes(), CancellationToken.None));

		Assert.Contains("audio/aac", failure.Message, StringComparison.Ordinal);
		Assert.False(playback.IsPlaying);
	}
}
