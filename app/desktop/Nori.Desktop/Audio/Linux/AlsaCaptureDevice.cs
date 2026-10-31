using System.Buffers;
using Nori.Core.Voice.Audio;

namespace Nori.Desktop.Audio.Linux;

/// <summary>Linux 输入设备。读到的 16 位样本还原成浮点后交给上层重采样。</summary>
internal sealed class AlsaCaptureDevice : IAudioCaptureDevice
{
	private readonly IAlsaFactory _factory;
	private readonly Lock _gate = new();
	private IAlsaPcm? _pcm;
	private int _channels = 1;
	private volatile bool _stopped;
	private bool _disposed;

	internal AlsaCaptureDevice() : this(new AlsaNativeFactory()) { }

	internal AlsaCaptureDevice(IAlsaFactory factory) => _factory = factory;

	public AudioFormat Open()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_gate)
		{
			_stopped = false;
			_pcm = _factory.OpenInput(48_000, 1);
			_channels = Math.Max(1, _pcm.Format.Channels);
			return _pcm.Format;
		}
	}

	public int Read(Span<float> buffer, CancellationToken cancellationToken)
	{
		if (buffer.IsEmpty) return 0;
		int aligned = buffer.Length - (buffer.Length % _channels);
		if (aligned <= 0) return 0;
		short[] rented = ArrayPool<short>.Shared.Rent(aligned);
		try
		{
			while (!_stopped && !cancellationToken.IsCancellationRequested)
			{
				int frames = 0;
				lock (_gate)
				{
					if (_pcm is null || _stopped || cancellationToken.IsCancellationRequested) return 0;
					// 首次读取会启动 PREPARED 状态的采集；先等数据会让部分设备永远无法启动。
					frames = _pcm.Read(rented.AsSpan(0, aligned));
					if (frames <= 0)
					{
						int state = _pcm.Wait(10);
						if (_stopped || cancellationToken.IsCancellationRequested) return 0;
						if (state < 0) throw new AudioDeviceException("等待 ALSA 输入设备失败");
					}
				}
				if (frames <= 0) continue;
				int samples = frames * _channels;
				PcmSampleScale.FromInt16(rented.AsSpan(0, samples), buffer[..samples]);
				return samples;
			}
			return 0;
		}
		finally
		{
			ArrayPool<short>.Shared.Return(rented);
		}
	}

	public void Stop()
	{
		_stopped = true;
		lock (_gate)
		{
			_pcm?.Drop();
		}
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		Stop();
		lock (_gate)
		{
			_pcm?.Dispose();
			_pcm = null;
		}
	}
}
