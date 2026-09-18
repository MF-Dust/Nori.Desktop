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

	private enum RecordingState { Idle, Starting, Recording, Stopping, Disposed }

	private readonly Lock _gate = new();
	private RecordingState _state;
	private IAudioCaptureDevice? _device;
	private Task<PcmAudio>? _pump;
	private CancellationTokenSource? _cancelling;

	/// <inheritdoc />
	public bool IsRecording
	{
		get { lock (_gate) return _state is RecordingState.Starting or RecordingState.Recording; }
	}

	/// <inheritdoc />
	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_state == RecordingState.Disposed, this);
			if (_state != RecordingState.Idle) throw new InvalidOperationException("已经在录音或正在停止");
			cancellationToken.ThrowIfCancellationRequested();
			CancellationTokenSource cancelling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
			// 先预留，再打开设备；两个 Start 不能同时越过空闲检查。
			_state = RecordingState.Starting;
			_cancelling = cancelling;
			_pump = Task.Run(() => Capture(cancelling, started), CancellationToken.None);
			return started.Task;
		}
	}

	/// <inheritdoc />
	public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
	{
		Task<PcmAudio> pump;
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_state == RecordingState.Disposed, this);
			if (_state is not (RecordingState.Starting or RecordingState.Recording))
				throw new InvalidOperationException("当前没有在录音");
			pump = _pump!;
			_state = RecordingState.Stopping;
			try { _device?.Stop(); }
			finally { _cancelling?.Cancel(); }
		}

		try
		{
			PcmAudio captured = await pump.WaitAsync(cancellationToken).ConfigureAwait(false);
			PcmAudio prepared = Downmix(captured, TargetSampleRate);
			return new RecordedAudio(WaveDecoder.Encode(prepared), "audio/wav", "recording.wav");
		}
		finally
		{
			lock (_gate)
			{
				if (ReferenceEquals(_pump, pump) && pump.IsCompleted)
				{
					_pump = null;
					if (_state != RecordingState.Disposed) _state = RecordingState.Idle;
				}
			}
		}
	}

	private PcmAudio Capture(CancellationTokenSource cancelling, TaskCompletionSource started)
	{
		CancellationToken cancellationToken = cancelling.Token;
		IAudioCaptureDevice? device = null;
		try
		{
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				device = openDevice();
				cancellationToken.ThrowIfCancellationRequested();
				AudioFormat format = device.Open();
				lock (_gate)
				{
					// Starting 被 Stop/Dispose 取消后，不再发布设备或启动读取。
					cancellationToken.ThrowIfCancellationRequested();
					_device = device;
					_state = RecordingState.Recording;
					started.TrySetResult();
				}
				return Pump(device, format, cancellationToken);
			}
			finally
			{
				lock (_gate) _device = null;
				try { device?.Dispose(); }
				finally
				{
					lock (_gate)
					{
						_cancelling = null;
						cancelling.Dispose();
						if (_state != RecordingState.Recording)
						{
							_pump = null;
							if (_state != RecordingState.Disposed) _state = RecordingState.Idle;
						}
					}
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			started.TrySetCanceled(cancellationToken);
			return new PcmAudio {Samples = [], SampleRate = TargetSampleRate, Channels = 1};
		}
		catch (Exception failure)
		{
			started.TrySetException(failure);
			throw;
		}
	}

	private static PcmAudio Pump(IAudioCaptureDevice device, AudioFormat format, CancellationToken cancellationToken)
	{
		int maxSamples = (int) (MaxDuration.TotalSeconds * format.SampleRate) * format.Channels;
		List<float> collected = [];
		float[] buffer = new float[ReadFrames * Math.Max(1, format.Channels)];

		while (!cancellationToken.IsCancellationRequested && collected.Count < maxSamples)
		{
			try
			{
				int read = device.Read(buffer, cancellationToken);
				if (read <= 0) break;
				collected.AddRange(buffer.AsSpan(0, read));
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				// 设备等待被取消时仍保留已经采到的样本。
				break;
			}
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
		lock (_gate)
		{
			if (_state == RecordingState.Disposed) return;
			_state = RecordingState.Disposed;
			// 打开和读取可能尚未退出，设备只由采集任务收尾释放。
			try { _device?.Stop(); }
			finally { _cancelling?.Cancel(); }
			_pump = null;
		}
	}
}
