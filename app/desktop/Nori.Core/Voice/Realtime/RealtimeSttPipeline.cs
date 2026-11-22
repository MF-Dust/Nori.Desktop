using Nori.Core.Voice.Audio;
using Nori.Core.Voice.Stt;

namespace Nori.Core.Voice.Realtime;

/// <summary>每轮按配置选择识别路径；本地模式不能降级到云端。</summary>
internal sealed class RealtimeSttPipeline(
	ISttProvider? streamingProvider,
	ISttProvider? batchProvider,
	Func<SttRequestOptions>? requestOptions,
	SttProviderRegistry? registry = null,
	bool allowNetwork = true)
{
	public bool SupportsStreaming(RealtimeVoiceSettings settings) =>
		settings.SttMode == SttMode.RealtimeCloud && StreamingProvider() is not null;

	public bool SupportsLocal => BatchProvider(SttMode.Local) is not null;

	public bool CanTranscribeBatch(RealtimeVoiceSettings settings) => BatchProvider(settings.SttMode) is not null;

	public RealtimeStreamingUtterance Open(
		RealtimeVoiceSettings settings, int sampleRate, int channels,
		Func<StreamingSttEvent, TranscriptionEventResult> handle, CancellationToken token,
		int maxBufferedBytes = BoundedAudioQueue.DefaultMaxBufferedBytes) =>
		new(StreamingProvider() ?? throw new InvalidOperationException("当前没有可用的流式语音识别服务"),
			() => Options(settings, settings.StreamingSttModel), sampleRate, channels, handle, token, maxBufferedBytes);

	public Task<string> TranscribeAsync(RealtimeVoiceSettings settings, RecordedAudio audio, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		ISttProvider provider = BatchProvider(settings.SttMode)
			?? throw new InvalidOperationException("当前模式没有可用的整段语音识别服务");
		SttRequestOptions options = settings.SttMode == SttMode.Local
			? new SttRequestOptions { Language = settings.Language }
			: Options(settings, settings.BatchSttModel);
		return provider.TranscribeAsync(audio, options, token);
	}

	private ISttProvider? StreamingProvider() => registry is null
		? streamingProvider?.Capabilities is { SupportsRealtimePcm: true, SupportsIncrementalDelta: true } ? streamingProvider : null
		: Select(SttMode.RealtimeCloud, new()
		{
			RequireRealtimePcm = true,
			RequireIncrementalDelta = true,
		});

	private ISttProvider? BatchProvider(SttMode mode)
	{
		bool local = mode == SttMode.Local;
		if (registry is null)
			return batchProvider?.Capabilities is { SupportsBatch: true } capabilities
				&& (local ? capabilities.SupportsLocalOffline : batchProvider.Mode != SttMode.Local) ? batchProvider : null;
		return Select(local ? SttMode.Local : SttMode.BatchCloud, new()
		{
			RequireBatch = true,
			RequireLocalOffline = local,
		});
	}

	private ISttProvider? Select(SttMode mode, SttSelectionCriteria criteria) =>
		registry!.FindMatching(criteria with { PreferredMode = mode, AllowNetwork = allowNetwork })
			.FirstOrDefault(entry => entry.Descriptor.Mode == mode)?.Provider;

	private SttRequestOptions Options(RealtimeVoiceSettings settings, string model) =>
		(requestOptions?.Invoke() ?? new()) with { Model = model, Language = settings.Language };
}

internal enum StreamingUtteranceResult
{
	Completed,
	FailedBeforeCommit,
	OverflowBeforeCommit,
	FailedAfterCommit,
}

/// <summary>每轮单独开连接。采集只写有界队列；发送与接收互不阻塞，溢出时立即释放缓冲。</summary>
internal sealed class RealtimeStreamingUtterance
{
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
	private readonly BoundedAudioQueue _queue;
	private readonly int _sampleRate;
	private readonly int _channels;
	private readonly Lock _commitGate = new();
	private bool _commitAttempted;
	private bool _terminated;
	private bool _serverCommitted;

	public RealtimeStreamingUtterance(
		ISttProvider provider, Func<SttRequestOptions> options, int sampleRate, int channels,
		Func<StreamingSttEvent, TranscriptionEventResult> handle, CancellationToken token,
		int maxBufferedBytes = BoundedAudioQueue.DefaultMaxBufferedBytes)
	{
		_sampleRate = sampleRate;
		_channels = Math.Max(1, channels);
		_queue = new BoundedAudioQueue(maxBufferedBytes);
		Completion = Task.Run(() => RunAsync(provider, options, sampleRate, _channels, handle, token));
	}

	public Task<StreamingUtteranceResult> Completion { get; }

	public long PeakBufferedBytes => _queue.PeakBufferedBytes;

	public long CurrentBufferedBytes => _queue.CurrentBufferedBytes;

	public bool IsOverflowed => _queue.IsOverflowed;

	public void Append(ReadOnlySpan<float> samples)
	{
		if (samples.IsEmpty || Completion.IsCompleted) return;
		_queue.TryEnqueue(samples, _sampleRate, _channels);
	}

	public void EndAudio() => _queue.Complete();

	public void Abort() => _queue.ClearAndAbort();

	private async Task<StreamingUtteranceResult> RunAsync(
		ISttProvider provider, Func<SttRequestOptions> options, int sampleRate, int channels,
		Func<StreamingSttEvent, TranscriptionEventResult> handle, CancellationToken token)
	{
		try
		{
			token.ThrowIfCancellationRequested();
			using CancellationTokenSource openCts = CancellationTokenSource.CreateLinkedTokenSource(token, _queue.OverflowToken);
			await using IStreamingSttSession session = await provider.OpenStreamingSessionAsync(options(), openCts.Token).ConfigureAwait(false);
			using CancellationTokenSource exchange = CancellationTokenSource.CreateLinkedTokenSource(token, _queue.OverflowToken);
			Task<bool> receive = ReceiveAsync(session, handle, exchange.Token);
			Task send = SendAsync(session, new ContinuousAudioProcessor(sampleRate, channels, 24000), exchange.Token);
			try
			{
				if (await Task.WhenAny(receive, send).ConfigureAwait(false) == send)
					await send.ConfigureAwait(false);
				if (!_queue.IsOverflowed && await receive.WaitAsync(RequestTimeout, exchange.Token).ConfigureAwait(false))
					return StreamingUtteranceResult.Completed;
			}
			finally
			{
				await exchange.CancelAsync().ConfigureAwait(false);
				try { await Task.WhenAll(receive, send).ConfigureAwait(false); }
				catch { /* 主路径已处理结果；不传播提供商原始异常。 */ }
			}
		}
		catch (Exception) when (!token.IsCancellationRequested)
		{
			// 提供商异常可能包含端点、凭据或识别正文，不离开此边界。
		}
		finally
		{
			_queue.Dispose();
		}
		token.ThrowIfCancellationRequested();
		lock (_commitGate)
		{
			if (_commitAttempted || _serverCommitted) return StreamingUtteranceResult.FailedAfterCommit;
			return _queue.IsOverflowed ? StreamingUtteranceResult.OverflowBeforeCommit : StreamingUtteranceResult.FailedBeforeCommit;
		}
	}

	private async Task SendAsync(IStreamingSttSession session, ContinuousAudioProcessor processor, CancellationToken token)
	{
		try
		{
			await foreach (float[] samples in _queue.ReadAllAsync(token).ConfigureAwait(false))
			{
				byte[] pcm;
				try { pcm = processor.ProcessToPcm16Bytes(samples); }
				finally { Array.Clear(samples); }
				try
				{
					if (pcm.Length == 0) continue;
					using CancellationTokenSource send = CancellationTokenSource.CreateLinkedTokenSource(token);
					send.CancelAfter(RequestTimeout);
					await session.AppendAudioAsync(pcm, send.Token).ConfigureAwait(false);
				}
				finally { Array.Clear(pcm); }
			}

			byte[] tail = processor.FlushToPcm16Bytes();
			if (tail.Length > 0)
			{
				try
				{
					using CancellationTokenSource send = CancellationTokenSource.CreateLinkedTokenSource(token);
					send.CancelAfter(RequestTimeout);
					await session.AppendAudioAsync(tail, send.Token).ConfigureAwait(false);
				}
				finally { Array.Clear(tail); }
			}

			using CancellationTokenSource commit = CancellationTokenSource.CreateLinkedTokenSource(token);
			commit.CancelAfter(RequestTimeout);
			lock (_commitGate)
			{
				token.ThrowIfCancellationRequested();
				if (_terminated || _serverCommitted || _queue.IsOverflowed) return;
				// 先跨过不可重放边界：发送报错也不能证明服务端没收到 commit。
				_commitAttempted = true;
			}
			await session.CommitAsync(commit.Token).ConfigureAwait(false);
		}
		finally
		{
			processor.Reset();
		}
	}

	private async Task<bool> ReceiveAsync(
		IStreamingSttSession session, Func<StreamingSttEvent, TranscriptionEventResult> handle, CancellationToken token)
	{
		try
		{
			await foreach (StreamingSttEvent sttEvent in session.Events.ReadAllAsync(token).ConfigureAwait(false))
			{
				TranscriptionEventResult result = handle(sttEvent);
				lock (_commitGate)
				{
					if (result is TranscriptionEventResult.Committed or TranscriptionEventResult.Completed) _serverCommitted = true;
					if (result is TranscriptionEventResult.Completed or TranscriptionEventResult.Failed) _terminated = true;
				}
				if (result == TranscriptionEventResult.Completed) return true;
				if (result == TranscriptionEventResult.Failed) return false;
			}
			return false;
		}
		finally { lock (_commitGate) _terminated = true; }
	}
}
