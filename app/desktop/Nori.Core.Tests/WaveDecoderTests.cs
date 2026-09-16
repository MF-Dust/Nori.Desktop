using System.Buffers.Binary;
using Nori.Core.Voice.Audio;

namespace Nori.Core.Tests;

/// <summary>
/// WAV 解码与 PCM 电平。
///
/// 这一层是原生音频后端里唯一能脱离声卡测的部分，也正是最容易出静默错误的部分：
/// 块跳错一个字节、8 位当成有符号、24 位没做符号扩展 —— 这些都不会抛，只会让
/// 声音变成噪声或者整体偏移。
/// </summary>
public sealed class WaveDecoderTests
{
	/// <summary>造一段最小可用的 WAV。<paramref name="extraChunk"/> 用来插真实世界里常见的杂块。</summary>
	private static byte[] Wave(
		short[] samples, int sampleRate = 44100, int channels = 1,
		ushort format = 1, ushort bits = 16, byte[]? extraChunk = null)
	{
		byte[] data = new byte[samples.Length * 2];
		for (int index = 0; index < samples.Length; index++)
			BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(index * 2), samples[index]);

		List<byte> output = [];
		output.AddRange("RIFF"u8.ToArray());
		output.AddRange(new byte[4]);                       // 先占位，最后回填
		output.AddRange("WAVE"u8.ToArray());

		output.AddRange("fmt "u8.ToArray());
		output.AddRange(BitConverter.GetBytes(16));
		output.AddRange(BitConverter.GetBytes(format));
		output.AddRange(BitConverter.GetBytes((ushort) channels));
		output.AddRange(BitConverter.GetBytes(sampleRate));
		output.AddRange(BitConverter.GetBytes(sampleRate * channels * bits / 8));
		output.AddRange(BitConverter.GetBytes((ushort) (channels * bits / 8)));
		output.AddRange(BitConverter.GetBytes(bits));

		if (extraChunk is not null) output.AddRange(extraChunk);

		output.AddRange("data"u8.ToArray());
		output.AddRange(BitConverter.GetBytes(data.Length));
		output.AddRange(data);

		byte[] bytes = [.. output];
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint) (bytes.Length - 8));
		return bytes;
	}

	[Fact]
	public void 认得出WAV头()
	{
		Assert.True(WaveDecoder.IsWave(Wave([0, 1, 2])));
		Assert.False(WaveDecoder.IsWave("not audio at all"u8));
		Assert.False(WaveDecoder.IsWave([]));
		// ID3 开头的 mp3 不能被当成 wav。
		Assert.False(WaveDecoder.IsWave("ID3\0\0\0\0\0\0"u8));
	}

	[Fact]
	public void 解出来的形状和头里写的一致()
	{
		PcmAudio audio = WaveDecoder.Decode(Wave([0, 16384, -16384, 32767], sampleRate: 22050, channels: 2));

		Assert.Equal(22050, audio.SampleRate);
		Assert.Equal(2, audio.Channels);
		Assert.Equal(4, audio.Samples.Length);
		Assert.Equal(2, audio.FrameCount);     // 4 个交错样本 / 2 声道
	}

	[Fact]
	public void 十六位样本归一到正负一之间()
	{
		PcmAudio audio = WaveDecoder.Decode(Wave([0, 32767, -32768, 16384]));

		Assert.Equal(0f, audio.Samples[0], 5);
		Assert.Equal(1f, audio.Samples[1], 3);
		Assert.Equal(-1f, audio.Samples[2], 3);
		Assert.Equal(0.5f, audio.Samples[3], 3);
	}

	/// <summary>
	/// 录音工具常在 fmt 和 data 之间塞 LIST/INFO。**必须按块长跳** ——
	/// 假设 data 紧跟 fmt 的解码器在这类文件上会把元数据当成样本，放出一段噪声。
	/// </summary>
	[Fact]
	public void 跳过fmt与data之间的杂块()
	{
		List<byte> list = [.. "LIST"u8.ToArray(), .. BitConverter.GetBytes(10)];
		list.AddRange("INFOISFT"u8.ToArray());
		list.AddRange([0x41, 0x00]);

		PcmAudio audio = WaveDecoder.Decode(Wave([1000, -1000], extraChunk: [.. list]));

		Assert.Equal(2, audio.Samples.Length);
		Assert.Equal(1000 / 32768f, audio.Samples[0], 5);
	}

	/// <summary>块长为奇数时后面跟一个填充字节。少算它，之后所有块的位置都会错。</summary>
	[Fact]
	public void 奇数长度的块后面有填充字节()
	{
		List<byte> odd = [.. "cue "u8.ToArray(), .. BitConverter.GetBytes(3)];
		odd.AddRange([1, 2, 3, 0]);          // 3 字节内容 + 1 字节填充

		PcmAudio audio = WaveDecoder.Decode(Wave([2000, -2000], extraChunk: [.. odd]));

		Assert.Equal(2, audio.Samples.Length);
		Assert.Equal(2000 / 32768f, audio.Samples[0], 5);
	}

	/// <summary>8 位 WAV 是**无符号**的，128 才是静音。按有符号处理会让整段偏移。</summary>
	[Fact]
	public void 八位按无符号解()
	{
		byte[] bytes = Wave([], bits: 8);
		// 手工换掉 data：内容是 0 / 128 / 255。
		List<byte> rebuilt = [.. bytes[..^8]];
		rebuilt.AddRange("data"u8.ToArray());
		rebuilt.AddRange(BitConverter.GetBytes(3));
		rebuilt.AddRange([0, 128, 255]);
		byte[] eight = [.. rebuilt];
		BinaryPrimitives.WriteUInt32LittleEndian(eight.AsSpan(4), (uint) (eight.Length - 8));

		PcmAudio audio = WaveDecoder.Decode(eight);

		Assert.Equal(-1f, audio.Samples[0], 3);
		Assert.Equal(0f, audio.Samples[1], 3);
		Assert.True(audio.Samples[2] > 0.99f);
	}

	/// <summary>WAVE_FORMAT_EXTENSIBLE（0xFFFE）很常见，真正的格式在 SubFormat 里。</summary>
	[Fact]
	public void 认得EXTENSIBLE()
	{
		// 造一个 40 字节的 fmt：前 16 字节同普通 PCM，SubFormat 头两字节写 1。
		List<byte> output =
		[
			.. "RIFF"u8.ToArray(), .. new byte[4], .. "WAVE"u8.ToArray(),
			.. "fmt "u8.ToArray(), .. BitConverter.GetBytes(40),
			.. BitConverter.GetBytes((ushort) 0xFFFE),
			.. BitConverter.GetBytes((ushort) 1),
			.. BitConverter.GetBytes(48000),
			.. BitConverter.GetBytes(48000 * 2),
			.. BitConverter.GetBytes((ushort) 2),
			.. BitConverter.GetBytes((ushort) 16),
			.. BitConverter.GetBytes((ushort) 22),              // cbSize
			.. BitConverter.GetBytes((ushort) 16),              // 有效位数
			.. BitConverter.GetBytes(3),                        // 声道掩码
			.. BitConverter.GetBytes((ushort) 1),               // SubFormat 头两字节 = PCM
			.. new byte[14],
		];
		output.AddRange("data"u8.ToArray());
		output.AddRange(BitConverter.GetBytes(4));
		output.AddRange([0, 0, 0xFF, 0x7F]);
		byte[] bytes = [.. output];
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint) (bytes.Length - 8));

		PcmAudio audio = WaveDecoder.Decode(bytes);

		Assert.Equal(48000, audio.SampleRate);
		Assert.Equal(2, audio.Samples.Length);
		Assert.Equal(1f, audio.Samples[1], 3);
	}

	/// <summary>data 的长度字段没回填（流式写入）时，按实际剩余字节算，不能越界。</summary>
	[Fact]
	public void data长度写坏时按剩余字节算()
	{
		byte[] bytes = Wave([100, 200, 300]);
		// 把 data 的长度字段改成一个远大于文件的数。
		int dataAt = bytes.Length - 6 - 4;
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(dataAt), 0xFFFFFF00);

		PcmAudio audio = WaveDecoder.Decode(bytes);

		Assert.Equal(3, audio.Samples.Length);
	}

	[Theory]
	[InlineData("not a wav at all")]
	[InlineData("RIFF____WAVE")]                 // 有头没块
	public void 形状不对时抛可读的错(string text)
	{
		AudioDecodeException failure = Assert.Throws<AudioDecodeException>(
			() => WaveDecoder.Decode(System.Text.Encoding.ASCII.GetBytes(text)));

		Assert.NotEmpty(failure.Message);
	}

	/// <summary>
	/// 零长度的 data 块是**合法**的：按下录音键立刻松开就会产出这种文件。
	/// 把它和「压根没有 data 块」当成一回事，会让一次空录音变成一个错误。
	/// </summary>
	[Fact]
	public void 空的data块是合法的()
	{
		PcmAudio decoded = WaveDecoder.Decode(Wave([]));

		Assert.Empty(decoded.Samples);
		Assert.Equal(0, decoded.FrameCount);
		Assert.Equal(TimeSpan.Zero, decoded.Duration);
	}

	[Fact]
	public void 真的没有data块才报错()
	{
		byte[] bytes = Wave([1, 2]);
		// 把 data 的块名改掉，等于这份文件里没有 data。
		bytes[bytes.Length - 4 - 4 - 4] = (byte) 'X';

		Assert.Throws<AudioDecodeException>(() => WaveDecoder.Decode(bytes));
	}

	[Fact]
	public void 不支持的位宽抛错而不是出噪声()
	{
		byte[] bytes = Wave([1, 2], bits: 16);
		BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 12);   // 12 位，没人支持

		Assert.Throws<AudioDecodeException>(() => WaveDecoder.Decode(bytes));
	}

	// ── 回写 ───────────────────────────────────────────────────────────────

	/// <summary>录音要封回 WAV 交给 Whisper，封完再解回来必须还是同一段。</summary>
	[Fact]
	public void 封回WAV再解回来是同一段()
	{
		PcmAudio original = new()
		{
			Samples = [0f, 0.5f, -0.5f, 0.999f],
			SampleRate = 16000,
			Channels = 1,
		};

		PcmAudio back = WaveDecoder.Decode(WaveDecoder.Encode(original));

		Assert.Equal(original.SampleRate, back.SampleRate);
		Assert.Equal(original.Channels, back.Channels);
		Assert.Equal(original.Samples.Length, back.Samples.Length);
		for (int index = 0; index < original.Samples.Length; index++)
			Assert.Equal(original.Samples[index], back.Samples[index], 3);
	}

	/// <summary>浮点侧做过增益之后可能越界；不夹会绕回成爆音。</summary>
	[Fact]
	public void 越界样本被夹住而不是绕回()
	{
		PcmAudio loud = new() {Samples = [2f, -2f], SampleRate = 8000, Channels = 1};

		PcmAudio back = WaveDecoder.Decode(WaveDecoder.Encode(loud));

		Assert.Equal(1f, back.Samples[0], 3);
		Assert.Equal(-1f, back.Samples[1], 3);
	}

	// ── 电平 ───────────────────────────────────────────────────────────────

	[Fact]
	public void 静音的电平是零()
	{
		Assert.Equal(0, PcmLevel.Rms(new float[256]), 6);
		Assert.Equal(0, PcmLevel.Rms([]), 6);
	}

	[Fact]
	public void 满量程方波的电平接近一()
	{
		float[] square = new float[256];
		for (int index = 0; index < square.Length; index++) square[index] = index % 2 == 0 ? 1f : -1f;

		Assert.Equal(1, PcmLevel.Rms(square), 3);
	}

	/// <summary>越响电平越高 —— 这是驱动口型的全部要求。</summary>
	[Fact]
	public void 电平随响度单调上升()
	{
		float[] quiet = new float[128];
		float[] loud = new float[128];
		for (int index = 0; index < 128; index++)
		{
			quiet[index] = 0.1f * MathF.Sin(index);
			loud[index] = 0.9f * MathF.Sin(index);
		}

		Assert.True(PcmLevel.Rms(loud) > PcmLevel.Rms(quiet));
	}

	[Fact]
	public void 切窗覆盖整段且不丢样本()
	{
		PcmAudio audio = new()
		{
			Samples = new float[44100],       // 单声道 1 秒
			SampleRate = 44100,
			Channels = 1,
		};

		IReadOnlyList<double> envelope = PcmLevel.Envelope(audio);

		int window = PcmLevel.WindowSamples(44100, 1);
		Assert.Equal((int) Math.Ceiling(44100.0 / window), envelope.Count);
	}

	/// <summary>张嘴要快、闭嘴要慢：同一个系数会让闭嘴时抽搐或让张嘴跟不上音头。</summary>
	[Fact]
	public void 平滑是不对称的()
	{
		double opening = PcmLevel.Smooth(0, 1);
		double closing = 1 - PcmLevel.Smooth(1, 0);

		Assert.True(opening > closing);
		// 两个方向都要真的朝目标走。
		Assert.InRange(opening, 0.01, 0.99);
		Assert.InRange(PcmLevel.Smooth(1, 0), 0.01, 0.99);
	}
}
