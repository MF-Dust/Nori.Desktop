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
	private readonly Queue<(CancellationTokenSource? Owner, bool IsLevel, Action Notify)> _notifications = new();
	private bool _notifying;
	private CancellationTokenSource? _current;
	private IAudioDevice? _device;
	private bool _playing;
	private bool _disposed;
	private double _volume = 1.0;

	/// <inheritdoc />
	public bool IsPlaying => Volatile.Read(ref _playing);

	/// <inheritdoc />
	public event Action<bool>? PlayingChanged;

	/// <inheritdoc />
	public event Action<double>? VolumeSampled;

	/// <summary>输出音量 0..1，传递给当前设备，并作为下一段播放的初始值。</summary>
	public void SetDeviceVolume(double volume)
	{
		if (!double.IsFinite(volume)) throw new ArgumentOutOfRangeException(nameof(volume), "音量必须是有限数值。");
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
		lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
		PcmAudio pcm = decode(audio.Bytes, audio.Mime);
		if (pcm.Samples.Length == 0) return;

		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		IAudioDevice? device = null;
		try
		{
			lock (_gate)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				// 停旧段和预留新段必须原子完成；设备创建期间也属于这一代。
				Stop();
				_current = linked;
				_device = null;
			}

			linked.Token.ThrowIfCancellationRequested();
			device = openDevice();
			lock (_gate)
			{
				if (!ReferenceEquals(_current, linked) || linked.IsCancellationRequested) return;
				_device = device;
				device.Volume = _volume;
				SetPlaying(true);
			}
			DrainNotifications();
			await Task.Run(() => Pump(device, pcm, linked), CancellationToken.None).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (linked.IsCancellationRequested)
		{
			// 被顶掉或被调用方取消，不是失败。
		}
		finally
		{
			try
			{
				lock (_gate)
				{
					if (ReferenceEquals(_current, linked))
					{
						_device = null;
						_current = null;
						SetPlaying(false);
						// 只有当前段能合嘴，旧段收尾不能覆盖新段的口型。
						EnqueueLevel(null, 0);
					}
				}
				DrainNotifications();
			}
			finally
			{
				// 泵退出后才释放；Stop/Dispose 只负责请求停止。
				device?.Dispose();
			}
		}
	}

	/// <summary>
	/// 一窗一窗地推。
	///
	/// 窗长取 <see cref="PcmLevel.WindowMilliseconds"/>：它同时是「写给设备的块大小」
	/// 和「发电平的节奏」。两者用同一个值，电平就天然对齐到正在播的那一段，
	/// 不需要另外算时间。
	/// </summary>
	private void Pump(IAudioDevice device, PcmAudio pcm, CancellationTokenSource owner)
	{
		CancellationToken cancellationToken = owner.Token;
		cancellationToken.ThrowIfCancellationRequested();
		AudioFormat actual = device.Open(pcm.SampleRate, pcm.Channels);
		cancellationToken.ThrowIfCancellationRequested();
		(float[] samples, float[] level) = PcmResampler.Prepare(pcm, actual, cancellationToken);

		int frameWindow = Math.Max(1, actual.SampleRate * PcmLevel.WindowMilliseconds / 1000);
		double smoothed = 0;

		int frame = 0;
		while (frame < level.Length && !cancellationToken.IsCancellationRequested)
		{
			int take = Math.Min(frameWindow, level.Length - frame);

			// 电平取自那条**单声道轨**，不是写给设备的缓冲。后者的声道数由设备定，
			// 拿它算 RMS 会让嘴张多大取决于用户的音响是几声道。
			smoothed = PcmLevel.Smooth(smoothed, PcmLevel.Rms(level.AsSpan(frame, take)));
			lock (_gate)
			{
				if (!ReferenceEquals(_current, owner) || cancellationToken.IsCancellationRequested) return;
				EnqueueLevel(owner, smoothed);
			}

			DrainNotifications();

			// 写会阻塞到设备吃得下，而这一窗**就是**马上要响的那一窗，所以先发后写。
			int written = device.Write(
				samples.AsSpan(frame * actual.Channels, take * actual.Channels), cancellationToken);
			if (written <= 0) break;          // 被 Stop 打断
			frame += written / actual.Channels;
		}

		if (!cancellationToken.IsCancellationRequested) device.Drain(cancellationToken);
	}

	/// <inheritdoc />
	public void Stop()
	{
		lock (_gate)
		{
			// 与摘除设备互斥，不能对已经交给收尾释放的设备调用 Stop。
			try { _device?.Stop(); }
			finally { _current?.Cancel(); }
		}
	}

	private void SetPlaying(bool playing)
	{
		if (Volatile.Read(ref _playing) == playing) return;
		Volatile.Write(ref _playing, playing);
		_notifications.Enqueue((null, false, () => PlayingChanged?.Invoke(playing)));
	}

	private void EnqueueLevel(CancellationTokenSource? owner, double level)
	{
		if (VolumeSampled is not { } sampled) return;
		// 每位订阅者单独入队；前一位回调启动新段后，其余旧段通知也必须失效。
		foreach (Action<double> subscriber in sampled.GetInvocationList())
			_notifications.Enqueue((owner, true, () => subscriber(level)));
	}

	/// <summary>通知按状态变更顺序串行派发，回调不持有设备状态锁，允许 UI 线程同步停止。</summary>
	private void DrainNotifications()
	{
		lock (_gate)
		{
			if (_notifying) return;
			_notifying = true;
		}
		System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
		while (true)
		{
			Action notify;
			lock (_gate)
			{
				if (!_notifications.TryDequeue(out var notification))
				{
					_notifying = false;
					break;
				}
				// 回调中可能启动下一段；延后的旧电平或归零不能覆盖它。
				if (notification.IsLevel && !ReferenceEquals(notification.Owner, _current)) continue;
				notify = notification.Notify;
			}
			try { notify(); }
			catch (Exception exception)
			{
				failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
			}
		}
		failure?.Throw();
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			Stop();
		}
	}
}
