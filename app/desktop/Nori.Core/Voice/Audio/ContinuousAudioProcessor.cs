using System.Buffers.Binary;

namespace Nori.Core.Voice.Audio;

/// <summary>连续音频处理管线单次输出。</summary>
public readonly record struct AudioProcessResult(
	float[] MonoResampled,
	byte[] Pcm16Bytes,
	double InputLevel,
	int InputFramesProcessed,
	int OutputFrames);

/// <summary>
/// 统一连续音频流处理器抽象：
/// 设备 PCM → 残余帧对齐与声道下混 → 跨块连续重采样 → PCM16 转换与电平计算。
/// </summary>
public interface IAudioStreamProcessor
{
	/// <summary>输入设备采样率（Hz）。</summary>
	int SourceSampleRate { get; }

	/// <summary>输入设备声道数。</summary>
	int SourceChannels { get; }

	/// <summary>输出单声道目标采样率（Hz）。</summary>
	int TargetSampleRate { get; }

	/// <summary>当前平滑后的标准化输入电平（0.0–1.0）。</summary>
	double InputLevel { get; }

	/// <summary>处理一批交错浮点设备样本。</summary>
	AudioProcessResult Process(ReadOnlySpan<float> interleaved);

	/// <summary>在音频流结束时冲刷残余重采样相位并清理内部状态。</summary>
	AudioProcessResult Flush();

	/// <summary>重置残余缓冲、重采样相位与输入电平。</summary>
	void Reset();
}

/// <summary>
/// 连续音频处理管线实现。
///
/// 支持常见设备采样率（16 kHz / 24 kHz / 44.1 kHz / 48 kHz 等）、单声道与多声道均衡下混、
/// 任意设备块大小（跨块保留不足一帧的残余声道样本不丢帧）、跨块线性插值连续重采样、
/// 非有限值与超幅样本安全夹紧，以及 0.0–1.0 平滑输入电平输出。
/// </summary>
public sealed class ContinuousAudioProcessor : IAudioStreamProcessor
{
	private readonly StatefulLinearResampler _resampler;
	private readonly float[] _residual;
	private int _residualCount;
	private double _inputLevel;

	/// <summary>创建连续音频流处理器。</summary>
	public ContinuousAudioProcessor(int sourceSampleRate, int sourceChannels, int targetSampleRate)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSampleRate);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceChannels);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSampleRate);
		SourceSampleRate = sourceSampleRate;
		SourceChannels = sourceChannels;
		TargetSampleRate = targetSampleRate;
		_resampler = new StatefulLinearResampler(sourceSampleRate, targetSampleRate);
		_residual = new float[sourceChannels];
	}

	/// <inheritdoc />
	public int SourceSampleRate { get; }

	/// <inheritdoc />
	public int SourceChannels { get; }

	/// <inheritdoc />
	public int TargetSampleRate { get; }

	/// <inheritdoc />
	public double InputLevel => _inputLevel;

	/// <summary>跨块重采样当前分数相位（0 ≤ Phase &lt; 1），供连续性与无漂移校验使用。</summary>
	public double ResamplerPhase => _resampler.Phase;

	/// <inheritdoc />
	public AudioProcessResult Process(ReadOnlySpan<float> interleaved)
	{
		float[] mono = ProcessMono(interleaved, out double level);
		if (mono.Length == 0)
			return new AudioProcessResult([], [], level, 0, 0);

		float[] resampled = _resampler.Process(mono);
		byte[] pcm16 = new byte[resampled.Length * 2];
		ConvertToInt16Bytes(resampled, pcm16);
		return new AudioProcessResult(resampled, pcm16, level, mono.Length, resampled.Length);
	}

	/// <inheritdoc />
	public AudioProcessResult Flush()
	{
		if (_residualCount > 0)
		{
			Array.Clear(_residual, 0, _residualCount);
			_residualCount = 0;
		}
		_inputLevel = 0;
		float[] flushed = _resampler.Flush();
		if (flushed.Length == 0)
			return new AudioProcessResult([], [], 0, 0, 0);

		byte[] pcm16 = new byte[flushed.Length * 2];
		ConvertToInt16Bytes(flushed, pcm16);
		return new AudioProcessResult(flushed, pcm16, 0, 0, flushed.Length);
	}

	/// <inheritdoc />
	public void Reset()
	{
		_resampler.Reset();
		Array.Clear(_residual);
		_residualCount = 0;
		_inputLevel = 0;
	}

	/// <summary>只执行残余帧对齐、多声道下混与电平计算，返回源采样率单声道样本。</summary>
	public float[] ProcessMono(ReadOnlySpan<float> interleaved, out double inputLevel)
	{
		float[] mono = DownmixWithResidual(interleaved, out double rawLevel);
		if (mono.Length > 0)
		{
			double smoothed = PcmLevel.Smooth(_inputLevel, rawLevel);
			_inputLevel = rawLevel == 0 && smoothed < 1e-4 ? 0 : Math.Clamp(smoothed, 0, 1);
		}
		inputLevel = _inputLevel;
		return mono;
	}

	/// <summary>处理设备交错样本并直接输出目标采样率单声道 PCM16 小端字节。</summary>
	public byte[] ProcessToPcm16Bytes(ReadOnlySpan<float> interleaved)
	{
		float[] mono = ProcessMono(interleaved, out _);
		if (mono.Length == 0) return [];
		float[] resampled = _resampler.Process(mono);
		if (resampled.Length == 0) return [];
		byte[] pcm16 = new byte[resampled.Length * 2];
		ConvertToInt16Bytes(resampled, pcm16);
		return pcm16;
	}

	/// <summary>处理设备交错样本并直接输出目标采样率单声道 PCM16 样本数组。</summary>
	public short[] ProcessToPcm16Samples(ReadOnlySpan<float> interleaved)
	{
		float[] mono = ProcessMono(interleaved, out _);
		if (mono.Length == 0) return [];
		float[] resampled = _resampler.Process(mono);
		if (resampled.Length == 0) return [];
		short[] pcm16 = new short[resampled.Length];
		ConvertToInt16(resampled, pcm16);
		return pcm16;
	}

	/// <summary>冲刷末尾残余相位并输出 PCM16 小端字节。</summary>
	public byte[] FlushToPcm16Bytes()
	{
		AudioProcessResult result = Flush();
		return result.Pcm16Bytes;
	}

	/// <summary>冲刷末尾残余相位并输出 PCM16 样本数组。</summary>
	public short[] FlushToPcm16Samples()
	{
		if (_residualCount > 0)
		{
			Array.Clear(_residual, 0, _residualCount);
			_residualCount = 0;
		}
		_inputLevel = 0;
		float[] flushed = _resampler.Flush();
		if (flushed.Length == 0) return [];
		short[] pcm16 = new short[flushed.Length];
		ConvertToInt16(flushed, pcm16);
		return pcm16;
	}

	/// <summary>将单个浮点样本安全转换为 PCM16 有符号整数（非有限值归零，超幅夹紧）。</summary>
	public static short FloatToInt16(float sample)
	{
		if (!float.IsFinite(sample)) return 0;
		float clamped = Math.Clamp(sample, -1f, 1f);
		return (short)Math.Round(clamped * short.MaxValue);
	}

	/// <summary>将单声道浮点样本批量转换为 PCM16 <see cref="short"/>。</summary>
	public static void ConvertToInt16(ReadOnlySpan<float> monoSamples, Span<short> destination)
	{
		if (destination.Length < monoSamples.Length)
			throw new ArgumentException("目标缓冲区长度不足", nameof(destination));
		for (int index = 0; index < monoSamples.Length; index++)
			destination[index] = FloatToInt16(monoSamples[index]);
	}

	/// <summary>将单声道浮点样本批量转换为 PCM16 小端字节。</summary>
	public static void ConvertToInt16Bytes(ReadOnlySpan<float> monoSamples, Span<byte> destination)
	{
		if (destination.Length < monoSamples.Length * 2)
			throw new ArgumentException("目标字节缓冲区长度不足", nameof(destination));
		for (int index = 0; index < monoSamples.Length; index++)
			BinaryPrimitives.WriteInt16LittleEndian(destination[(index * 2)..], FloatToInt16(monoSamples[index]));
	}

	/// <summary>将单声道浮点样本编码为 16 位单声道 WAV 字节。</summary>
	internal static byte[] EncodeWaveFromMono(ReadOnlySpan<float> monoSamples, int sampleRate)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
		int dataBytes = checked(monoSamples.Length * 2);
		byte[] output = new byte[44 + dataBytes];
		Span<byte> span = output;

		"RIFF"u8.CopyTo(span);
		BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + dataBytes));
		"WAVE"u8.CopyTo(span[8..]);
		"fmt "u8.CopyTo(span[12..]);
		BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
		BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1);
		BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 1);
		BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)sampleRate);
		BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(sampleRate * 2));
		BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 2);
		BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
		"data"u8.CopyTo(span[36..]);
		BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataBytes);
		ConvertToInt16Bytes(monoSamples, span[44..]);
		return output;
	}

	private float[] DownmixWithResidual(ReadOnlySpan<float> interleaved, out double rawLevel)
	{
		if (interleaved.IsEmpty)
		{
			rawLevel = 0;
			return [];
		}

		int channels = SourceChannels;
		if (channels == 1)
		{
			float[] mono = new float[interleaved.Length];
			double sumSq = 0;
			double peak = 0;
			for (int index = 0; index < interleaved.Length; index++)
			{
				float sample = Sanitize(interleaved[index]);
				mono[index] = sample;
				double clamped = Math.Clamp(sample, -1f, 1f);
				double abs = Math.Abs(clamped);
				if (abs > peak) peak = abs;
				sumSq += clamped * clamped;
			}
			double rms = Math.Sqrt(sumSq / mono.Length);
			rawLevel = Math.Clamp((rms * 0.7) + (peak * 0.3), 0, 1);
			return mono;
		}

		int totalAvailable = _residualCount + interleaved.Length;
		int completeFrames = totalAvailable / channels;
		if (completeFrames == 0)
		{
			for (int index = 0; index < interleaved.Length; index++)
				_residual[_residualCount++] = Sanitize(interleaved[index]);
			rawLevel = 0;
			return [];
		}

		float[] output = new float[completeFrames];
		int inputOffset = 0;
		int frameIndex = 0;
		double multiSumSq = 0;
		double multiPeak = 0;

		if (_residualCount > 0)
		{
			double sum = 0;
			for (int channel = 0; channel < _residualCount; channel++)
				sum += _residual[channel];
			for (int channel = _residualCount; channel < channels; channel++)
				sum += Sanitize(interleaved[inputOffset++]);
			Array.Clear(_residual, 0, _residualCount);
			_residualCount = 0;

			float monoSample = (float)(sum / channels);
			output[frameIndex++] = monoSample;
			double clamped = Math.Clamp(monoSample, -1f, 1f);
			double abs = Math.Abs(clamped);
			if (abs > multiPeak) multiPeak = abs;
			multiSumSq += clamped * clamped;
		}

		while (frameIndex < completeFrames)
		{
			double sum = 0;
			for (int channel = 0; channel < channels; channel++)
				sum += Sanitize(interleaved[inputOffset++]);
			float monoSample = (float)(sum / channels);
			output[frameIndex++] = monoSample;
			double clamped = Math.Clamp(monoSample, -1f, 1f);
			double abs = Math.Abs(clamped);
			if (abs > multiPeak) multiPeak = abs;
			multiSumSq += clamped * clamped;
		}

		int leftover = interleaved.Length - inputOffset;
		for (int index = 0; index < leftover; index++)
			_residual[_residualCount++] = Sanitize(interleaved[inputOffset + index]);

		double multiRms = Math.Sqrt(multiSumSq / completeFrames);
		rawLevel = Math.Clamp((multiRms * 0.7) + (multiPeak * 0.3), 0, 1);
		return output;
	}

	private static float Sanitize(float sample) => float.IsFinite(sample) ? sample : 0f;
}
