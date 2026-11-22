using Nori.Core.Voice.Audio;

namespace Nori.Desktop.Audio.Mac;

/// <summary>macOS 输入设备。AudioQueue 回调把样本推进环形缓冲，<see cref="Read"/> 再拉出来。</summary>
internal sealed class CoreAudioCaptureDevice : IAudioCaptureDevice
{
	private readonly ICoreAudioQueue _queue;
	private readonly int _bufferSamples;
	private readonly bool _ownsQueue;
	private PcmBlockingBuffer? _buffer;
	private bool _disposed;

	internal CoreAudioCaptureDevice(string? deviceId = null) : this(new CoreAudioQueue(deviceId), 0, ownsQueue: true) { }

	internal CoreAudioCaptureDevice(ICoreAudioQueue queue, int bufferSamples = 0)
		: this(queue, bufferSamples, ownsQueue: false) { }

	private CoreAudioCaptureDevice(ICoreAudioQueue queue, int bufferSamples, bool ownsQueue)
	{
		_queue = queue;
		_bufferSamples = bufferSamples;
		_ownsQueue = ownsQueue;
	}

	public AudioFormat Open()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		try
		{
			AudioFormat format = _queue.OpenInput(Accept);
			int capacity = _bufferSamples > 0
				? _bufferSamples
				: Math.Max(1, format.SampleRate * Math.Max(1, format.Channels));
			_buffer = new PcmBlockingBuffer(capacity);
			_queue.Start();
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

	public int Read(Span<float> buffer, CancellationToken cancellationToken)
	{
		if (buffer.IsEmpty) return 0;
		if (_buffer is null) throw new AudioDeviceException("输入设备尚未打开");
		return _buffer.Read(buffer, cancellationToken);
	}

	public void Stop()
	{
		_buffer?.Stop();
		_queue.StopImmediate();
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

	private void Accept(ReadOnlySpan<float> samples) => _buffer?.TryWrite(samples);
}
