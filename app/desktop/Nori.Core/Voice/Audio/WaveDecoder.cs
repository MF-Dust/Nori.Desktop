using System.Buffers.Binary;

namespace Nori.Core.Voice.Audio;

/// <summary>
/// WAV 解码。
///
/// 为什么先做这一种：本地 GPT-SoVITS 直接返回 wav，而 OpenAI 的
/// <c>/audio/speech</c> 支持 <c>response_format</c> —— 请求体是我们自己拼的，
/// 指定 wav 即可。也就是说走原生播放时，**我们控制得了的那几条链路都能只出 wav**，
/// 不必为了它们背一个 mp3 解码器。
///
/// 实现上要挺过几件真实世界的事：
/// - 块不是只有 fmt 和 data。录音工具常塞 LIST/INFO，有的还塞 fact、cue、id3。
///   必须**按块长跳**，不能假设 data 紧跟 fmt。
/// - 块长是奇数时后面有一个填充字节。少算这一个，之后所有块的位置就全错了。
/// - WAVE_FORMAT_EXTENSIBLE（0xFFFE）很常见，真正的格式在 SubFormat 的头两字节里。
/// - data 块的长度字段可能写着 0 或者一个比文件还大的数（流式写入没回填），
///   这时按实际剩余字节算。
/// </summary>
public static class WaveDecoder
{
	private const ushort FormatPcm = 1;
	private const ushort FormatFloat = 3;
	private const ushort FormatExtensible = 0xFFFE;

	/// <summary>这段字节是不是 WAV。只看头，不解码。</summary>
	public static bool IsWave(ReadOnlySpan<byte> bytes) =>
		bytes.Length >= 12
		&& bytes[0] == (byte) 'R' && bytes[1] == (byte) 'I' && bytes[2] == (byte) 'F' && bytes[3] == (byte) 'F'
		&& bytes[8] == (byte) 'W' && bytes[9] == (byte) 'A' && bytes[10] == (byte) 'V' && bytes[11] == (byte) 'E';

	/// <summary>解一段 WAV。任何形状问题都抛 <see cref="AudioDecodeException"/>。</summary>
	public static PcmAudio Decode(ReadOnlySpan<byte> bytes)
	{
		if (!IsWave(bytes)) throw new AudioDecodeException("不是 WAV：缺少 RIFF/WAVE 头");

		ushort format = 0;
		ushort channels = 0;
		int sampleRate = 0;
		ushort bitsPerSample = 0;
		ReadOnlySpan<byte> data = default;
		bool sawFormat = false;
		bool sawData = false;

		int offset = 12;
		while (offset + 8 <= bytes.Length)
		{
			ReadOnlySpan<byte> id = bytes.Slice(offset, 4);
			uint declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
			int body = offset + 8;
			// 长度字段可能没回填或写坏；按实际剩余字节封顶，别越界读。
			int size = (int) Math.Min(declared, (uint) Math.Max(0, bytes.Length - body));

			if (Matches(id, "fmt "))
			{
				if (size < 16) throw new AudioDecodeException("WAV 的 fmt 块太短");
				format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body, 2));
				channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 2, 2));
				sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(body + 4, 4));
				bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 14, 2));
				// EXTENSIBLE：真正的格式藏在 SubFormat 的头两字节。
				if (format == FormatExtensible && size >= 40)
					format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 24, 2));
				sawFormat = true;
			}
			else if (Matches(id, "data"))
			{
				data = bytes.Slice(body, size);
				sawData = true;
			}

			// 块长为奇数时后面跟一个填充字节。少算它，后面所有块的位置都会错。
			offset = body + size + (size & 1);
		}

		if (!sawFormat) throw new AudioDecodeException("WAV 里没有 fmt 块");
		// 「没有 data 块」和「data 块是空的」是两回事：零长度录音（按下就松开）
		// 产出的就是后者，它是合法 WAV，不该被当成坏文件。
		if (!sawData) throw new AudioDecodeException("WAV 里没有 data 块");
		if (channels is 0 or > 32) throw new AudioDecodeException($"WAV 声道数不合理: {channels}");
		if (sampleRate is <= 0 or > 768_000) throw new AudioDecodeException($"WAV 采样率不合理: {sampleRate}");

		return new PcmAudio
		{
			Samples = ToFloat(data, format, bitsPerSample),
			SampleRate = sampleRate,
			Channels = channels,
		};
	}

	private static bool Matches(ReadOnlySpan<byte> id, string text) =>
		id[0] == (byte) text[0] && id[1] == (byte) text[1] && id[2] == (byte) text[2] && id[3] == (byte) text[3];

	/// <summary>
	/// 把样本铺成 float。
	///
	/// 8 位是**无符号**的（128 为静音），其余整数位宽是有符号 —— 这是 WAV 的历史包袱，
	/// 按同一套公式处理会让 8 位音频听起来整体偏移。
	/// </summary>
	private static float[] ToFloat(ReadOnlySpan<byte> data, ushort format, ushort bitsPerSample)
	{
		switch (format, bitsPerSample)
		{
			case (FormatPcm, 8):
			{
				float[] output = new float[data.Length];
				for (int index = 0; index < data.Length; index++) output[index] = (data[index] - 128) / 128f;
				return output;
			}
			case (FormatPcm, 16):
			{
				int count = data.Length / 2;
				float[] output = new float[count];
				for (int index = 0; index < count; index++)
					output[index] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(index * 2, 2)) / 32768f;
				return output;
			}
			case (FormatPcm, 24):
			{
				int count = data.Length / 3;
				float[] output = new float[count];
				for (int index = 0; index < count; index++)
				{
					int at = index * 3;
					// 24 位小端有符号：补上第四个字节做符号扩展。
					int value = data[at] | (data[at + 1] << 8) | (data[at + 2] << 16);
					if ((value & 0x800000) != 0) value |= unchecked((int) 0xFF000000);
					output[index] = value / 8388608f;
				}
				return output;
			}
			case (FormatPcm, 32):
			{
				int count = data.Length / 4;
				float[] output = new float[count];
				for (int index = 0; index < count; index++)
					output[index] = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(index * 4, 4)) / 2147483648f;
				return output;
			}
			case (FormatFloat, 32):
			{
				int count = data.Length / 4;
				float[] output = new float[count];
				for (int index = 0; index < count; index++)
					output[index] = BitConverter.Int32BitsToSingle(
						BinaryPrimitives.ReadInt32LittleEndian(data.Slice(index * 4, 4)));
				return output;
			}
			case (FormatFloat, 64):
			{
				int count = data.Length / 8;
				float[] output = new float[count];
				for (int index = 0; index < count; index++)
					output[index] = (float) BitConverter.Int64BitsToDouble(
						BinaryPrimitives.ReadInt64LittleEndian(data.Slice(index * 8, 8)));
				return output;
			}
			default:
				throw new AudioDecodeException($"不支持的 WAV 样本格式: format={format} bits={bitsPerSample}");
		}
	}

	/// <summary>
	/// 把 PCM 封回 WAV（16 位整数）。
	///
	/// 录音要用：原生采集拿到的是裸 PCM，而 Whisper 的 <c>/audio/transcriptions</c>
	/// 收的是带头的文件。
	/// </summary>
	public static byte[] Encode(PcmAudio audio)
	{
		ArgumentNullException.ThrowIfNull(audio);
		int dataBytes = audio.Samples.Length * 2;
		byte[] output = new byte[44 + dataBytes];
		Span<byte> span = output;

		"RIFF"u8.CopyTo(span);
		BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint) (36 + dataBytes));
		"WAVE"u8.CopyTo(span[8..]);
		"fmt "u8.CopyTo(span[12..]);
		BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
		BinaryPrimitives.WriteUInt16LittleEndian(span[20..], FormatPcm);
		BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort) audio.Channels);
		BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint) audio.SampleRate);
		BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint) (audio.SampleRate * audio.Channels * 2));
		BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort) (audio.Channels * 2));
		BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
		"data"u8.CopyTo(span[36..]);
		BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint) dataBytes);

		for (int index = 0; index < audio.Samples.Length; index++)
		{
			// 先夹再转：浮点侧做过增益之后可能越界，不夹会绕回成爆音。
			float sample = Math.Clamp(audio.Samples[index], -1f, 1f);
			BinaryPrimitives.WriteInt16LittleEndian(span[(44 + index * 2)..], (short) Math.Round(sample * 32767f));
		}
		return output;
	}
}
