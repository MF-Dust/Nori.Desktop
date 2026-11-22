using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nori.Core.Agent;
using Nori.Core.Voice.Audio;
using Nori.Core.Voice.Metrics;
using Nori.Core.Voice.Stt;

namespace Nori.Core.Voice.Realtime;

/// <summary>外部可读的实时语音状态。不含录音、识别正文或设备原文。</summary>
public sealed record RealtimeVoiceStatus(
	RealtimeVoicePhase Phase,
	long Generation,
	string Error,
	bool CapturePaused,
	bool InterruptEnabled,
	bool Enabled,
	bool Continuous,
	bool Streaming,
	bool VoiceBargeInSupported)
{
	public bool UsedBatchFallback { get; init; }
	public string FallbackReason { get; init; } = "";
	public AecCapabilities Aec { get; init; } = AecCapabilities.None;
}

/// <summary>
/// 实时语音的一轮生命周期。
///
/// 代号在提交识别、打断和停止时递增。进行中的识别或回答只属于提交时的代号；
/// 旧代号的结果直接丢掉，不会再调用本轮处理，也不会把已执行的工具再跑一遍。
/// 朗读期间暂停麦克风，因此没有回声消除时不能靠说话打断。主动打断仍会停掉播放。
/// 没有接上对话处理时，截下的音频不会上传。
/// </summary>
public sealed class RealtimeVoiceSession : IDisposable
{
	private readonly Lock _gate = new();
	private readonly Func<RealtimeVoiceSettings> _settings;
	private readonly bool _safeMode;
	private readonly CaptureOwnership? _ownership;
	private readonly Func<string?, IAudioCaptureDevice>? _openDevice;
	private readonly bool _supportsDeviceSelection;
	private readonly VoiceMetricsTracker? _metrics;
	private long? _firstDeltaStartedAt;
	private long? _audioEndedAt;
	private readonly Func<RecordedAudio, CancellationToken, Task<string>>? _transcribe;
	private readonly Action _stopSpeech;
	private readonly CancellationToken _lifetime;
	private readonly EnergyVoiceActivity _defaultActivity = new();
	private EnergyVoiceActivity _activity;
	private readonly bool _legacySupportsStreaming;
	private readonly RealtimeSttPipeline? _stt;
	private readonly IAecProcessor _aec;

	private RealtimeStreamingUtterance? _stream;
	private ITranscriptionCoordinator? _coordinator;
	private (RealtimeTranscriptSnapshot? Snapshot, bool IsPartial)? _pendingTranscriptChange;
	private bool _publishingTranscript;
	private RealtimeVoiceSettings _turnSettings = RealtimeVoiceSettings.Defaults;
	private string _fallbackReason = "";
	private ContinuousAudioProcessor? _inputProcessor;
	private double _currentInputLevel;
	private double _publishedInputLevel;
	private long _lastInputLevelTickMs;
	private double? _pendingInputLevel;
	private bool _publishingInputLevel;
	private long _lastPeakBufferedBytes;

	private RealtimeVoiceSettings _tuning = RealtimeVoiceSettings.Defaults;
	private RealtimeVoicePhase _phase = RealtimeVoicePhase.Idle;
	private RealtimeVoicePhase _published = RealtimeVoicePhase.Idle;
	private long _generation;
	private string _error = "";
	private bool _capturePaused;
	private bool _externalPlayback;
	private bool _pushHeld;
	private bool _capturing;
	private bool _loopRunning;
	private bool _resumeWhenLoopExits;
	private bool _disposed;
	private int _channels = 1;
	private int _sampleRate = 16000;
	private float[] _ring = [];
	private int _ringStart;
	private int _ringCount;
	private List<float>? _utterance;
	private float[]? _pendingSamples;
	private int _pendingChannels;
	private int _pendingRate;
	private RealtimeVoiceTurn? _activeTurn;
	private CancellationTokenSource? _listenCts;
	private CancellationTokenSource? _turnCts;
	private Task? _listenTask;
	private Task? _turnTask;
	private IAudioCaptureDevice? _device;

	/// <summary>创建会话。设备工厂为空时只接受 <see cref="Observe"/> 喂入的样本。</summary>
	public RealtimeVoiceSession(
		Func<RealtimeVoiceSettings> settings,
		bool safeMode,
		CaptureOwnership? ownership,
		Func<IAudioCaptureDevice>? openDevice,
		Func<RecordedAudio, CancellationToken, Task<string>>? transcribe,
		Action stopSpeech,
		bool supportsStreaming,
		CancellationToken lifetime = default,
		IAecProcessor? aecProcessor = null,
		Func<string?, IAudioCaptureDevice>? openDeviceById = null,
		VoiceMetricsTracker? metrics = null)
	{
		_settings = settings ?? throw new ArgumentNullException(nameof(settings));
		_safeMode = safeMode;
		_ownership = ownership;
		_openDevice = openDeviceById ?? (openDevice is null ? null : _ => openDevice());
		_supportsDeviceSelection = openDeviceById is not null;
		_metrics = metrics;
		_transcribe = transcribe;
		_stopSpeech = stopSpeech ?? throw new ArgumentNullException(nameof(stopSpeech));
		_legacySupportsStreaming = supportsStreaming;
		_lifetime = lifetime;
		_aec = aecProcessor ?? NullAecProcessor.Instance;
		_tuning = settings();
		_activity = _defaultActivity;
	}

	/// <summary>创建使用提供商的会话；请求参数在识别时读取，不在会话中保存密钥。</summary>
	public RealtimeVoiceSession(
		Func<RealtimeVoiceSettings> settings,
		bool safeMode,
		CaptureOwnership? ownership,
		Func<IAudioCaptureDevice>? openDevice,
		ISttProvider? streamingSttProvider,
		ISttProvider? batchSttProvider,
		Action stopSpeech,
		Func<SttRequestOptions>? requestOptions = null,
		CancellationToken lifetime = default,
		IAecProcessor? aecProcessor = null,
		Func<string?, IAudioCaptureDevice>? openDeviceById = null,
		VoiceMetricsTracker? metrics = null,
		SttProviderRegistry? sttRegistry = null)
		: this(settings, safeMode, ownership, openDevice, null, stopSpeech, false, lifetime, aecProcessor, openDeviceById, metrics)
	{
		_stt = new(streamingSttProvider, batchSttProvider, requestOptions, sttRegistry, allowNetwork: !safeMode);
	}

	/// <summary>阶段变化。音频帧本身不会触发。</summary>
	public event Action<RealtimeVoicePhase>? PhaseChanged;

	/// <summary>只有识别器真实支持流式时才会发出临时文本。</summary>
	public event Action<long, string>? PartialTranscript;

	/// <summary>当前识别器是否会给出临时结果。Whisper 整段接口为 false。</summary>
	public bool TranscriberSupportsStreaming => _legacySupportsStreaming || SupportsStreaming;

	/// <summary>当前标准化输入电平（0.0–1.0），停止或空闲时归零。</summary>
	public double CurrentInputLevel
	{
		get { lock (_gate) return _currentInputLevel; }
	}

	/// <summary>输入电平变化通知（节流，停止或离开监听时立即归零）。</summary>
	public event Action<double>? InputLevelChanged;

	/// <summary>本轮或最近一轮流式队列的待发送字节峰值。</summary>
	public long LastPeakBufferedBytes
	{
		get { lock (_gate) return Math.Max(_lastPeakBufferedBytes, _stream?.PeakBufferedBytes ?? 0); }
	}

	internal int MaxStreamingBufferedBytes { get; init; } = BoundedAudioQueue.DefaultMaxBufferedBytes;

	internal EnergyVoiceActivityOptions VadOptions
	{
		get { lock (_gate) return _activity.Options; }
		init { _activity = new EnergyVoiceActivity(value); }
	}

	public bool SupportsStreaming
	{
		get { lock (_gate) return !_safeMode && _stt?.SupportsStreaming(_tuning) == true; }
	}

	/// <summary>临时字幕或清空通知；订阅者应异步转发到 UI，不阻塞回调。</summary>
	public event Action<RealtimeTranscriptSnapshot?>? TranscriptChanged;

	public RealtimeTranscriptSnapshot? ActivePartialTranscript
	{
		get { lock (_gate) return _coordinator?.Snapshot; }
	}

	public bool UsedBatchFallback => FallbackReason.Length > 0;

	public string FallbackReason
	{
		get { lock (_gate) return _fallbackReason; }
	}

	/// <summary>
	/// 只有接通经验证的声学回声消除（AEC）与双讲检测后，才允许在朗读期间靠语音打断。
	/// 无真实 AEC 时必须保持 false，并在播放期间强制暂停采集防止扬声器回声自触发。
	/// </summary>
	public bool VoiceBargeInSupported => !_safeMode && _aec.Capabilities.CanEnableVoiceBargeIn;

	/// <summary>接上对话之后才会收到最终文本。未设置时，截下的语音不会上传。</summary>
	public Func<RealtimeVoiceTurn, Task>? TurnAccepted { get; set; }

	/// <summary>当前阶段。</summary>
	public RealtimeVoicePhase Phase
	{
		get { lock (_gate) return _phase; }
	}

	/// <summary>还留在会话里的样本数。交给识别后应为 0。</summary>
	public int BufferedSampleCount
	{
		get { lock (_gate) return (_utterance?.Count ?? 0) + (_pendingSamples?.Length ?? 0); }
	}

	/// <summary>读取一份不含正文的状态。</summary>
	public RealtimeVoiceStatus Status()
	{
		lock (_gate)
		{
			return new RealtimeVoiceStatus(
				_phase,
				_generation,
				_error,
				_capturePaused,
				_tuning.InterruptEnabled,
				_tuning.Enabled,
				_tuning.Continuous,
				TranscriberSupportsStreaming,
				VoiceBargeInSupported)
			{
				UsedBatchFallback = _fallbackReason.Length > 0,
				FallbackReason = _fallbackReason,
				Aec = _aec.Capabilities,
			};
		}
	}

	/// <summary>一次读取同一轮的内存显示数据；调用方须节流，不用于持久化。</summary>
	public (RealtimeVoiceStatus Status, double InputLevel, RealtimeTranscriptSnapshot? Transcript) ReadPresentation()
	{
		lock (_gate) return (Status(), _publishedInputLevel, _coordinator?.Snapshot);
	}

	/// <summary>换上新选项。关闭总开关、切换识别模式或更换麦克风时停止当前监听。</summary>
	public Task ApplySettingsAsync(RealtimeVoiceSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		bool stop;
		lock (_gate)
		{
			stop = (!settings.Enabled || settings.SttMode != _tuning.SttMode
				|| settings.InputDeviceId != _tuning.InputDeviceId || settings.InputDeviceInvalid != _tuning.InputDeviceInvalid)
				&& _phase is not (RealtimeVoicePhase.Idle or RealtimeVoicePhase.Error);
			_tuning = settings;
		}
		return stop ? StopAsync() : Task.CompletedTask;
	}

	/// <summary>
	/// 开始监听。调用方取消只中止还没打开的启动，不会在打开成功后把监听关掉。
	/// 总开关、安全模式和无法兑现的设备标识会在打开设备之前拒绝。
	/// </summary>
	public async Task StartAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Task? draining;
		lock (_gate)
		{
			draining = _phase is RealtimeVoicePhase.Idle or RealtimeVoicePhase.Error
				&& _listenTask is { IsCompleted: false }
				? _listenTask
				: null;
		}
		if (draining is not null)
		{
			try
			{
				await draining.WaitAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
				// 上一轮收尾失败不阻止这次启动。调用方取消仍要停在启动之前。
			}
		}
		cancellationToken.ThrowIfCancellationRequested();

		Task ready;
		Task? listenTask = null;
		CancellationTokenSource listen;
		bool openCapture;
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_phase is not (RealtimeVoicePhase.Idle or RealtimeVoicePhase.Error))
				throw new InvalidOperationException("实时语音已经在进行");
			_tuning = _settings();
			RejectBeforeOpen();
			_generation++;
			CancelTurnLocked();
			_error = "";
			_fallbackReason = "";
			bool allowExternalBargeIn = _externalPlayback && VoiceBargeInSupported && _tuning.InterruptEnabled;
			_capturePaused = _externalPlayback && !allowExternalBargeIn;
			_pushHeld = false;
			_lastPeakBufferedBytes = 0;
			ResetInputLevelLocked();
			_activity.ResetAll();
			ClearAudioLocked();
			listen = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
			ReplaceListenLocked(listen);
			SetPhaseLocked(RealtimeVoicePhase.Listening);
			openCapture = _openDevice is not null && !_capturePaused;
			ready = openCapture ? StartLoopLocked(listen) : Task.CompletedTask;
			if (openCapture) listenTask = _listenTask;
		}
		PublishPhase();
		if (!openCapture) return;

		// 只把取消绑在“还没打开”这一段。命令返回后令牌会被释放，不能因此关掉已经开始的监听。
		try
		{
			await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			StopCaptured(listen);
			await WaitQuietlyAsync(listenTask).ConfigureAwait(false);
			throw;
		}
	}

	/// <summary>停止监听和这一轮。已经不在朗读时不会再去停播放。</summary>
	public Task StopAsync() => StopCoreAsync(disposing: false);

	private async Task StopCoreAsync(bool disposing)
	{
		Task? loop;
		Task? turn;
		Task? stream;
		bool stopSpeech;
		IAudioCaptureDevice? device;
		CancellationTokenSource? listen;
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = disposing;
			stopSpeech = _phase is RealtimeVoicePhase.Speaking or RealtimeVoicePhase.Interrupting;
			stream = _stream?.Completion;
			_generation++;
			CancelTurnLocked();
			_capturePaused = _externalPlayback;
			_pushHeld = false;
			ResetInputLevelLocked();
			ClearAudioLocked();
			loop = _listenTask;
			turn = _turnTask;
			device = _device;
			listen = _listenCts;
			_device = null;
			SetPhaseLocked(RealtimeVoicePhase.Idle);
			_error = "";
		}
		try
		{
			StopCaptured(listen, device);
			PublishTranscriptChanges();
			if (stopSpeech) _stopSpeech();
			await WaitQuietlyAsync(loop).ConfigureAwait(false);
			await WaitQuietlyAsync(turn).ConfigureAwait(false);
			await WaitQuietlyAsync(stream).ConfigureAwait(false);
			PublishPhase();
		}
		finally
		{
			if (disposing)
			{
				lock (_gate)
				{
					_listenCts?.Dispose();
					_listenCts = null;
					_turnCts?.Dispose();
					_turnCts = null;
				}
			}
		}
	}

	/// <summary>
	/// 主动打断，覆盖全链路各阶段（监听中/识别中/思考中/朗读中）。
	/// 空闲或策略关闭时无副作用并返回 false。
	/// 打断仅取消后续生成、识别与朗读，不会自动撤销已经执行的外部工具动作。
	/// </summary>
	public bool Interrupt()
	{
		lock (_gate)
		{
			if (_disposed || !_tuning.InterruptEnabled) return false;
			if (_phase is not (RealtimeVoicePhase.Listening
				or RealtimeVoicePhase.Transcribing
				or RealtimeVoicePhase.Thinking
				or RealtimeVoicePhase.Speaking))
			{
				return false;
			}
			_generation++;
			CancelTurnLocked();
			_pushHeld = false;
			ResetInputLevelLocked();
			ClearAudioLocked();
			_activity.ResetSpeech();
			SetPhaseLocked(RealtimeVoicePhase.Interrupting);
		}
		_stopSpeech();
		bool resume;
		IAudioCaptureDevice? device = null;
		CancellationTokenSource? listen = null;
		lock (_gate)
		{
			if (_phase != RealtimeVoicePhase.Interrupting) return true;
			resume = CanResumeLocked();
			bool allowExternalBargeIn = _externalPlayback && VoiceBargeInSupported && _tuning.InterruptEnabled;
			_capturePaused = _externalPlayback && !allowExternalBargeIn;
			_error = "";
			SetPhaseLocked(resume ? RealtimeVoicePhase.Listening : RealtimeVoicePhase.Idle);
			if (resume) _activity.ResetSpeech();
			else
			{
				device = _device;
				listen = _listenCts;
				_device = null;
			}
		}
		if (!resume) StopCaptured(listen, device);
		else RequestResume();
		PublishPhase();
		return true;
	}

	/// <summary>按住说话。只在已经监听时生效，松手后没有足够语音就不会提交。</summary>
	public void SetPushToTalk(bool held)
	{
		bool finish = false;
		long generation = 0;
		RealtimeStreamingUtterance? stream = null;
		lock (_gate)
		{
			if (_phase != RealtimeVoicePhase.Listening || _capturePaused) return;
			bool wasHeld = _pushHeld;
			_pushHeld = held;
			finish = wasHeld && !held && _capturing;
			if (finish)
			{
				generation = _generation;
				stream = _stream;
			}
		}
		if (finish) CommitUtterance(generation, stream);
	}

	/// <summary>
	/// 外部播放开始或结束。无真实已验证 AEC 时在播放期间强制暂停采集防止扬声器回声自触发；
	/// 播放结束后如果还在听就重新打开。
	/// </summary>
	public void SetPlaybackHold(bool held)
	{
		bool resume = false;
		IAudioCaptureDevice? device = null;
		CancellationTokenSource? listen = null;
		lock (_gate)
		{
			_externalPlayback = held;
			if (_phase is RealtimeVoicePhase.Speaking or RealtimeVoicePhase.Interrupting)
				return;
			bool allowBargeIn = held && VoiceBargeInSupported && _tuning.InterruptEnabled;
			if (allowBargeIn)
			{
				_capturePaused = false;
				return;
			}
			_capturePaused = held;
			if (held)
			{
				_pushHeld = false;
				if (_phase == RealtimeVoicePhase.Listening)
				{
					CancelTurnLocked();
					ClearAudioLocked();
					ResetInputLevelLocked();
					_activity.ResetSpeech();
				}
				device = _device;
				listen = _listenCts;
				_device = null;
			}
			else resume = _phase == RealtimeVoicePhase.Listening;
		}
		if (held) StopCaptured(listen, device);
		else if (resume) RequestResume();
		PublishPhase(force: true);
	}

	/// <summary>宿主准备朗读这一轮时调用。代号不符就拒绝，避免旧回答继续出声。</summary>
	public bool TryBeginSpeaking(long generation)
	{
		bool stopCapture;
		lock (_gate)
		{
			if (!IsActiveTurnLocked(generation)) return false;
			if (_phase is not (RealtimeVoicePhase.Thinking or RealtimeVoicePhase.Speaking)) return false;
			bool allowBargeIn = VoiceBargeInSupported && _tuning.InterruptEnabled;
			_capturePaused = !allowBargeIn;
			stopCapture = !allowBargeIn;
			SetPhaseLocked(RealtimeVoicePhase.Speaking);
		}
		if (stopCapture) StopDevice(generation, RealtimeVoicePhase.Speaking);
		else RequestResume();
		PublishPhase();
		return true;
	}

	/// <summary>这一轮朗读结束。连续模式会回到监听，否则回到空闲。</summary>
	public void CompleteSpeaking(long generation)
	{
		lock (_gate)
		{
			if (!IsActiveTurnLocked(generation) || _phase != RealtimeVoicePhase.Speaking) return;
			bool allowExternalBargeIn = _externalPlayback && VoiceBargeInSupported && _tuning.InterruptEnabled;
			_capturePaused = _externalPlayback && !allowExternalBargeIn;
			FinishTurnLocked();
		}
		PublishPhase();
	}

	/// <summary>
	/// 喂入一帧交错样本。不在这里做识别或播放。
	/// 无已验证 AEC 时朗读期间直接忽略；仅当存在已验证 AEC 且检测到近端双讲时才触发语音打断。
	/// </summary>
	public void Observe(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
	{
		if (channels <= 0 || sampleRate <= 0 || interleaved.IsEmpty) return;

		float[]? pooled = interleaved.Length <= 2048 ? null : ArrayPool<float>.Shared.Rent(interleaved.Length);
		Span<float> cleanedBuffer = pooled is null
			? stackalloc float[interleaved.Length]
			: pooled.AsSpan(0, interleaved.Length);
		scoped ReadOnlySpan<float> effective = interleaved;

		try
		{
			bool triggerBargeIn = false;
			bool commit = false;
			bool overflowAbort = false;
			bool transcriptChanged = false;
			long targetGeneration = 0;
			RealtimeStreamingUtterance? targetStream = null;
			lock (_gate)
			{
				if (_capturePaused) return;
				if (_phase == RealtimeVoicePhase.Speaking)
				{
					if (!VoiceBargeInSupported || !_tuning.InterruptEnabled) return;
					int cleanedLen = _aec.ProcessCapture(interleaved, cleanedBuffer, channels, sampleRate);
					if (cleanedLen > 0 && _aec.DetectDoubleTalk(cleanedBuffer[..cleanedLen], channels, sampleRate))
						triggerBargeIn = true;
				}
				else
				{
					if (_phase != RealtimeVoicePhase.Listening) return;
					if (_externalPlayback)
					{
						if (!VoiceBargeInSupported || !_tuning.InterruptEnabled) return;
						int cleanedLen = _aec.ProcessCapture(interleaved, cleanedBuffer, channels, sampleRate);
						if (cleanedLen <= 0 || !_aec.DetectDoubleTalk(cleanedBuffer[..cleanedLen], channels, sampleRate))
							return;
						effective = cleanedBuffer[..cleanedLen];
					}
					else if (_aec.Capabilities.HasAcousticEchoCancellation)
					{
						int cleanedLen = _aec.ProcessCapture(interleaved, cleanedBuffer, channels, sampleRate);
						if (cleanedLen <= 0) return;
						effective = cleanedBuffer[..cleanedLen];
					}

					EnsureFormatLocked(sampleRate, channels);
					float[] mono = _inputProcessor!.ProcessMono(effective, out double inputLevel);
					_currentInputLevel = inputLevel;
					if (TryPrepareInputLevelPublishLocked(out double levelToPublish))
						EnqueueInputLevelLocked(levelToPublish);
					if (mono.Length > 0)
					{
						PushRingLocked(mono);
						EnergyVoiceDecision decision = _activity.Push(
							mono,
							1,
							sampleRate,
							_tuning.Sensitivity,
							_tuning.SilenceMilliseconds,
							_pushHeld);
						if (decision.SpeechStarted) BeginCaptureLocked();
						else if (_capturing) AppendLocked(mono);
						int maximum = MaximumMonoSamplesLocked();
						bool reachedCap = _capturing && _utterance is { } utterance && utterance.Count >= maximum;
						targetGeneration = _generation;
						targetStream = _stream;
						if (_stream is { IsOverflowed: true } && !CanFallbackToBatchLocked())
						{
							overflowAbort = true;
						}
						else
						{
							commit = _capturing && (decision.SpeechEnded || reachedCap);
						}
					}
					transcriptChanged = _pendingTranscriptChange is not null;
				}
			}
			if (triggerBargeIn)
			{
				Interrupt();
				return;
			}
			if (transcriptChanged) PublishTranscriptChanges();
			if (overflowAbort)
			{
				HandleStreamingOverflowAbort(targetGeneration, targetStream!);
				return;
			}
			if (commit) CommitUtterance(targetGeneration, targetStream);
		}
		finally
		{
			if (pooled is not null) ArrayPool<float>.Shared.Return(pooled, clearArray: true);
		}
	}

	/// <summary>
	/// 流式识别器上报临时文本。当前识别器不支持流式时直接丢掉，不会自行把最终文本切段。
	/// </summary>
	public void ReportPartial(long generation, string? text)
	{
		if (!_legacySupportsStreaming || string.IsNullOrWhiteSpace(text)) return;
		Action<long, string>? partialHandler;
		lock (_gate)
		{
			if (!IsActiveTurnLocked(generation) || _phase != RealtimeVoicePhase.Transcribing) return;
			partialHandler = PartialTranscript;
		}
		partialHandler?.Invoke(generation, text);
	}

	/// <summary>停止并放开设备。</summary>
	public void Dispose() => Task.Run(() => StopCoreAsync(disposing: true)).GetAwaiter().GetResult();

	private void RejectBeforeOpen()
	{
		if (_safeMode) throw new InvalidOperationException("安全模式已禁止外部语音服务");
		if (!_tuning.Enabled) throw new InvalidOperationException("请先打开实时语音");
		if (_tuning.SttMode == SttMode.Local && _stt?.SupportsLocal != true)
			throw new InvalidOperationException("当前没有可用的本地语音识别服务");
		if (_tuning.InputDeviceInvalid) throw new InvalidOperationException("麦克风标识无效");
		if (_tuning.InputDeviceId.Length > 0 && !_supportsDeviceSelection)
			throw new InvalidOperationException("当前采集只打开系统默认麦克风，不能按设备标识切换");
	}

	private bool CanResumeLocked() =>
		_tuning.Enabled && _tuning.Continuous && !_safeMode && !_tuning.InputDeviceInvalid && (_supportsDeviceSelection || _tuning.InputDeviceId.Length == 0);

	private Task StartLoopLocked(CancellationTokenSource listen, bool recovering = false)
	{
		TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_loopRunning = true;
		string deviceId = _tuning.InputDeviceId;
		_listenTask = Task.Run(() => ListenAsync(listen, ready, deviceId, recovering));
		return ready.Task;
	}

	private async Task ListenAsync(CancellationTokenSource listen, TaskCompletionSource ready, string deviceId, bool recovering)
	{
		if (_ownership is not null && !_ownership.TryAcquire())
		{
			FailOpen(listen, ready, new InvalidOperationException("麦克风正被占用"), recovering);
			lock (_gate) _loopRunning = false;
			return;
		}
		bool acquired = _ownership is not null;
		IAudioCaptureDevice? device = null;
		try
		{
			listen.Token.ThrowIfCancellationRequested();
			device = _openDevice!(deviceId.Length == 0 ? null : deviceId);
			AudioFormat format = device.Open();
			lock (_gate)
			{
				listen.Token.ThrowIfCancellationRequested();
				_device = device;
			}
			ready.TrySetResult();
			float[] buffer = new float[1024 * Math.Max(1, format.Channels)];
			while (!listen.IsCancellationRequested)
			{
				int read;
				try { read = device.Read(buffer, listen.Token); }
				catch (OperationCanceledException) { break; }
				if (listen.IsCancellationRequested) break;
				if (read <= 0)
				{
					lock (_gate)
					{
						if (_stream is null || _phase != RealtimeVoicePhase.Listening || listen.IsCancellationRequested) break;
					}
					throw new AudioDeviceException("麦克风采集中断，本轮已结束");
				}
				Observe(buffer.AsSpan(0, read), format.Channels, format.SampleRate);
				bool keepListening;
				lock (_gate) keepListening = !_capturePaused && CanCaptureInPhaseLocked();
				if (!keepListening) break;
			}
		}
		catch (Exception exception)
		{
			bool pausedByPlayback;
			bool alreadyLeft;
			lock (_gate)
			{
				pausedByPlayback = exception is OperationCanceledException
					&& _externalPlayback
					&& _phase == RealtimeVoicePhase.Listening;
				alreadyLeft = _phase is RealtimeVoicePhase.Idle or RealtimeVoicePhase.Error;
			}
			if (pausedByPlayback)
			{
				// 打开设备时外面开始播放，这是暂停，不是启动失败。播完后再打开。
				ready.TrySetResult();
			}
			else if (!alreadyLeft)
			{
				FailOpen(listen, ready, exception, recovering && !ready.Task.IsCompleted);
			}
			else
			{
				ready.TrySetCanceled(listen.Token);
			}
		}
		finally
		{
			lock (_gate)
			{
				if (ReferenceEquals(_device, device)) _device = null;
			}
			try { device?.Dispose(); }
			catch { /* 设备关闭失败不挡住占用释放。 */ }
			if (acquired) _ownership?.Release();
		}
		bool transcribe;
		bool resume;
		lock (_gate)
		{
			_loopRunning = false;
			transcribe = _phase == RealtimeVoicePhase.Transcribing;
			resume = _resumeWhenLoopExits && CanCaptureInPhaseLocked() && !_capturePaused;
			_resumeWhenLoopExits = false;
		}
		if (transcribe) BeginTranscribe();
		else if (resume) ResumeLoop();
	}

	private void FailOpen(CancellationTokenSource listen, TaskCompletionSource ready, Exception exception, bool recovering)
	{
		string message = exception is OperationCanceledException
			? "实时语音启动已取消"
			: exception is InvalidOperationException or AudioDeviceException
				? exception.Message
				: "麦克风打开失败";
		lock (_gate)
		{
			if (!ReferenceEquals(_listenCts, listen) || listen.IsCancellationRequested)
			{
				ready.TrySetCanceled();
				return;
			}
			if (recovering) _metrics?.RecordDeviceRecoveryFailure();
			CancelTurnLocked();
			ClearAudioLocked();
			ResetInputLevelLocked();
			_error = message;
			SetPhaseLocked(RealtimeVoicePhase.Error);
		}
		if (!ready.TrySetException(exception is OperationCanceledException cancelled
			? cancelled
			: new InvalidOperationException(message, exception)))
		{
			PublishPhase();
		}
		else PublishPhase();
	}

	private bool CanCaptureInPhaseLocked() =>
		_phase == RealtimeVoicePhase.Listening
		|| (_phase == RealtimeVoicePhase.Speaking && VoiceBargeInSupported && _tuning.InterruptEnabled);

	private void RequestResume()
	{
		lock (_gate)
		{
			if (_disposed || !CanCaptureInPhaseLocked() || _openDevice is null || _capturePaused) return;
			if (_loopRunning)
			{
				_resumeWhenLoopExits = true;
				return;
			}
			BeginLoopLocked();
		}
	}

	private void ResumeLoop() => RequestResume();

	private void BeginLoopLocked()
	{
		CancellationTokenSource listen = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
		ReplaceListenLocked(listen);
		_ = StartLoopLocked(listen, recovering: true).ContinueWith(
			static task => _ = task.Exception, CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}

	private void CommitUtterance(long? expectedGeneration = null, RealtimeStreamingUtterance? expectedStream = null)
	{
		bool hasDevice;
		long generation;
		lock (_gate)
		{
			if (_phase != RealtimeVoicePhase.Listening || !_capturing || _utterance is not { Count: > 0 } utterance)
			{
				return;
			}
			if (expectedGeneration is not null && _generation != expectedGeneration)
			{
				return;
			}
			if (expectedStream is not null && !ReferenceEquals(_stream, expectedStream))
			{
				return;
			}
			_pendingSamples = utterance.ToArray();
			// 连续监听的下一轮不能从预卷缓冲重新上传已经提交的声音。
			ClearCaptureLocked();
			_pendingChannels = 1;
			_pendingRate = _sampleRate;
			ResetInputLevelLocked();
			_activity.ResetSpeech();
			if (_stream is null) CreateTurnLocked();
			generation = _generation;
			_audioEndedAt = Stopwatch.GetTimestamp();
			_stream?.EndAudio();
			_error = "";
			hasDevice = _openDevice is not null;
			SetPhaseLocked(RealtimeVoicePhase.Transcribing);
		}
		PublishPhase();
		// 有设备时等采集循环放开占用后再识别；没有设备时样本是直接喂进来的。
		if (hasDevice) StopDevice(generation, RealtimeVoicePhase.Transcribing);
		else BeginTranscribe(generation);
	}

	private void BeginTranscribe(long? expectedGeneration = null)
	{
		lock (_gate)
		{
			if (_pendingSamples is not { } samples || _activeTurn is not { } turn
				|| (expectedGeneration is not null && turn.Generation != expectedGeneration)
				|| !IsActiveTurnLocked(turn.Generation) || _phase != RealtimeVoicePhase.Transcribing) return;
			_pendingSamples = null;
			if (TurnAccepted is { } handler && (_transcribe is not null || _stt is not null))
			{
				int channels = _pendingChannels;
				int sampleRate = _pendingRate;
				RealtimeStreamingUtterance? stream = _stream;
				RealtimeVoiceSettings settings = _turnSettings;
				_turnTask = Task.Run(() => RunTurnAsync(turn, samples, sampleRate, channels, handler, stream, settings));
				return;
			}
			Array.Clear(samples);
			CancelTurnLocked();
			_error = "实时对话还没有接上，这段语音没有上传识别";
			SetPhaseLocked(RealtimeVoicePhase.Error);
		}
		PublishPhase();
	}

	private async Task RunTurnAsync(
		RealtimeVoiceTurn turn,
		float[] samples,
		int sampleRate,
		int channels,
		Func<RealtimeVoiceTurn, Task> handler,
		RealtimeStreamingUtterance? stream,
		RealtimeVoiceSettings settings)
	{
		long generation = turn.Generation;
		CancellationToken token = turn.CancellationToken;
		bool transcribed = false;
		bool replayForbidden = false;
		bool bufferOverflow = false;
		try
		{
			StreamingUtteranceResult? streamingResult = stream is null
				? null : await stream.Completion.WaitAsync(token).ConfigureAwait(false);
			string text;
			string? providerItemId = null;
			lock (_gate)
			{
				if (!IsActiveTurnLocked(generation)) return;
				if (stream is not null)
					ObserveBufferPeakLocked(stream);
				_stream = null;
				if (streamingResult == StreamingUtteranceResult.OverflowBeforeCommit)
				{
					if (!CanFallbackToBatchLocked())
					{
						bufferOverflow = true;
						throw new InvalidOperationException("网络发送过慢，本轮音频缓冲已满，已重置本轮");
					}
					_fallbackReason = "网络发送过慢，实时音频缓冲已满，改用整段识别";
					ClearTranscriptLocked();
				}
				else if (streamingResult == StreamingUtteranceResult.FailedBeforeCommit
					|| (stream is null && settings.SttMode == SttMode.RealtimeCloud))
				{
					_fallbackReason = stream is null ? "实时语音识别不可用，改用整段识别" : "实时语音提交前连接或发送失败，改用整段识别";
					ClearTranscriptLocked();
				}
			}
			PublishTranscriptChanges();
			if (streamingResult == StreamingUtteranceResult.FailedAfterCommit)
			{
				replayForbidden = true;
				throw new InvalidOperationException("实时语音已提交，不能重发");
			}
			if (streamingResult == StreamingUtteranceResult.Completed)
			{
				lock (_gate)
				{
					if (!IsActiveTurnLocked(generation) || _coordinator?.Snapshot is not { IsFinal: true } final) return;
					text = final.Text;
					providerItemId = final.ProviderItemId;
				}
			}
			else
			{
				token.ThrowIfCancellationRequested();
				RecordedAudio audio = EncodeUtterance(samples, sampleRate, channels);
				Array.Clear(samples);
				text = await (_stt is not null
					? _stt.TranscribeAsync(settings, audio, token)
					: _transcribe!(audio, token)).WaitAsync(token).ConfigureAwait(false);
			}
			Array.Clear(samples);
			transcribed = true;
			if (string.IsNullOrWhiteSpace(text))
			{
				FinishTurn(generation, RealtimeVoicePhase.Transcribing);
				return;
			}
			lock (_gate)
			{
				if (!IsActiveTurnLocked(generation) || _phase != RealtimeVoicePhase.Transcribing) return;
				SetPhaseLocked(RealtimeVoicePhase.Thinking);
			}
			PublishPhase();
			lock (_gate)
			{
				if (!IsActiveTurnLocked(generation) || _phase != RealtimeVoicePhase.Thinking
					|| !turn.TryPublishFinal(text, providerItemId)) return;
			}
			Task accepted = handler(turn);
			await accepted.WaitAsync(token).ConfigureAwait(false);
			FinishTurn(generation, RealtimeVoicePhase.Thinking);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			// 打断或停止已经改了阶段，这里不再覆盖。
		}
		catch (Exception exception)
		{
			bool agentBusy = exception is VoiceConversationBusyException or AgentSessionBusyException;
			lock (_gate)
			{
				if (!IsActiveTurnLocked(generation)) return;
				ClearAudioLocked();
				ClearTranscriptLocked();
				_error = bufferOverflow ? "网络发送过慢，本轮音频缓冲已满，已重置本轮"
					: replayForbidden ? "实时语音提交后识别失败，本轮已结束且未重发录音"
					: agentBusy ? "已有对话正在进行，请等待当前回复完成后再试"
					: transcribed ? "实时语音对话失败，本轮已结束" : "语音识别连接已断开，本轮已结束";
				SetPhaseLocked(RealtimeVoicePhase.Error);
			}
			PublishPhase();
		}
		finally { Array.Clear(samples); }
	}

	private void FinishTurn(long generation, RealtimeVoicePhase expected)
	{
		lock (_gate)
		{
			if (!IsActiveTurnLocked(generation) || _phase != expected) return;
			FinishTurnLocked();
		}
		PublishPhase();
	}

	private void FinishTurnLocked()
	{
		ClearTranscriptLocked();
		ResetInputLevelLocked();
		bool resume = CanResumeLocked();
		SetPhaseLocked(resume ? RealtimeVoicePhase.Listening : RealtimeVoicePhase.Idle);
		if (!resume) return;
		_activity.ResetSpeech();
		RequestResume();
	}

	private bool IsActiveTurnLocked(long generation) =>
		!_disposed && generation == _generation
		&& _activeTurn is { CancellationToken.IsCancellationRequested: false } turn && turn.Generation == generation;

	private static RecordedAudio EncodeUtterance(float[] samples, int sampleRate, int channels)
	{
		ContinuousAudioProcessor processor = new(sampleRate, channels, NativeMicrophoneRecorder.TargetSampleRate);
		AudioProcessResult head = processor.Process(samples);
		AudioProcessResult tail = processor.Flush();
		byte[] bytes;
		if (tail.MonoResampled.Length == 0)
		{
			bytes = ContinuousAudioProcessor.EncodeWaveFromMono(head.MonoResampled, NativeMicrophoneRecorder.TargetSampleRate);
		}
		else
		{
			float[] combined = new float[head.MonoResampled.Length + tail.MonoResampled.Length];
			head.MonoResampled.CopyTo(combined, 0);
			tail.MonoResampled.CopyTo(combined, head.MonoResampled.Length);
			bytes = ContinuousAudioProcessor.EncodeWaveFromMono(combined, NativeMicrophoneRecorder.TargetSampleRate);
			Array.Clear(combined);
		}
		Array.Clear(head.MonoResampled);
		Array.Clear(head.Pcm16Bytes);
		Array.Clear(tail.MonoResampled);
		Array.Clear(tail.Pcm16Bytes);
		if (bytes.Length > VoiceAudioLimits.MaxBytes) throw new InvalidOperationException("录音内容超过 32MiB 限制");
		return new RecordedAudio(bytes, "audio/wav", "speech.wav");
	}

	private void BeginCaptureLocked()
	{
		_capturing = true;
		_fallbackReason = "";
		_lastPeakBufferedBytes = 0;
		int capacity = Math.Min(MaximumMonoSamplesLocked(), Math.Max(1, _sampleRate));
		_utterance = new List<float>(capacity);
		for (int index = 0; index < _ringCount; index++)
			_utterance.Add(_ring[(_ringStart + index) % _ring.Length]);
		if (SupportsStreaming && TurnAccepted is not null)
		{
			RealtimeVoiceTurn turn = CreateTurnLocked();
			_firstDeltaStartedAt = Stopwatch.GetTimestamp();
			_coordinator = new RealtimeTranscriptionCoordinator(turn.Generation, turn.TurnId);
			_stream = _stt!.Open(_turnSettings, _sampleRate, 1,
				sttEvent => HandleStreamingEvent(turn.Generation, sttEvent), turn.CancellationToken, MaxStreamingBufferedBytes);
			_stream.Append(CollectionsMarshal.AsSpan(_utterance));
		}
	}

	private RealtimeVoiceTurn CreateTurnLocked()
	{
		long generation = ++_generation;
		ReplaceTurnLocked();
		_turnSettings = _tuning;
		_firstDeltaStartedAt = null;
		_audioEndedAt = null;
		return _activeTurn = new RealtimeVoiceTurn(generation, "", _turnCts!.Token, state: RealtimeVoicePhase.Transcribing);
	}

	private TranscriptionEventResult HandleStreamingEvent(long generation, StreamingSttEvent sttEvent)
	{
		TranscriptionEventResult result;
		lock (_gate)
		{
			if (!IsActiveTurnLocked(generation) || _coordinator is not { } coordinator)
				return TranscriptionEventResult.Ignored;
			RealtimeTranscriptSnapshot? previous = coordinator.Snapshot;
			result = coordinator.Handle(generation, sttEvent);
			if (result == TranscriptionEventResult.Partial && _firstDeltaStartedAt is { } started
				&& !string.IsNullOrWhiteSpace(sttEvent.Text))
			{
				_firstDeltaStartedAt = null;
				_metrics?.RecordFirstDeltaLatency(Stopwatch.GetElapsedTime(started));
			}
			if (result == TranscriptionEventResult.Completed && _audioEndedAt is { } ended)
				_metrics?.RecordFinalTranscriptLatency(Stopwatch.GetElapsedTime(ended));
			if (!ReferenceEquals(previous, coordinator.Snapshot))
				_pendingTranscriptChange = (coordinator.Snapshot, result == TranscriptionEventResult.Partial);
		}
		PublishTranscriptChanges();
		return result;
	}

	private void ClearTranscriptLocked()
	{
		if (_coordinator?.Snapshot is not null) _pendingTranscriptChange = (null, false);
		_coordinator = null;
	}

	private void PublishTranscriptChanges()
	{
		lock (_gate)
		{
			if (_publishingTranscript || _pendingTranscriptChange is null) return;
			_publishingTranscript = true;
		}
		try
		{
			// 只允许一个发布者；重入或并发产生的新状态由该发布者继续发送，旧清空不会越过新快照。
			while (true)
			{
				(RealtimeTranscriptSnapshot? Snapshot, bool IsPartial) change;
				lock (_gate)
				{
					if (_pendingTranscriptChange is not { } pending)
					{
						_publishingTranscript = false;
						return;
					}
					change = pending;
					_pendingTranscriptChange = null;
				}
				TranscriptChanged?.Invoke(change.Snapshot);
				if (change.IsPartial && change.Snapshot is { } partial && !string.IsNullOrWhiteSpace(partial.Text))
				{
					lock (_gate)
					{
						if (!IsActiveTurnLocked(partial.Generation) || !ReferenceEquals(_coordinator?.Snapshot, partial)) continue;
					}
					PartialTranscript?.Invoke(partial.Generation, partial.Text);
				}
			}
		}
		catch
		{
			lock (_gate) _publishingTranscript = false;
			throw;
		}
	}

	private void AppendLocked(ReadOnlySpan<float> mono)
	{
		List<float> utterance = _utterance ??= new List<float>();
		int maximum = MaximumMonoSamplesLocked();
		int added = Math.Min(maximum - utterance.Count, mono.Length);
		if (added <= 0) return;
		for (int index = 0; index < added; index++) utterance.Add(mono[index]);
		_stream?.Append(mono[..added]);
	}

	private void EnsureFormatLocked(int sampleRate, int channels)
	{
		int prerollMs = RealtimeVoiceLimits.PrerollMilliseconds
			+ _activity.MinimumSpeechMilliseconds
			+ EnergyVoiceActivity.AnalysisWindowMilliseconds;
		long prerollFrames = (long)sampleRate * prerollMs / 1000L;
		int capacity = (int)Math.Clamp(prerollFrames, 1, int.MaxValue);
		if (_sampleRate == sampleRate && _channels == channels && _ring.Length == capacity && _inputProcessor is not null) return;
		if (_stream is not null) CancelTurnLocked();
		_sampleRate = sampleRate;
		_channels = channels;
		_inputProcessor = new ContinuousAudioProcessor(sampleRate, channels, sampleRate);
		_ring = new float[capacity];
		_ringStart = 0;
		_ringCount = 0;
		_capturing = false;
		_utterance = null;
		_activity.ResetSpeech();
	}

	private void PushRingLocked(ReadOnlySpan<float> samples)
	{
		if (_ring.Length == 0) return;
		for (int index = 0; index < samples.Length; index++)
		{
			if (_ringCount == _ring.Length)
			{
				_ring[_ringStart] = samples[index];
				_ringStart = (_ringStart + 1) % _ring.Length;
			}
			else
			{
				_ring[(_ringStart + _ringCount) % _ring.Length] = samples[index];
				_ringCount++;
			}
		}
	}

	private int MaximumMonoSamplesLocked() => RealtimeVoiceLimits.MaximumSamples(_sampleRate, 1);

	private bool CanFallbackToBatchLocked() =>
		_transcribe is not null || (_stt is not null && _stt.CanTranscribeBatch(_turnSettings));

	private void HandleStreamingOverflowAbort(long expectedGeneration, RealtimeStreamingUtterance expectedStream)
	{
		IAudioCaptureDevice? device;
		CancellationTokenSource? listen;
		lock (_gate)
		{
			if (_phase != RealtimeVoicePhase.Listening
				|| _generation != expectedGeneration
				|| !ReferenceEquals(_stream, expectedStream))
			{
				return;
			}
			_generation++;
			CancelTurnLocked();
			_pushHeld = false;
			ResetInputLevelLocked();
			_activity.ResetSpeech();
			ClearAudioLocked();
			device = _device;
			listen = _listenCts;
			_device = null;
			_error = "网络发送过慢，本轮音频缓冲已满，已重置本轮";
			SetPhaseLocked(RealtimeVoicePhase.Error);
		}
		StopCaptured(listen, device);
		PublishTranscriptChanges();
		PublishPhase();
	}

	private void ResetInputLevelLocked()
	{
		_currentInputLevel = 0;
		_inputProcessor?.Reset();
		if (TryPrepareInputLevelPublishLocked(out double levelToPublish))
			EnqueueInputLevelLocked(levelToPublish);
	}

	private bool TryPrepareInputLevelPublishLocked(out double levelToPublish)
	{
		double current = _currentInputLevel;
		long now = Environment.TickCount64;
		bool becameZero = current == 0 && _publishedInputLevel > 0;
		bool becameActive = current > 0 && _publishedInputLevel == 0;
		bool intervalElapsed = now - _lastInputLevelTickMs >= 40 && Math.Abs(current - _publishedInputLevel) >= 0.01;
		if (becameZero || becameActive || intervalElapsed)
		{
			_publishedInputLevel = current;
			_lastInputLevelTickMs = now;
			levelToPublish = current;
			return true;
		}
		levelToPublish = 0;
		return false;
	}

	private void EnqueueInputLevelLocked(double level)
	{
		_pendingInputLevel = level;
		if (_publishingInputLevel) return;
		_publishingInputLevel = true;
		_ = Task.Run(DrainInputLevelLoop);
	}

	private void DrainInputLevelLoop()
	{
		while (true)
		{
			double next;
			lock (_gate)
			{
				if (_pendingInputLevel is not { } pending)
				{
					_publishingInputLevel = false;
					return;
				}
				next = pending;
				_pendingInputLevel = null;
			}
			try
			{
				InputLevelChanged?.Invoke(next);
			}
			catch
			{
				// 避免单个订阅者异常终止后台电平发布循环
			}
		}
	}

	private void PublishInputLevelIfNeeded()
	{
		lock (_gate)
		{
			if (TryPrepareInputLevelPublishLocked(out double levelToPublish))
				EnqueueInputLevelLocked(levelToPublish);
		}
	}

	private void ClearAudioLocked()
	{
		if (_pendingSamples is { } samples) Array.Clear(samples);
		_pendingSamples = null;
		ClearCaptureLocked();
	}

	private void ClearCaptureLocked()
	{
		if (_utterance is { } utterance) CollectionsMarshal.AsSpan(utterance).Clear();
		_utterance = null;
		_capturing = false;
		_ringCount = 0;
		_ringStart = 0;
		if (_ring.Length > 0) Array.Clear(_ring);
	}

	private void ObserveBufferPeakLocked(RealtimeStreamingUtterance stream)
	{
		long peak = stream.PeakBufferedBytes;
		_lastPeakBufferedBytes = Math.Max(_lastPeakBufferedBytes, peak);
		_metrics?.ObserveAudioBufferBytes(peak);
	}

	private void CancelTurnLocked()
	{
		_activeTurn = null;
		if (_stream is { } stream)
		{
			ObserveBufferPeakLocked(stream);
			stream.Abort();
			_stream = null;
		}
		try { _turnCts?.Cancel(); }
		catch (ObjectDisposedException) { }
		ClearTranscriptLocked();
	}

	private void ReplaceTurnLocked()
	{
		CancelTurnLocked();
		_turnCts?.Dispose();
		_turnCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
	}

	private void ReplaceListenLocked(CancellationTokenSource listen)
	{
		try { _listenCts?.Cancel(); }
		catch (ObjectDisposedException) { }
		_listenCts?.Dispose();
		_listenCts = listen;
	}

	private void SetPhaseLocked(RealtimeVoicePhase phase) => _phase = phase;

	private void PublishPhase(bool force = false)
	{
		PublishInputLevelIfNeeded();
		PublishTranscriptChanges();
		RealtimeVoicePhase phase;
		lock (_gate)
		{
			if (!force && _published == _phase) return;
			_published = _phase;
			phase = _phase;
		}
		PhaseChanged?.Invoke(phase);
	}

	private void StopDevice(long generation, RealtimeVoicePhase phase)
	{
		IAudioCaptureDevice? device;
		CancellationTokenSource? listen;
		lock (_gate)
		{
			if (generation != _generation || phase != _phase) return;
			device = _device;
			listen = _listenCts;
			_device = null;
		}
		StopCaptured(listen, device);
	}

	/// <summary>只停调用方已经摘下的那一路，避免停到停止之后新打开的监听。</summary>
	private static void StopCaptured(CancellationTokenSource? listen, IAudioCaptureDevice? device = null)
	{
		try { listen?.Cancel(); }
		catch (ObjectDisposedException) { }
		try { device?.Stop(); }
		catch (ObjectDisposedException) { }
	}

	private static async Task WaitQuietlyAsync(Task? task)
	{
		if (task is null) return;
		try { await task.ConfigureAwait(false); }
		catch { /* 收尾只等任务结束，原始失败留在原来的调用上。 */ }
	}
}
