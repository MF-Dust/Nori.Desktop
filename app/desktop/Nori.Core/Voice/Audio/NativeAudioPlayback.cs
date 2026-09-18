namespace Nori.Core.Voice.Audio;

/// <summary>
/// 直接推给声卡的播放后端。
///
/// 与 WebView 那份的区别只有一条链路：
/// <code>
/// WebView：字节 → 一次性媒体端点 → 本地 HTTP → WebView fetch → WebAudio → 设备
///          电平在前端算，每约 60ms 经桥回传一次
/// 原生：  字节 → 解码 → 设备
///          电平在送进设备的**那个缓冲**上算，和听到的声音严格对齐
/// </code>
///
/// 设备那一层是 <see cref="IAudioDevice"/>，只有它需要原生实现；这里的编排
/// （解码、切块、发电平、停止、收尾）全是普通托管代码，用假设备就能测完。
/// </summary>
public sealed class NativeAudioPlayback(Func<IAudioDevice> openDevice, Func<ReadOnlyMemory<byte>, string, PcmAudio> decode)
	: IAudioPlayback
{
	private readonly Lock _gate = new();
	private CancellationTokenSource? _current;
	private IAudioDevice? _device;
	private bool _playing;
	private double _volume = 1.0;

	/// <inheritdoc />
	public bool IsPlaying => Volatile.Read(ref _playing);

	/// <inheritdoc />
	public event Action<bool>? PlayingChanged;

	/// <inheritdoc />
	public event Action<double>? VolumeSampled;

	/// <summary>输出音量 0..1，随下一段播放生效。</summary>
	public void SetDeviceVolume(double volume)
	{
		double clamped = Math.Clamp(volume, 0, 1);
		lock (_gate)
		{
			_volume = clamped;
			if (_device is { } device) device.Volume = clamped;
		}
	}

	/// <inheritdoc />
	public async Task PlayAsync(EncodedAudio audio, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(audio);
		PcmAudio pcm = decode(audio.Bytes, audio.Mime);
		if (pcm.Samples.Length == 0) return;

		// 新的一段顶掉旧的。语音是串行的：上一句还没播完就来了下一句，说明用户
		// 已经不想听上一句了。
		Stop();

		CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		IAudioDevice device = openDevice();
		lock (_gate)
		{
			_current = linked;
			_device = device;
			device.Volume = _volume;
		}
		SetPlaying(true);

		try
		{
			await Task.Run(() => Pump(device, pcm, linked.Token), CancellationToken.None).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// 被顶掉或被调用方取消，不是失败。
		}
		finally
		{
			lock (_gate)
			{
				if (ReferenceEquals(_current, linked))
				{
					_current = null;
					_device = null;
				}
			}
			device.Dispose();
			linked.Dispose();
			SetPlaying(false);
			// 收尾必须把嘴合上。少这一条，播放被打断时口型会停在张开的那一帧。
			VolumeSampled?.Invoke(0);
		}
	}

	/// <summary>
	/// 一窗一窗地推。
	///
	/// 窗长取 <see cref="PcmLevel.WindowMilliseconds"/>：它同时是「写给设备的块大小」
	/// 和「发电平的节奏」。两者用同一个值，电平就天然对齐到正在播的那一段，
	/// 不需要另外算时间。
	/// </summary>
	private void Pump(IAudioDevice device, PcmAudio pcm, CancellationToken cancellationToken)
	{
		AudioFormat actual = device.Open(pcm.SampleRate, pcm.Channels);
		(float[] samples, float[] level) = Prepare(pcm, actual);

		int frameWindow = Math.Max(1, actual.SampleRate * PcmLevel.WindowMilliseconds / 1000);
		double smoothed = 0;

		for (int frame = 0; frame < level.Length && !cancellationToken.IsCancellationRequested;)
		{
			int take = Math.Min(frameWindow, level.Length - frame);

			// 电平取自那条**单声道轨**，不是写给设备的缓冲。后者的声道数由设备定，
			// 拿它算 RMS 会让嘴张多大取决于用户的音响是几声道。
			smoothed = PcmLevel.Smooth(smoothed, PcmLevel.Rms(level.AsSpan(frame, take)));
			VolumeSampled?.Invoke(smoothed);

			// 写会阻塞到设备吃得下，而这一窗**就是**马上要响的那一窗，所以先发后写。
			int written = device.Write(
				samples.AsSpan(frame * actual.Channels, take * actual.Channels), cancellationToken);
			if (written <= 0) break;          // 被 Stop 打断
			frame += written / actual.Channels;
		}

		if (!cancellationToken.IsCancellationRequested) device.Drain(cancellationToken);
	}

	/// <summary>
	/// 备好两样东西：写给设备的多声道缓冲，和一条与设备格式无关的单声道电平轨。
	///
	/// 两者帧数相同，用同一个帧区间对齐，所以电平天然对应正在播的那一段。
	///
	/// 重采样用最近邻。够用的理由：这里处理的是语音不是音乐，而多数声卡固定跑
	/// 48000、语音合成常给 22050 或 24000，不转就变调。最近邻在语音上的可闻损失
	/// 很小，换来的是零依赖；真要更好的质量，换成线性插值也只动这一个方法。
	/// </summary>
	private static (float[] Samples, float[] Level) Prepare(PcmAudio source, AudioFormat target)
	{
		int sourceFrames = source.FrameCount;
		if (sourceFrames == 0) return ([], []);

		long targetFrames = (long) sourceFrames * target.SampleRate / Math.Max(1, source.SampleRate);
		float[] samples = new float[targetFrames * target.Channels];
		float[] level = new float[targetFrames];

		for (long frame = 0; frame < targetFrames; frame++)
		{
			long sourceFrame = frame * source.SampleRate / Math.Max(1, target.SampleRate);
			if (sourceFrame >= sourceFrames) sourceFrame = sourceFrames - 1;

			// 电平轨按源声道下混。口型只有一张嘴，不分左右。
			double sum = 0;
			for (int channel = 0; channel < source.Channels; channel++)
				sum += source.Samples[sourceFrame * source.Channels + channel];
			level[frame] = (float) (sum / source.Channels);

			for (int channel = 0; channel < target.Channels; channel++)
			{
				int sourceChannel = MapChannel(channel, source.Channels, target.Channels);
				samples[frame * target.Channels + channel] = sourceChannel < 0
					? 0
					: source.Samples[sourceFrame * source.Channels + sourceChannel];
			}
		}
		return (samples, level);
	}

	/// <summary>
	/// 目标的第 n 个声道该取源的哪一个；返回 −1 表示这个声道留空。
	///
	/// **只铺前两个声道（前置左右），其余留空。** 实测这台机器的默认输出是
	/// 48000 Hz / **8 声道**（环绕配置），早先那版「源声道不够就重复最后一个」
	/// 会把语音同时送进重低音和环绕音箱 —— 低频被 LFE 轰一遍，后方也在说话。
	/// 多数播放器对单声道/立体声素材的做法就是只占前置，这里跟它一致。
	///
	/// 单声道进立体声要复制到两边，否则只有左边有声音。
	/// </summary>
	private static int MapChannel(int targetChannel, int sourceChannels, int targetChannels)
	{
		if (sourceChannels >= targetChannels) return targetChannel;          // 够用就一一对应
		if (targetChannel >= 2) return -1;                                   // 前两个之外留空
		return sourceChannels == 1 ? 0 : targetChannel;                      // 单声道复制到左右
	}

	/// <inheritdoc />
	public void Stop()
	{
		CancellationTokenSource? cancelling;
		IAudioDevice? device;
		lock (_gate)
		{
			cancelling = _current;
			device = _device;
		}
		// 先让设备把阻塞中的 Write 放出来，再取消 —— 反过来的话取消信号会卡在
		// 一个正在等缓冲的 Write 后面。
		device?.Stop();
		try { cancelling?.Cancel(); } catch (ObjectDisposedException) { /* 已经收尾了 */ }
	}

	private void SetPlaying(bool playing)
	{
		if (Volatile.Read(ref _playing) == playing) return;
		Volatile.Write(ref _playing, playing);
		PlayingChanged?.Invoke(playing);
	}

	public void Dispose()
	{
		Stop();
		lock (_gate)
		{
			_device?.Dispose();
			_device = null;
		}
	}
}
