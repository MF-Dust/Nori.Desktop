using System.Buffers.Binary;
using System.Threading.Channels;
using Nori.Core.Voice.Stt;

namespace Nori.Core.Voice.Realtime;

/// <summary>每轮按配置选择识别路径；本地模式不能降级到云端。</summary>
internal sealed class RealtimeSttPipeline(
	ISttProvider? streamingProvider,
	ISttProvider? batchProvider,
	Func<SttRequestOptions>? requestOptions)
{
	public bool SupportsStreaming(RealtimeVoiceSettings settings) =>
		settings.SttMode == SttMode.RealtimeCloud
		&& streamingProvider?.Capabilities is { SupportsRealtimePcm: true, SupportsIncrementalDelta: true };

	public bool SupportsLocal => batchProvider?.Capabilities is { SupportsBatch: true, SupportsLocalOffline: true };

	public RealtimeStreamingUtterance Open(
		RealtimeVoiceSettings settings, int sampleRate, int channels,
		Func<StreamingSttEvent, TranscriptionEventResult> handle, CancellationToken token) =>
		new(streamingProvider!, () => Options(settings, settings.StreamingSttModel), sampleRate, channels, handle, token);

	public Task<string> TranscribeAsync(RealtimeVoiceSettings settings, RecordedAudio audio, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		if (batchProvider?.Capabilities is not { SupportsBatch: true }
			|| (settings.SttMode == SttMode.Local && !SupportsLocal)
			|| (settings.SttMode != SttMode.Local && batchProvider.Mode == SttMode.Local))
			throw new InvalidOperationException("当前模式没有可用的整段语音识别服务");
		SttRequestOptions options = settings.SttMode == SttMode.Local
			? new SttRequestOptions { Language = settings.Language }
			: Options(settings, settings.BatchSttModel);
		return batchProvider.TranscribeAsync(audio, options, token);
	}

	private SttRequestOptions Options(RealtimeVoiceSettings settings, string model) =>
		(requestOptions?.Invoke() ?? new()) with { Model = model, Language = settings.Language };
}

internal enum StreamingUtteranceResult
{
	Completed,
	FailedBeforeCommit,
	FailedAfterCommit,
}

/// <summary>每轮单独开连接。采集只写有界队列；发送与接收互不阻塞。</summary>
internal sealed class RealtimeStreamingUtterance
{
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
	private readonly Channel<float[]> _audio = Channel.CreateBounded<float[]>(
		new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false });
	private readonly Lock _commitGate = new();
	private bool _commitAttempted;
	private bool _terminated;
	private bool _serverCommitted;

	public RealtimeStreamingUtterance(
		ISttProvider provider, Func<SttRequestOptions> options, int sampleRate, int channels,
		Func<StreamingSttEvent, TranscriptionEventResult> handle, CancellationToken token)
	{
		Completion = Task.Run(() => RunAsync(provider, options, sampleRate, channels, handle, token));
	}

	public Task<StreamingUtteranceResult> Completion { get; }

	public void Append(ReadOnlySpan<float> samples)
	{
		if (samples.IsEmpty || Completion.IsCompleted) return;
		float[] copy = samples.ToArray();
		if (_audio.Writer.TryWrite(copy)) return;
		Array.Clear(copy);
		_audio.Writer.TryComplete(new InvalidOperationException("实时语音发送积压"));
	}

	public void EndAudio() => _audio.Writer.TryComplete();

	private async Task<StreamingUtteranceResult> RunAsync(
		ISttProvider provider, Func<SttRequestOptions> options, int sampleRate, int channels,
		Func<StreamingSttEvent, TranscriptionEventResult> handle, CancellationToken token)
	{
		try
		{
			token.ThrowIfCancellationRequested();
			await using IStreamingSttSession session = await provider.OpenStreamingSessionAsync(options(), token).ConfigureAwait(false);
			using CancellationTokenSource exchange = CancellationTokenSource.CreateLinkedTokenSource(token);
			Task<bool> receive = ReceiveAsync(session, handle, exchange.Token);
			Task send = SendAsync(session, new Pcm24kEncoder(sampleRate, channels), exchange.Token);
			try
			{
				if (await Task.WhenAny(receive, send).ConfigureAwait(false) == send)
					await send.ConfigureAwait(false);
				if (await receive.WaitAsync(RequestTimeout, token).ConfigureAwait(false))
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
			_audio.Writer.TryComplete();
			while (_audio.Reader.TryRead(out float[]? pending)) Array.Clear(pending);
		}
		token.ThrowIfCancellationRequested();
		lock (_commitGate)
			return _commitAttempted || _serverCommitted ? StreamingUtteranceResult.FailedAfterCommit : StreamingUtteranceResult.FailedBeforeCommit;
	}

	private async Task SendAsync(IStreamingSttSession session, Pcm24kEncoder encoder, CancellationToken token)
	{
		await foreach (float[] samples in _audio.Reader.ReadAllAsync(token).ConfigureAwait(false))
		{
			byte[] pcm;
			try { pcm = encoder.Encode(samples); }
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
		using CancellationTokenSource commit = CancellationTokenSource.CreateLinkedTokenSource(token);
		commit.CancelAfter(RequestTimeout);
		lock (_commitGate)
		{
			token.ThrowIfCancellationRequested();
			if (_terminated || _serverCommitted) return;
			// 先跨过不可重放边界：发送报错也不能证明服务端没收到 commit。
			_commitAttempted = true;
		}
		await session.CommitAsync(commit.Token).ConfigureAwait(false);
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

	/// <summary>样本片段之间保留重采样相位，避免每帧取整导致音频时长漂移。</summary>
	private sealed class Pcm24kEncoder(int sampleRate, int channels)
	{
		private const int OutputRate = 24000;
		private int _weight;
		private double _sum;

		public byte[] Encode(ReadOnlySpan<float> samples)
		{
			int frames = samples.Length / channels;
			int bytes = checked((int)(((long)frames * OutputRate + _weight) / sampleRate * 2));
			byte[] pcm = new byte[bytes];
			int offset = 0;
			for (int frame = 0; frame < frames; frame++)
			{
				double mono = 0;
				foreach (float sample in samples.Slice(frame * channels, channels))
					mono += float.IsFinite(sample) ? sample : 0;
				mono /= channels;
				int remaining = OutputRate;
				while (remaining > 0)
				{
					int take = Math.Min(remaining, sampleRate - _weight);
					_sum += mono * take;
					_weight += take;
					remaining -= take;
					if (_weight != sampleRate) continue;
					short value = (short)Math.Round(Math.Clamp(_sum / sampleRate, -1.0, 1.0) * short.MaxValue);
					BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset), value);
					offset += 2;
					_weight = 0;
					_sum = 0;
				}
			}
			return pcm;
		}
	}
}
