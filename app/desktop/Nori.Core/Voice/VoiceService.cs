using System.Threading.Channels;
using Nori.Core.Configuration;

namespace Nori.Core.Voice;

/// <summary>
/// 最近一次朗读流水线的只读指标快照，供诊断与延迟观测读取。
/// </summary>
public sealed record VoicePlaybackMetrics(
	DateTimeOffset StartedAtUtc,
	DateTimeOffset? FirstChunkSynthesizedAtUtc,
	DateTimeOffset? FirstChunkPlaybackStartedAtUtc,
	DateTimeOffset? CompletedAtUtc,
	int TotalChunks,
	int SynthesizedChunks,
	int PlayedChunks,
	bool Cancelled,
	bool Faulted);

/// <summary>
/// 全局语音服务。
///
/// TTS 合成采用有界的生产/播放流水线：后台最多预取两段，首段合成完成即可开始播放，
/// 不让长回复一次性等待全部音频。停止或配置变化会取消合成、清空播放并通知观察者。
/// </summary>
public sealed class VoiceService : IDisposable
{
	private readonly HttpClient _httpClient;
	private readonly ConfigStore _config;
	private readonly IAudioPlayback? _playback;
	private readonly Func<IMicrophoneRecorder?> _recorderFactory;
	private readonly SemaphoreSlim _queue = new(1, 1);
	private readonly object _speechGate = new();
	private readonly object _metricsGate = new();
	private CancellationTokenSource? _speechCts;
	private VoicePlaybackMetrics? _lastPlaybackMetrics;
	private bool _speaking;
	private bool _disposed;

	/// <summary>合成结果缓存，可供诊断和测试读取。</summary>
	public AudioSynthesisCache SynthesisCache { get; } = new();

	/// <summary>最近一次（或正在进行）的朗读流水线统计指标。</summary>
	public VoicePlaybackMetrics? LastPlaybackMetrics
	{
		get { lock (_metricsGate) return _lastPlaybackMetrics; }
	}

	/// <summary>音量变化通知。</summary>
	public event Action<double>? VolumeChanged;

	/// <summary>朗读状态变化通知。</summary>
	public event Action<bool>? SpeakingChanged;

	/// <summary>朗读流水线指标变化通知。</summary>
	public event Action<VoicePlaybackMetrics>? PlaybackMetricsUpdated;

	public VoiceService(
		HttpClient httpClient,
		ConfigStore config,
		IAudioPlayback? playback,
		Func<IMicrophoneRecorder?> recorderFactory,
		Nori.Core.Data.AppStoragePaths? paths = null)
	{
		_httpClient = httpClient;
		_config = config;
		_playback = playback;
		_recorderFactory = recorderFactory;
		_paths = paths;
	}

	private readonly Nori.Core.Data.AppStoragePaths? _paths;

	// ---- 音量 ----

	/// <summary>读取全局音量 (0.0 ~ 1.0)。</summary>
	public double GetVolume()
	{
		string raw = _config.GetStringOr("audio_volume", "1");
		if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
		{
			return 1.0;
		}
		return Math.Clamp(value, 0, 1);
	}

	/// <summary>设置全局音量并持久化。</summary>
	public void SetVolume(double volume)
	{
		double clamped = Math.Clamp(volume, 0, 1);
		_config.Set("audio_volume", new ConfigValue.Text(clamped.ToString("0.0######", System.Globalization.CultureInfo.InvariantCulture)));
		VolumeChanged?.Invoke(clamped);
	}

	// ---- 播放状态 ----

	/// <summary>是否正在朗读 (含合成和播放阶段)。</summary>
	public bool IsSpeaking => Volatile.Read(ref _speaking);

	/// <summary>停止朗读并清空队列。</summary>
	public void Stop()
	{
		CancellationTokenSource? speechCts;
		lock (_speechGate) speechCts = _speechCts;
		CancelIgnoringDisposedCallbacks(speechCts);
		_playback?.Stop();
	}

	/// <summary>在设备切换或播放异常后释放损坏的播放状态并重置朗读标志，保持口型与状态一致。</summary>
	public void RecoverPlayback()
	{
		CancellationTokenSource? speechCts;
		lock (_speechGate)
		{
			speechCts = _speechCts;
			_speechCts = null;
		}
		_playback?.RecoverPlayback();
		CancelIgnoringDisposedCallbacks(speechCts);
		bool notifyStopped = false;
		lock (_speechGate)
		{
			if (_speechCts is null && Volatile.Read(ref _speaking))
			{
				Volatile.Write(ref _speaking, false);
				notifyStopped = true;
			}
		}
		if (notifyStopped) SpeakingChanged?.Invoke(false);
	}

	/// <summary>
	/// 配置发生变化时取消旧请求，避免旧端点的音频继续播放；同时丢弃旧缓存。
	/// </summary>
	public void NotifyConfigurationChanged()
	{
		SynthesisCache.Clear();
		Stop();
	}

	// ---- 合成与播放 ----

	/// <summary>解析当前配置的 TTS 提供商名。</summary>
	public string ResolveProviderName() =>
		_config.GetStringOr("tts_provider", "openai") is {Length: > 0} saved ? saved : "openai";

	/// <summary>
	/// 朗读文本：按句切段并以有界流水线边合成边播放。
	/// </summary>
	public async Task SpeakAsync(string text, TtsSynthesizeOptions? options = null, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		string sanitized = VoiceTextSanitizer.StripKaomoji(text);
		if (string.IsNullOrWhiteSpace(sanitized)) return;
		IAudioPlayback player = _playback ?? throw new InvalidOperationException("音频播放后端不可用");
		ThrowIfDisposed();
		cancellationToken.ThrowIfCancellationRequested();

		CancellationTokenSource speechCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		CancellationTokenSource? previous;
		bool notifySpeaking;
		lock (_speechGate)
		{
			if (_disposed)
			{
				speechCts.Dispose();
				throw new ObjectDisposedException(nameof(VoiceService));
			}
			previous = _speechCts;
			_speechCts = speechCts;
			notifySpeaking = !Volatile.Read(ref _speaking);
			Volatile.Write(ref _speaking, true);
		}

		try
		{
			if (notifySpeaking) SpeakingChanged?.Invoke(true);
			CancelIgnoringDisposedCallbacks(previous);
			await _queue.WaitAsync(speechCts.Token);
			try
			{
				// IndexTTS-2 整段一次合成（避免长回复拆成多段触发多次 API 调用撞限流）；
				// 其余 provider 保持按句切段、边合成边播放。
				IReadOnlyList<string> chunks = ResolveProviderName() == "indextts"
					? [sanitized]
					: SentenceChunker.Split(sanitized);
				if (chunks.Count == 0) return;
				// 仅在当前轮次真正持有播放队列期间绑定 Stop，避免跨轮次误停新接管的播放。
				using CancellationTokenRegistration stopRegistration = speechCts.Token.Register(
					static state => ((IAudioPlayback)state!).Stop(),
					player);
				await RunPipelineAsync(player, chunks, options, speechCts.Token);
			}
			finally
			{
				_queue.Release();
			}
		}
		finally
		{
			bool notifyStopped = false;
			lock (_speechGate)
			{
				if (ReferenceEquals(_speechCts, speechCts))
				{
					_speechCts = null;
					if (Volatile.Read(ref _speaking))
					{
						Volatile.Write(ref _speaking, false);
						notifyStopped = true;
					}
				}
			}
			if (notifyStopped) SpeakingChanged?.Invoke(false);
			speechCts.Dispose();
		}
	}

	private async Task RunPipelineAsync(
		IAudioPlayback player,
		IReadOnlyList<string> chunks,
		TtsSynthesizeOptions? options,
		CancellationToken cancellationToken)
	{
		using CancellationTokenSource pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		Channel<EncodedAudio> audioChannel = Channel.CreateBounded<EncodedAudio>(new BoundedChannelOptions(VoiceAudioLimits.SynthesisQueueCapacity)
		{
			FullMode = BoundedChannelFullMode.Wait,
			SingleWriter = true,
			SingleReader = true,
		});
		DateTimeOffset startedAt = DateTimeOffset.UtcNow;
		DateTimeOffset? firstSynthesizedAt = null;
		DateTimeOffset? firstPlaybackStartedAt = null;
		int synthesizedCount = 0;
		int playedCount = 0;
		bool cancelled = false;
		bool faulted = false;
		PublishMetrics(new VoicePlaybackMetrics(startedAt, null, null, null, chunks.Count, 0, 0, false, false));

		async Task ProduceAsync()
		{
			try
			{
				foreach (string chunk in chunks)
				{
					pipelineCts.Token.ThrowIfCancellationRequested();
					// 先等队列出现空位再发起合成请求，严格限制预取在途与内存占用。
					if (!await audioChannel.Writer.WaitToWriteAsync(pipelineCts.Token).ConfigureAwait(false)) break;
					EncodedAudio audio = await SynthesizeAsync(chunk, options, pipelineCts.Token);
					int count = Interlocked.Increment(ref synthesizedCount);
					DateTimeOffset? synthesizedAt;
					DateTimeOffset? playbackAt;
					lock (_metricsGate)
					{
						firstSynthesizedAt ??= DateTimeOffset.UtcNow;
						synthesizedAt = firstSynthesizedAt;
						playbackAt = firstPlaybackStartedAt;
					}
					if (count == 1)
					{
						PublishMetrics(new VoicePlaybackMetrics(
							startedAt, synthesizedAt, playbackAt, null,
							chunks.Count, count, Volatile.Read(ref playedCount), false, false));
					}
					await audioChannel.Writer.WriteAsync(audio, pipelineCts.Token);
				}
				audioChannel.Writer.TryComplete();
			}
			catch (Exception exception)
			{
				audioChannel.Writer.TryComplete(exception);
				CancelIgnoringDisposedCallbacks(pipelineCts);
				throw;
			}
		}

		async Task ConsumeAsync()
		{
			try
			{
				await foreach (EncodedAudio audio in audioChannel.Reader.ReadAllAsync(pipelineCts.Token))
				{
					DateTimeOffset? synthesizedAt;
					DateTimeOffset? playbackStartedAt;
					lock (_metricsGate)
					{
						firstPlaybackStartedAt ??= DateTimeOffset.UtcNow;
						synthesizedAt = firstSynthesizedAt;
						playbackStartedAt = firstPlaybackStartedAt;
					}
					if (Volatile.Read(ref playedCount) == 0)
					{
						PublishMetrics(new VoicePlaybackMetrics(
							startedAt, synthesizedAt, playbackStartedAt, null,
							chunks.Count, Volatile.Read(ref synthesizedCount), 0, false, false));
					}
					await player.PlayAsync(audio, pipelineCts.Token);
					pipelineCts.Token.ThrowIfCancellationRequested();
					Interlocked.Increment(ref playedCount);
				}
			}
			catch (OperationCanceledException) when (
				pipelineCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
			{
				// 生产者失败时会取消流水线以停止消费者；保留生产者的原始异常。
			}
			catch
			{
				CancelIgnoringDisposedCallbacks(pipelineCts);
				throw;
			}
		}

		Task producer = ProduceAsync();
		Task consumer = ConsumeAsync();
		try
		{
			// 先失败的一方是主错误; 聚合到 AggregateException 会把 Provider 失败
			// 和消费者的取消噪音混成一条不可分类的遥测。
			await VoicePipeline.JoinAsync(producer, consumer);
		}
		catch (OperationCanceledException)
		{
			cancelled = true;
			CancelIgnoringDisposedCallbacks(pipelineCts);
			player.RecoverPlayback();
			throw;
		}
		catch
		{
			faulted = true;
			CancelIgnoringDisposedCallbacks(pipelineCts);
			player.RecoverPlayback();
			throw;
		}
		finally
		{
			audioChannel.Writer.TryComplete();
			while (audioChannel.Reader.TryRead(out _)) { }
			DateTimeOffset? synthesizedAt;
			DateTimeOffset? playbackAt;
			lock (_metricsGate)
			{
				synthesizedAt = firstSynthesizedAt;
				playbackAt = firstPlaybackStartedAt;
			}
			PublishMetrics(new VoicePlaybackMetrics(
				startedAt, synthesizedAt, playbackAt, DateTimeOffset.UtcNow,
				chunks.Count, Volatile.Read(ref synthesizedCount), Volatile.Read(ref playedCount),
				cancelled, faulted));
		}
	}

	private void PublishMetrics(VoicePlaybackMetrics metrics)
	{
		lock (_metricsGate) _lastPlaybackMetrics = metrics;
		PlaybackMetricsUpdated?.Invoke(metrics);
	}

	/// <summary>取消时忽略已释放资源的回调，避免覆盖流水线的原始失败；其他回调错误仍抛出。</summary>
	private static void CancelIgnoringDisposedCallbacks(CancellationTokenSource? source)
	{
		try { source?.Cancel(); }
		catch (ObjectDisposedException) { }
		catch (AggregateException exception) when (
			exception.Flatten().InnerExceptions.All(error => error is ObjectDisposedException))
		{
			// Cancel 会把同步回调异常聚合，关联令牌还可能产生多层聚合。
		}
	}

	/// <summary>仅合成不播放 (测试/预检用)，返回实际 MIME。</summary>
	public Task<EncodedAudio> SynthesizeAsync(
		string text, TtsSynthesizeOptions? options = null, CancellationToken cancellationToken = default) =>
		SynthesizeCoreAsync(text.Trim(), options, cancellationToken);

	private async Task<EncodedAudio> SynthesizeCoreAsync(
		string text, TtsSynthesizeOptions? options, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("合成文本不能为空");
		// 合成前清洗颜文字/装饰符号（AI 回复常带 (๑•̀ㅂ•́)و✧ 等，会被 TTS 读成怪声）。
		// 清洗后再算缓存 key，避免脏文本占缓存。
		text = VoiceTextSanitizer.StripKaomoji(text);
		if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("合成文本清洗后为空");
		string providerName = ResolveProviderName();
		ITtsProvider provider = CreateProvider(providerName);
		TtsSynthesizeOptions merged = MergeOptions(options);
		// indextts 的音色由模板驱动：解析出真实 voice_id，让缓存 key 用实际音色，
		// 换模板后不会命中旧模板的合成缓存。
		if (provider is IndexTtsProvider indexTts
			&& _config.GetStringOr("indextts_template_audio", "").Trim() is {Length: > 0})
		{
			string resolvedVoice = await indexTts.ResolveTemplateVoiceAsync(cancellationToken);
			merged = merged with {Voice = resolvedVoice};
		}
		string endpoint = ResolveProviderEndpoint(providerName);
		// IndexTTS 的情绪强度会改变音频结果，也要参与缓存身份；这样配置从任何入口变化都不会命中旧音频。
		if (provider is IndexTtsProvider cacheAwareIndexTts)
		{
			endpoint = $"{endpoint}:{cacheAwareIndexTts.GetSynthesisCacheVariant()}";
		}
		string key = AudioSynthesisCache.CreateKey(endpoint, merged.Voice, merged.Speed, text, merged.EmotionText);
		if (SynthesisCache.TryGet(key, out EncodedAudio cached)) return cached;

		EncodedAudio audio = await provider.SynthesizeAsync(text, merged, cancellationToken);
		EncodedAudio validated = AudioMime.ValidateEncoded(audio.Bytes, audio.Mime);
		SynthesisCache.Put(key, validated);
		return validated;
	}

	private TtsSynthesizeOptions MergeOptions(TtsSynthesizeOptions? options) => new()
	{
		Voice = options?.Voice is {Length: > 0} voice
			? voice
			: _config.GetStringOr("tts_voice", "") is {Length: > 0} saved ? saved : null,
		Speed = options?.Speed is > 0 ? options.Speed : ReadDoubleConfig("tts_speed", 1.0),
		EmotionText = options?.EmotionText,
	};

	private string ResolveProviderEndpoint(string providerName)
	{
		string endpoint = providerName.ToLowerInvariant() switch
		{
			"gpt_sovits" => _config.GetStringOr("gptsovits_base_url", "http://127.0.0.1:9880"),
			"minimax" => _config.GetStringOr("tts_base_url", "") is {Length: > 0} minimaxUrl
				? minimaxUrl
				: "https://api.minimaxi.com/v1",
			"indextts" => _config.GetStringOr("tts_base_url", "") is {Length: > 0} indexttsUrl
				? indexttsUrl
				: "https://api.modelverse.cn/v1",
			_ => _config.GetStringOr("tts_base_url", "https://api.openai.com/v1"),
		};
		return $"{providerName}:{endpoint.Trim().TrimEnd('/') }";
	}

	/// <summary>按名称构造 TTS 提供商。</summary>
	public ITtsProvider CreateProvider(string name) => name switch
	{
		"gemini" => new GeminiTtsProvider(_httpClient, _config),
		"minimax" => new MiniMaxTtsProvider(_httpClient, _config),
		"indextts" => new IndexTtsProvider(_httpClient, _config, _paths),
		"gpt_sovits" => new GptSoVitsTtsProvider(_httpClient, _config),
		"custom" => new CustomHttpTtsProvider(_httpClient, _config),
		"openai" => new OpenAiTtsProvider(_httpClient, _config),
		_ => throw new InvalidOperationException("不支持当前 TTS 服务商，请在语音设置中重新选择"),
	};

	// ---- 录音识别 ----

	/// <summary>原生录音器的实际采集状态，不把转写中的网络请求视为录音。</summary>
	public bool IsRecording => _recorderFactory()?.IsRecording == true;

	/// <summary>开始录音；前端权限失败会由 recorder 立即报告。</summary>
	public async Task StartListeningAsync(CancellationToken cancellationToken = default)
	{
		IMicrophoneRecorder recorder = _recorderFactory() ?? throw new InvalidOperationException("麦克风录音后端不可用");
		await recorder.StartAsync(cancellationToken);
	}

	/// <summary>结束录音并经 Whisper 识别返回文本。</summary>
	public async Task<string> StopListeningAndTranscribeAsync(CancellationToken cancellationToken = default)
	{
		IMicrophoneRecorder? recorder = _recorderFactory() ?? throw new InvalidOperationException("麦克风录音后端不可用");
		if (!recorder.IsRecording) return "";
		RecordedAudio audio = await recorder.StopAsync(cancellationToken);
		if (audio.Bytes.Length == 0) return "";
		return await new WhisperSttProvider(_httpClient, _config).TranscribeAsync(audio, cancellationToken);
	}

	/// <summary>读取数值配置，非法时回退。</summary>
	private double ReadDoubleConfig(string key, double fallback) =>
		double.TryParse(
			_config.GetStringOr(key, ""),
			System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture,
			out double value) && value > 0 ? value : fallback;

	private void ThrowIfDisposed()
	{
		if (_disposed) throw new ObjectDisposedException(nameof(VoiceService));
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		Stop();
		_playback?.Dispose();
		_queue.Dispose();
		SynthesisCache.Clear();
	}
}
