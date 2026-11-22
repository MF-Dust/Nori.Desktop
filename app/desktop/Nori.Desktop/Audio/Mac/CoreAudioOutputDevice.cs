using Nori.Core.Voice.Audio;

namespace Nori.Desktop.Audio.Mac;

/// <summary>
/// macOS 输出设备。样本先进入环形缓冲，AudioQueue 回调再取走。
/// 音量交给队列的 <c>kAudioQueueParam_Volume</c>，不在样本上再乘一次。
/// </summary>
internal sealed class CoreAudioOutputDevice : IAudioDevice
{
	private readonly ICoreAudioQueue _queue;
	private readonly int _bufferSamples;
	private readonly bool _ownsQueue;
	private PcmBlockingBuffer? _buffer;
	private double _volume = 1;
	private bool _open;
	private bool _disposed;

	internal CoreAudioOutputDevice(string? deviceId = null) : this(new CoreAudioQueue(deviceId), 0, ownsQueue: true) { }

	internal CoreAudioOutputDevice(ICoreAudioQueue queue, int bufferSamples = 0)
		: this(queue, bufferSamples, ownsQueue: false) { }

	private CoreAudioOutputDevice(ICoreAudioQueue queue, int bufferSamples, bool ownsQueue)
	{
		_queue = queue;
		_bufferSamples = bufferSamples;
		_ownsQueue = ownsQueue;
	}

	public double Volume
	{
		get => Volatile.Read(ref _volume);
		set
		{
			if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "音量必须是有限数值");
			double clamped = Math.Clamp(value, 0, 1);
			Volatile.Write(ref _volume, clamped);
			if (_open) _queue.SetVolume(clamped);
		}
	}

	public AudioFormat Open(int sampleRate, int channels)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_open) throw new AudioDeviceException("输出设备已经打开");
		try
		{
			AudioFormat format = _queue.OpenOutput(sampleRate, channels, Fill);
			int capacity = _bufferSamples > 0
				? _bufferSamples
				: Math.Max(1, format.SampleRate * Math.Max(1, format.Channels));
			_buffer = new PcmBlockingBuffer(capacity);
			_queue.SetVolume(Volume);
			_queue.Start();
			_open = true;
			return format;
		}
		catch (Exception exception)
		{
			_buffer?.Dispose();
			_buffer = null;
			_queue.Abort();
			if (exception is AudioDeviceException) throw;
			if (exception is DllNotFoundException or EntryPointNotFoundException or TypeLoadException)
				throw new AudioDeviceException("未找到 AudioToolbox。macOS 系统音频组件不可用。", exception);
			throw;
		}
	}

	public int Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
	{
		if (samples.IsEmpty) return 0;
		if (_buffer is null) throw new AudioDeviceException("输出设备尚未打开");
		return _buffer.Write(samples, cancellationToken);
	}

	public void Drain(CancellationToken cancellationToken)
	{
		if (_buffer is null || !_open) return;
		_buffer.Drain(cancellationToken);
		if (cancellationToken.IsCancellationRequested || _buffer is null) return;
		_queue.Finish();
	}

	public void Stop()
	{
		_buffer?.Stop();
		if (_open) _queue.StopImmediate();
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		Stop();
		_buffer?.Dispose();
		_buffer = null;
		if (_ownsQueue) _queue.Dispose();
	}

	private int Fill(Span<float> destination)
	{
		int read = _buffer?.TryRead(destination) ?? 0;
		if (read < destination.Length) destination[Math.Max(0, read)..].Clear();
		return destination.Length;
	}
}
