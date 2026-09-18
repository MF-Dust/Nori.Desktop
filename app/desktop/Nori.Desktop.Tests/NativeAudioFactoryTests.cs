using System.Text;
using Nori.Core.Voice;
using Nori.Core.Voice.Audio;
using Nori.Desktop.Audio;

namespace Nori.Desktop.Tests;

/// <summary>原生音频工厂的 WAV 解码与按段格式限制，不依赖真实声卡或 WebView。</summary>
public sealed class NativeAudioFactoryTests
{
	[Theory]
	[InlineData("audio/wav")]
	[InlineData("audio/x-wav")]
	[InlineData("audio/mpeg")]
	public void 按实际WAV内容解码而非依赖MIME(string mime)
	{
		PcmAudio decoded = NativeAudioFactory.Decode(Wave(), mime);

		Assert.Equal(24000, decoded.SampleRate);
		Assert.Equal(1, decoded.Channels);
		Assert.Equal(new float[] {0, 0.5f, -0.5f}, decoded.Samples);
	}

	[Theory]
	[InlineData("ID3", "audio/mpeg")]
	[InlineData("OggS", "audio/ogg")]
	[InlineData("ID3", "audio/wav")]
	[InlineData("", "audio/wav")]
	public async Task 工厂播放非WAV时明确提示配置而非打开设备(string content, string mime)
	{
		using IAudioPlayback playback = NativeAudioFactory.CreatePlayback();

		AudioDecodeException error = await Assert.ThrowsAsync<AudioDecodeException>(() =>
			playback.PlayAsync(new EncodedAudio(Encoding.ASCII.GetBytes(content), mime), CancellationToken.None));

		Assert.Contains(mime, error.Message, StringComparison.Ordinal);
		Assert.Contains("PCM WAV", error.Message, StringComparison.Ordinal);
		Assert.Contains("自定义 HTTP 在服务端", error.Message, StringComparison.Ordinal);
		Assert.Contains("audio_backend=webview", error.Message, StringComparison.Ordinal);
		Assert.Contains("重启", error.Message, StringComparison.Ordinal);
		Assert.False(playback.IsPlaying);
	}

	[Fact]
	public void 损坏WAV保留解码错误()
	{
		AudioDecodeException error = Assert.Throws<AudioDecodeException>(() =>
			NativeAudioFactory.Decode("RIFF\0\0\0\0WAVE"u8.ToArray(), "audio/wav"));

		Assert.Contains("fmt", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 非WAV失败后同一播放器仍能播放下一段WAV()
	{
		RecordingDevice device = new();
		int opened = 0;
		using NativeAudioPlayback playback = new(() =>
		{
			opened++;
			return device;
		}, NativeAudioFactory.Decode);
		List<bool> states = [];
		playback.PlayingChanged += states.Add;

		await Assert.ThrowsAsync<AudioDecodeException>(() =>
			playback.PlayAsync(new EncodedAudio("ID3"u8.ToArray(), "audio/mpeg"), CancellationToken.None));
		Assert.Equal(0, opened);
		Assert.Empty(states);
		Assert.False(playback.IsPlaying);

		await playback.PlayAsync(new EncodedAudio(Wave(), "audio/wav"), CancellationToken.None);

		Assert.Equal(1, opened);
		Assert.Equal(3, device.WrittenSamples);
		Assert.Equal([true, false], states);
		Assert.False(playback.IsPlaying);
	}

	private static byte[] Wave() => WaveDecoder.Encode(new PcmAudio
	{
		Samples = [0, 0.5f, -0.5f],
		SampleRate = 24000,
		Channels = 1,
	});

	private sealed class RecordingDevice : IAudioDevice
	{
		public int WrittenSamples { get; private set; }
		public double Volume { get; set; } = 1;
		public AudioFormat Open(int sampleRate, int channels) => new(sampleRate, channels);
		public int Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
		{
			WrittenSamples += samples.Length;
			return samples.Length;
		}
		public void Drain(CancellationToken cancellationToken) { }
		public void Stop() { }
		public void Dispose() { }
	}
}
