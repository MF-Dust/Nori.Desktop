namespace Nori.Core.Voice.Audio;

/// <summary>
/// 直接从麦克风采集的录音后端。
///
/// 产出 **16 kHz 单声道 WAV**，理由有两条：
/// - Whisper 的 <c>/audio/transcriptions</c> 内部就是按 16 kHz 单声道跑的，
///   送 48 kHz 立体声上去只是让它再降一次；
/// - 体积。WebView 那条录的是 webm/opus（约 3 KB/s），原生拿到的是裸 PCM；
///   若按设备原始格式（48 kHz 立体声 16 位 ≈ 192 KB/s）上传，一段 30 秒的话就是
///   5.6 MB。降到 16 kHz 单声道之后是 32 KB/s，仍比 opus 大，但已经在可接受范围。
///
/// 采集设备那一层是 <see cref="IAudioCaptureDevice"/>，只有它需要原生实现；
/// 这里的编排（攒缓冲、下混、重采样、封 WAV）全是普通托管代码。
/// </summary>
public sealed class NativeMicrophoneRecorder(Func<IAudioCaptureDevice> openDevice) : IMicrophoneRecorder
{
	/// <summary>上传给 Whisper 的采样率。</summary>
	public const int TargetSampleRate = 16000;

	/// <summary>单次录音上限。防止一次忘了停就把内存吃光。</summary>
	public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);

	/// <summary>一次从设备读多少帧。太小会频繁唤醒，太大会让停止不跟手。</summary>
	private const int ReadFrames = 1024;

	private readonly Lock _gate = new();
	private IAudioCaptureDevice? _device;
	private Task<PcmAudio>? _pump;
	private CancellationTokenSource? _cancelling;

	/// <inheritdoc />
	public bool IsRecording
	{
		get { lock (_gate) return _pump is not null; }
	}

	/// <inheritdoc />
	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		lock (_gate)
		{
			if (_pump is not null) throw new InvalidOperationException("已经在录音了");
		}

		IAudioCaptureDevice device = openDevice();
		AudioFormat format = device.Open();
		CancellationTokenSource cancelling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		lock (_gate)
		{
			_device = device;
			_cancelling = cancelling;
			_pump = Task.Run(() => Pump(device, format, cancelling.Token), CancellationToken.None);
		}
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
	{
		Task<PcmAudio>? pump;
		IAudioCaptureDevice? device;
		CancellationTokenSource? cancelling;
		lock (_gate)
		{
			pump = _pump;
			device = _device;
			cancelling = _cancelling;
			_pump = null;
			_device = null;
			_cancelling = null;
		}
		if (pump is null) throw new InvalidOperationException("当前没有在录音");

		// 先让设备把阻塞中的 Read 放出来，再取消 —— 反过来的话取消信号会卡在
		// 一个正在等数据的 Read 后面。
		device?.Stop();
		try { await cancelling!.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }

		try
		{
			PcmAudio captured = await pump.WaitAsync(cancellationToken).ConfigureAwait(false);
			PcmAudio prepared = Downmix(captured, TargetSampleRate);
			return new RecordedAudio(WaveDecoder.Encode(prepared), "audio/wav", "recording.wav");
		}
		finally
		{
			device?.Dispose();
			cancelling?.Dispose();
		}
	}

	private static PcmAudio Pump(IAudioCaptureDevice device, AudioFormat format, CancellationToken cancellationToken)
	{
		int maxSamples = (int) (MaxDuration.TotalSeconds * format.SampleRate) * format.Channels;
		List<float> collected = [];
		float[] buffer = new float[ReadFrames * Math.Max(1, format.Channels)];

		while (!cancellationToken.IsCancellationRequested && collected.Count < maxSamples)
		{
			int read = device.Read(buffer, cancellationToken);
			if (read <= 0) break;
			collected.AddRange(buffer.AsSpan(0, read));
		}

		return new PcmAudio
		{
			Samples = [.. collected],
			SampleRate = format.SampleRate,
			Channels = format.Channels,
		};
	}

	/// <summary>
	/// 下混成单声道并重采样到目标率。
	///
	/// 降采样用**区间平均**而不是最近邻：48000 → 16000 是三取一，最近邻等于直接丢掉
	/// 三分之二的样本，高频会折回来变成嘶声；平均相当于顺带做了一次低通。
	/// 输出侧那个升采样场景没有这个问题，所以那边用最近邻就够。
	/// </summary>
	internal static PcmAudio Downmix(PcmAudio source, int targetRate)
	{
		int frames = source.FrameCount;
		if (frames == 0 || source.SampleRate <= 0)
			return new PcmAudio {Samples = [], SampleRate = targetRate, Channels = 1};

		long targetFrames = Math.Max(1, (long) frames * targetRate / source.SampleRate);
		float[] output = new float[targetFrames];

		for (long frame = 0; frame < targetFrames; frame++)
		{
			long from = frame * source.SampleRate / targetRate;
			long to = Math.Min(frames, (frame + 1) * source.SampleRate / targetRate);
			if (to <= from) to = Math.Min(frames, from + 1);

			double sum = 0;
			int counted = 0;
			for (long sourceFrame = from; sourceFrame < to; sourceFrame++)
			{
				for (int channel = 0; channel < source.Channels; channel++)
					sum += source.Samples[sourceFrame * source.Channels + channel];
				counted += source.Channels;
			}
			output[frame] = counted == 0 ? 0 : (float) (sum / counted);
		}

		return new PcmAudio {Samples = output, SampleRate = targetRate, Channels = 1};
	}

	public void Dispose()
	{
		IAudioCaptureDevice? device;
		CancellationTokenSource? cancelling;
		lock (_gate)
		{
			device = _device;
			cancelling = _cancelling;
			_device = null;
			_pump = null;
			_cancelling = null;
		}
		device?.Stop();
		try { cancelling?.Cancel(); } catch (ObjectDisposedException) { }
		device?.Dispose();
		cancelling?.Dispose();
	}
}
