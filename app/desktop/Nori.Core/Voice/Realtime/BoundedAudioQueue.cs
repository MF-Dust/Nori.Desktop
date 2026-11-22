using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Nori.Core.Voice.Realtime;

internal enum AudioEnqueueResult
{
	Enqueued,
	Completed,
	Overflowed,
	DurationLimitReached,
}

/// <summary>
/// 有界实时音频缓冲队列。
///
/// 通过单块字节上限、通道块数上限与单轮总时长上限三重保护，防止慢消费导致内存无限增长；
/// 溢出或清空时立即排空队列并用 <see cref="Array.Clear"/> 抹除敏感音频内存。
/// </summary>
internal sealed class BoundedAudioQueue : IDisposable
{
	/// <summary>默认待发送字节上限（512 KiB）。</summary>
	public const int DefaultMaxBufferedBytes = 512 * 1024;

	/// <summary>默认通道块数上限。</summary>
	public const int DefaultMaxChunks = 256;

	private readonly Lock _gate = new();
	private readonly Channel<float[]> _channel;
	private readonly CancellationTokenSource _overflowCts = new();
	private readonly int _maxBufferedBytes;
	private readonly int _maxChunks;
	private readonly double _maxDurationMilliseconds;

	private long _currentBytes;
	private long _peakBytes;
	private int _currentChunks;
	private double _totalEnqueuedMilliseconds;
	private bool _overflowed;
	private bool _durationLimitReached;
	private bool _completed;
	private bool _disposed;

	public BoundedAudioQueue(
		int maxBufferedBytes = DefaultMaxBufferedBytes,
		int maxChunks = DefaultMaxChunks,
		int maxDurationMilliseconds = RealtimeVoiceLimits.MaximumUtteranceMilliseconds)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferedBytes);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChunks);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDurationMilliseconds);
		_maxBufferedBytes = maxBufferedBytes;
		_maxChunks = maxChunks;
		_maxDurationMilliseconds = maxDurationMilliseconds;
		_channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(maxChunks)
		{
			SingleReader = true,
			SingleWriter = false,
			FullMode = BoundedChannelFullMode.Wait,
		});
	}

	public int MaxBufferedBytes => _maxBufferedBytes;

	public long CurrentBufferedBytes
	{
		get { lock (_gate) return _currentBytes; }
	}

	public long PeakBufferedBytes
	{
		get { lock (_gate) return _peakBytes; }
	}

	public double TotalEnqueuedMilliseconds
	{
		get { lock (_gate) return _totalEnqueuedMilliseconds; }
	}

	public bool IsOverflowed
	{
		get { lock (_gate) return _overflowed; }
	}

	public bool IsDurationLimitReached
	{
		get { lock (_gate) return _durationLimitReached; }
	}

	public CancellationToken OverflowToken => _overflowCts.Token;

	public AudioEnqueueResult TryEnqueue(ReadOnlySpan<float> samples, int sampleRate, int channels)
	{
		if (samples.IsEmpty) return AudioEnqueueResult.Enqueued;
		int channelCount = Math.Max(1, channels);
		int alignedLength = samples.Length - (samples.Length % channelCount);
		if (alignedLength <= 0) return AudioEnqueueResult.Enqueued;

		bool cancelOverflow = false;
		AudioEnqueueResult result;
		lock (_gate)
		{
			if (_disposed || _overflowed)
				return _overflowed ? AudioEnqueueResult.Overflowed : AudioEnqueueResult.Completed;
			if (_completed)
				return AudioEnqueueResult.Completed;

			if (sampleRate > 0)
			{
				double remainingMs = _maxDurationMilliseconds - _totalEnqueuedMilliseconds;
				if (remainingMs <= 0)
				{
					_durationLimitReached = true;
					_completed = true;
					_channel.Writer.TryComplete();
					return AudioEnqueueResult.DurationLimitReached;
				}

				long maxRemainingFrames = (long)Math.Floor(remainingMs * sampleRate / 1000.0);
				int maxRemainingSamples = (int)Math.Min(int.MaxValue, Math.Max(0, maxRemainingFrames) * channelCount);
				if (maxRemainingSamples <= 0)
				{
					_durationLimitReached = true;
					_completed = true;
					_channel.Writer.TryComplete();
					return AudioEnqueueResult.DurationLimitReached;
				}
				if (alignedLength > maxRemainingSamples)
					alignedLength = maxRemainingSamples - (maxRemainingSamples % channelCount);
				if (alignedLength <= 0)
				{
					_durationLimitReached = true;
					_completed = true;
					_channel.Writer.TryComplete();
					return AudioEnqueueResult.DurationLimitReached;
				}
			}

			long byteLength = (long)alignedLength * sizeof(float);
			if (_currentBytes + byteLength > _maxBufferedBytes || _currentChunks + 1 > _maxChunks)
			{
				_overflowed = true;
				_completed = true;
				DrainAndClearLocked();
				cancelOverflow = true;
				result = AudioEnqueueResult.Overflowed;
			}
			else
			{
				float[] copy = samples[..alignedLength].ToArray();
				if (_channel.Writer.TryWrite(copy))
				{
					_currentBytes += byteLength;
					_currentChunks++;
					if (_currentBytes > _peakBytes) _peakBytes = _currentBytes;
					if (sampleRate > 0)
						_totalEnqueuedMilliseconds += (double)(alignedLength / channelCount) * 1000.0 / sampleRate;
					result = AudioEnqueueResult.Enqueued;
				}
				else
				{
					Array.Clear(copy);
					_overflowed = true;
					_completed = true;
					DrainAndClearLocked();
					cancelOverflow = true;
					result = AudioEnqueueResult.Overflowed;
				}
			}
		}

		if (cancelOverflow)
		{
			try { _overflowCts.Cancel(); }
			catch (ObjectDisposedException) { }
		}
		return result;
	}

	public void Complete()
	{
		lock (_gate)
		{
			if (_disposed || _completed) return;
			_completed = true;
			_channel.Writer.TryComplete();
		}
	}

	public void ClearAndAbort()
	{
		bool shouldCancel;
		lock (_gate)
		{
			if (_disposed) return;
			_completed = true;
			DrainAndClearLocked();
			shouldCancel = !_overflowCts.IsCancellationRequested;
		}
		if (shouldCancel)
		{
			try { _overflowCts.Cancel(); }
			catch (ObjectDisposedException) { }
		}
	}

	public bool TryDequeue(out float[]? samples)
	{
		lock (_gate)
		{
			if (!_channel.Reader.TryRead(out samples)) return false;
			long bytes = (long)samples.Length * sizeof(float);
			_currentBytes = Math.Max(0, _currentBytes - bytes);
			_currentChunks = Math.Max(0, _currentChunks - 1);
			return true;
		}
	}

	public async IAsyncEnumerable<float[]> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
		{
			while (TryDequeue(out float[]? samples))
			{
				yield return samples!;
			}
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_completed = true;
			DrainAndClearLocked();
		}
		try { _overflowCts.Cancel(); }
		catch (ObjectDisposedException) { }
		_overflowCts.Dispose();
	}

	private void DrainAndClearLocked()
	{
		_channel.Writer.TryComplete();
		while (_channel.Reader.TryRead(out float[]? pending))
			Array.Clear(pending);
		_currentBytes = 0;
		_currentChunks = 0;
	}
}
