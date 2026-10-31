using System.Buffers;
using Nori.Core.Voice.Audio;

namespace Nori.Desktop.Audio.Linux;

/// <summary>
/// Linux 输出设备。音量在样本上相乘后再交给 ALSA。
/// 等待使用短超时，<see cref="Stop"/> 置位后下一轮就会返回。
/// </summary>
internal sealed class AlsaAudioDevice : IAudioDevice
{
	private readonly IAlsaFactory _factory;
	private readonly Lock _gate = new();
	private IAlsaPcm? _pcm;
	private int _channels = 1;
	private double _volume = 1;
	private volatile bool _stopped;
	private bool _disposed;

	internal AlsaAudioDevice() : this(new AlsaNativeFactory()) { }

	internal AlsaAudioDevice(IAlsaFactory factory) => _factory = factory;

	public double Volume
	{
		get => Volatile.Read(ref _volume);
		set
		{
			if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "音量必须是有限数值");
			Volatile.Write(ref _volume, Math.Clamp(value, 0, 1));
		}
	}

	public AudioFormat Open(int sampleRate, int channels)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_gate)
		{
			if (_pcm is not null) throw new AudioDeviceException("输出设备已经打开");
			_stopped = false;
			_pcm = _factory.OpenOutput(sampleRate, channels);
			_channels = Math.Max(1, _pcm.Format.Channels);
			return _pcm.Format;
		}
	}

	public int Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
	{
		if (samples.IsEmpty) return 0;
		short[] rented = ArrayPool<short>.Shared.Rent(samples.Length);
		try
		{
			PcmSampleScale.ToInt16(samples, rented.AsSpan(0, samples.Length), Volume);
			int offset = 0;
			while (offset < samples.Length && !_stopped && !cancellationToken.IsCancellationRequested)
			{
				int frames = 0;
				lock (_gate)
				{
					if (_pcm is null || _stopped || cancellationToken.IsCancellationRequested) break;
					int state = _pcm.Wait(10);
					if (_stopped || cancellationToken.IsCancellationRequested) break;
					if (state < 0) throw new AudioDeviceException("等待 ALSA 输出设备失败");
					if (state > 0)
					{
						int available = (samples.Length - offset) / _channels;
						if (available <= 0) break;
						frames = _pcm.Write(rented.AsSpan(offset, available * _channels));
					}
				}
				if (frames <= 0) continue;
				offset += frames * _channels;
			}
			return offset;
		}
		finally
		{
			ArrayPool<short>.Shared.Return(rented);
		}
	}

	public void Drain(CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			if (_pcm is null || _stopped || cancellationToken.IsCancellationRequested) return;
			_pcm.Drain();
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
