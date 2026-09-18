namespace Nori.Core.Voice.Audio;

/// <summary>带限重采样，并生成帧数一致的设备缓冲和源声道下混电平轨。</summary>
internal static class PcmResampler
{
	private const int KernelRadius = 32;
	private const int TableStepsPerSample = 128;
	// 为有限窗的过渡带留出 10%，让新奈奎斯特频率落在阻带内。
	private const double Bandwidth = 0.9;
	private static readonly double[] Kernel = CreateKernel();

	/// <summary>
	/// 使用 Blackman 窗截断 sinc，截止频率随较低的采样率缩放，避免降采样混叠。
	/// 预计算核并线性查表，热循环不计算三角函数，也不为每帧分配数组。
	/// 同采样率直接复制样本；边界截断核重新归一，保持常量及单帧输入的幅度。
	/// </summary>
	internal static (float[] Samples, float[] Level) Prepare(
		PcmAudio source, AudioFormat target, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(source.SampleRate);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(source.Channels);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(target.SampleRate);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(target.Channels);
		if (source.Samples.Length % source.Channels != 0)
			throw new AudioDecodeException("PCM 样本未按完整声道帧对齐。");
		if (source.FrameCount == 0) return ([], []);

		// 与原播放路径一致，时长向下取整；极短的非空片段至少保留一帧。
		int targetFrames = checked((int) Math.Max(1, (long) source.FrameCount * target.SampleRate / source.SampleRate));
		float[] samples = new float[checked(targetFrames * target.Channels)];
		float[] level = new float[targetFrames];
		double[] filtered = new double[source.Channels];
		double cutoff = Bandwidth * Math.Min(1, (double) target.SampleRate / source.SampleRate);
		double radius = KernelRadius / cutoff;

		for (int frame = 0; frame < targetFrames; frame++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (source.SampleRate == target.SampleRate)
			{
				for (int channel = 0; channel < source.Channels; channel++)
					filtered[channel] = source.Samples[frame * source.Channels + channel];
			}
			else
			{
				// 每帧从整数时间戳求位置，长音频也不会积累步进误差。
				double position = (double) ((long) frame * source.SampleRate) / target.SampleRate;
				FilterFrame(source, position, cutoff, radius, filtered, cancellationToken);
			}

			double sum = 0;
			foreach (double sample in filtered) sum += sample;
			level[frame] = (float) (sum / source.Channels);
			for (int channel = 0; channel < target.Channels; channel++)
			{
				int sourceChannel = MapChannel(channel, source.Channels, target.Channels);
				if (sourceChannel >= 0)
					samples[frame * target.Channels + channel] = (float) filtered[sourceChannel];
			}
		}
		return (samples, level);
	}

	private static void FilterFrame(PcmAudio source, double position, double cutoff, double radius,
		double[] filtered, CancellationToken cancellationToken)
	{
		Array.Clear(filtered);
		int first = (int) Math.Max(0, Math.Ceiling(position - radius));
		int last = (int) Math.Min(source.FrameCount - 1, Math.Floor(position + radius));
		double weightSum = 0;
		for (int frame = first; frame <= last; frame++)
		{
			// 极低目标采样率的核可能覆盖很多输入帧，取消不能等整条核算完。
			if ((frame & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
			double weight = LookupKernel(Math.Abs(frame - position) * cutoff);
			weightSum += weight;
			for (int channel = 0; channel < source.Channels; channel++)
				filtered[channel] += weight * source.Samples[frame * source.Channels + channel];
		}
		for (int channel = 0; channel < filtered.Length; channel++)
			filtered[channel] /= weightSum;
	}

	private static double LookupKernel(double distance)
	{
		if (distance >= KernelRadius) return 0;
		double position = distance * TableStepsPerSample;
		int index = (int) position;
		return Kernel[index] + (Kernel[index + 1] - Kernel[index]) * (position - index);
	}

	private static double[] CreateKernel()
	{
		double[] table = new double[KernelRadius * TableStepsPerSample + 1];
		table[0] = 1;
		for (int index = 1; index < table.Length; index++)
		{
			double distance = (double) index / TableStepsPerSample;
			double angle = Math.PI * distance;
			double window = 0.42 + 0.5 * Math.Cos(angle / KernelRadius) + 0.08 * Math.Cos(2 * angle / KernelRadius);
			table[index] = Math.Sin(angle) / angle * window;
		}
		return table;
	}

	/// <summary>保留既有声道映射：单声道复制到前置左右，多余环绕声道静音。</summary>
	private static int MapChannel(int targetChannel, int sourceChannels, int targetChannels)
	{
		if (sourceChannels >= targetChannels) return targetChannel;
		if (targetChannel >= 2) return -1;
		return sourceChannels == 1 ? 0 : targetChannel;
	}
}
