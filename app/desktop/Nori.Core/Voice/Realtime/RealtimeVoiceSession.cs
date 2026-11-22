using Nori.Core.Voice.Audio;

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
	bool VoiceBargeInSupported);

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
	private readonly Func<IAudioCaptureDevice>? _openDevice;
	private readonly Func<RecordedAudio, CancellationToken, Task<string>>? _transcribe;
	private readonly Action _stopSpeech;
	private readonly CancellationToken _lifetime;
	private readonly EnergyVoiceActivity _activity = new();
	private readonly bool _supportsStreaming;

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
		CancellationToken lifetime = default)
	{
		_settings = settings ?? throw new ArgumentNullException(nameof(settings));
		_safeMode = safeMode;
		_ownership = ownership;
		_openDevice = openDevice;
		_transcribe = transcribe;
		_stopSpeech = stopSpeech ?? throw new ArgumentNullException(nameof(stopSpeech));
		_supportsStreaming = supportsStreaming;
		_lifetime = lifetime;
		_tuning = settings();
	}

	/// <summary>阶段变化。音频帧本身不会触发。</summary>
	public event Action<RealtimeVoicePhase>? PhaseChanged;

	/// <summary>只有识别器真实支持流式时才会发出临时文本。</summary>
	public event Action<long, string>? PartialTranscript;

	/// <summary>当前识别器是否会给出临时结果。Whisper 整段接口为 false。</summary>
	public bool TranscriberSupportsStreaming => _supportsStreaming;

	/// <summary>这一版没有回声消除，朗读期间不能靠语音打断。</summary>
	public bool VoiceBargeInSupported => false;

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
				_supportsStreaming,
				VoiceBargeInSupported);
		}
	}

	/// <summary>换上新选项。总开关关闭时，正在进行的监听会停掉。</summary>
	public Task ApplySettingsAsync(RealtimeVoiceSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		bool stop;
		lock (_gate)
		{
			_tuning = settings;
			stop = !settings.Enabled && _phase is not (RealtimeVoicePhase.Idle or RealtimeVoicePhase.Error);
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
			_capturePaused = _externalPlayback;
			_pushHeld = false;
			_activity.ResetAll();
			ClearAudioLocked();
			listen = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
			ReplaceListenLocked(listen);
			SetPhaseLocked(RealtimeVoicePhase.Listening);
			openCapture = _openDevice is not null && !_externalPlayback;
			ready = openCapture ? StartLoopLocked(listen) : Task.CompletedTask;
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
			await WaitQuietlyAsync(_listenTask).ConfigureAwait(false);
			throw;
		}
	}

	/// <summary>停止监听和这一轮。已经不在朗读时不会再去停播放。</summary>
	public Task StopAsync() => StopCoreAsync(disposing: false);

	private async Task StopCoreAsync(bool disposing)
	{
		Task? loop;
		Task? turn;
		bool stopSpeech;
		IAudioCaptureDevice? device;
		CancellationTokenSource? listen;
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = disposing;
			stopSpeech = _phase is RealtimeVoicePhase.Speaking or RealtimeVoicePhase.Interrupting;
			_generation++;
			CancelTurnLocked();
			_capturePaused = _externalPlayback;
			_pushHeld = false;
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
			if (stopSpeech) _stopSpeech();
			await WaitQuietlyAsync(loop).ConfigureAwait(false);
			await WaitQuietlyAsync(turn).ConfigureAwait(false);
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
	/// 主动打断。朗读期间麦克风是停着的，这个入口仍然可用。
	/// 打断会取消这一轮并停掉播放，不会再次提交，也不会撤销已经发生的工具副作用。
	/// </summary>
	public bool Interrupt()
	{
		lock (_gate)
		{
			if (_disposed || !_tuning.InterruptEnabled) return false;
			if (_phase is not (RealtimeVoicePhase.Transcribing or RealtimeVoicePhase.Thinking or RealtimeVoicePhase.Speaking))
				return false;
			_generation++;
			CancelTurnLocked();
			ClearAudioLocked();
			SetPhaseLocked(RealtimeVoicePhase.Interrupting);
		}
		_stopSpeech();
		bool resume;
		lock (_gate)
		{
			if (_phase != RealtimeVoicePhase.Interrupting) return true;
			resume = CanResumeLocked();
			_capturePaused = _externalPlayback;
			_error = "";
			SetPhaseLocked(resume ? RealtimeVoicePhase.Listening : RealtimeVoicePhase.Idle);
			if (resume) _activity.ResetSpeech();
		}
		if (resume && !_capturePaused) RequestResume();
		PublishPhase();
		return true;
	}

	/// <summary>按住说话。只在已经监听时生效，松手后没有足够语音就不会提交。</summary>
	public void SetPushToTalk(bool held)
	{
		bool finish = false;
		lock (_gate)
		{
			if (_phase != RealtimeVoicePhase.Listening || _capturePaused) return;
			bool wasHeld = _pushHeld;
			_pushHeld = held;
			finish = wasHeld && !held && _capturing;
		}
		if (finish) CommitUtterance();
	}

	/// <summary>
	/// 外部播放开始或结束。实时会话自己的朗读阶段不用再靠它暂停。
	/// 监听中的麦克风会先放开，播放结束后如果还在听就重新打开。
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
			_capturePaused = held;
			if (held)
			{
				_pushHeld = false;
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
		lock (_gate)
		{
			if (!IsActiveTurnLocked(generation)) return false;
			if (_phase is not (RealtimeVoicePhase.Thinking or RealtimeVoicePhase.Speaking)) return false;
			_capturePaused = true;
			SetPhaseLocked(RealtimeVoicePhase.Speaking);
		}
		StopDevice(generation, RealtimeVoicePhase.Speaking);
		PublishPhase();
		return true;
	}

	/// <summary>这一轮朗读结束。连续模式会回到监听，否则回到空闲。</summary>
	public void CompleteSpeaking(long generation)
	{
		lock (_gate)
		{
			if (!IsActiveTurnLocked(generation) || _phase != RealtimeVoicePhase.Speaking) return;
			_capturePaused = _externalPlayback;
			FinishTurnLocked();
		}
		PublishPhase();
	}

	/// <summary>
	/// 喂入一帧交错样本。不在这里做识别或播放。
	/// 不是监听阶段时直接忽略，所以朗读期间的声音不会开新的一轮。
	/// </summary>
	public void Observe(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
	{
		if (interleaved.Length == 0 || channels <= 0 || sampleRate <= 0) return;
		bool commit = false;
		lock (_gate)
		{
			if (_phase != RealtimeVoicePhase.Listening || _capturePaused) return;
			EnsureFormatLocked(sampleRate, channels);
			PushRingLocked(interleaved);
			EnergyVoiceDecision decision = _activity.Push(
				interleaved,
				channels,
				sampleRate,
				_tuning.Sensitivity,
				_tuning.SilenceMilliseconds,
				_pushHeld);
			if (decision.SpeechStarted) BeginCaptureLocked();
			else if (_capturing) AppendLocked(interleaved);
			int maximum = MaximumSamplesLocked();
			bool reachedCap = _capturing && _utterance is { } utterance && utterance.Count >= maximum;
			commit = _capturing && (decision.SpeechEnded || reachedCap);
		}
		if (commit) CommitUtterance();
	}

	/// <summary>
	/// 流式识别器上报临时文本。当前识别器不支持流式时直接丢掉，不会自行把最终文本切段。
	/// </summary>
	public void ReportPartial(long generation, string? text)
	{
		if (!_supportsStreaming || string.IsNullOrWhiteSpace(text)) return;
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
		if (_tuning.InputDeviceInvalid) throw new InvalidOperationException("麦克风标识无效");
		if (_tuning.InputDeviceId.Length > 0)
			throw new InvalidOperationException("当前采集只打开系统默认麦克风，不能按设备标识切换");
	}

	private bool CanResumeLocked() =>
		_tuning.Enabled && _tuning.Continuous && !_safeMode && !_tuning.InputDeviceInvalid && _tuning.InputDeviceId.Length == 0;

	private Task StartLoopLocked(CancellationTokenSource listen)
	{
		TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_loopRunning = true;
		_listenTask = Task.Run(() => ListenAsync(listen, ready));
		return ready.Task;
	}

	private async Task ListenAsync(CancellationTokenSource listen, TaskCompletionSource ready)
	{
		if (_ownership is not null && !_ownership.TryAcquire())
		{
			FailOpen(ready, new InvalidOperationException("麦克风正被占用"));
			lock (_gate) _loopRunning = false;
			return;
		}
		bool acquired = _ownership is not null;
		IAudioCaptureDevice? device = null;
		try
		{
			device = _openDevice!();
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
				if (read <= 0 || listen.IsCancellationRequested) break;
				Observe(buffer.AsSpan(0, read), format.Channels, format.SampleRate);
				if (Phase != RealtimeVoicePhase.Listening) break;
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
				FailOpen(ready, exception);
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
			resume = _resumeWhenLoopExits && _phase == RealtimeVoicePhase.Listening && !_capturePaused;
			_resumeWhenLoopExits = false;
		}
		if (transcribe) BeginTranscribe();
		else if (resume) ResumeLoop();
	}

	private void FailOpen(TaskCompletionSource ready, Exception exception)
	{
		string message = exception is OperationCanceledException
			? "实时语音启动已取消"
			: exception is InvalidOperationException or AudioDeviceException
				? exception.Message
				: "麦克风打开失败";
		lock (_gate)
		{
			ClearAudioLocked();
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

	private void RequestResume()
	{
		lock (_gate)
		{
			if (_disposed || _phase != RealtimeVoicePhase.Listening || _openDevice is null || _capturePaused) return;
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
		_ = StartLoopLocked(listen);
	}

	private void CommitUtterance()
	{
		bool hasDevice;
		long generation;
		lock (_gate)
		{
			if (_phase != RealtimeVoicePhase.Listening || !_capturing || _utterance is not { Count: > 0 } utterance)
			{
				_capturing = false;
				_utterance = null;
				return;
			}
			_pendingSamples = utterance.ToArray();
			_utterance = null;
			_capturing = false;
			_pendingChannels = _channels;
			_pendingRate = _sampleRate;
			_activity.ResetSpeech();
			generation = ++_generation;
			ReplaceTurnLocked();
			_activeTurn = new RealtimeVoiceTurn(generation, "", _turnCts!.Token, state: RealtimeVoicePhase.Transcribing);
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
			if (TurnAccepted is { } handler && _transcribe is not null)
			{
				int channels = _pendingChannels;
				int sampleRate = _pendingRate;
				_turnTask = Task.Run(() => RunTurnAsync(turn, samples, sampleRate, channels, handler));
				return;
			}
			Array.Clear(samples);
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
		Func<RealtimeVoiceTurn, Task> handler)
	{
		long generation = turn.Generation;
		CancellationToken token = turn.CancellationToken;
		bool transcribed = false;
		try
		{
			RecordedAudio audio;
			try
			{
				token.ThrowIfCancellationRequested();
				audio = EncodeUtterance(samples, sampleRate, channels);
			}
			finally { Array.Clear(samples); }

			token.ThrowIfCancellationRequested();
			string text = await _transcribe!(audio, token).WaitAsync(token).ConfigureAwait(false);
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
					|| !turn.TryPublishFinal(text)) return;
			}
			Task accepted = handler(turn);
			await accepted.WaitAsync(token).ConfigureAwait(false);
			FinishTurn(generation, RealtimeVoicePhase.Thinking);
		}
		catch (OperationCanceledException)
		{
			// 打断或停止已经改了阶段，这里不再覆盖。
		}
		catch (Exception)
		{
			lock (_gate)
			{
				if (!IsActiveTurnLocked(generation)) return;
				ClearAudioLocked();
				_error = transcribed ? "实时语音对话失败，本轮已结束" : "语音识别连接已断开，本轮已结束";
				SetPhaseLocked(RealtimeVoicePhase.Error);
			}
			PublishPhase();
		}
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
		PcmAudio source = new() {Samples = samples, SampleRate = sampleRate, Channels = channels};
		PcmAudio prepared = NativeMicrophoneRecorder.Downmix(source, NativeMicrophoneRecorder.TargetSampleRate);
		byte[] bytes = WaveDecoder.Encode(prepared);
		Array.Clear(prepared.Samples);
		if (bytes.Length > VoiceAudioLimits.MaxBytes) throw new InvalidOperationException("录音内容超过 32MiB 限制");
		return new RecordedAudio(bytes, "audio/wav", "speech.wav");
	}

	private void BeginCaptureLocked()
	{
		_capturing = true;
		int capacity = Math.Min(MaximumSamplesLocked(), Math.Max(1, _sampleRate * _channels));
		_utterance = new List<float>(capacity);
		for (int index = 0; index < _ringCount; index++)
			_utterance.Add(_ring[(_ringStart + index) % _ring.Length]);
	}

	private void AppendLocked(ReadOnlySpan<float> samples)
	{
		List<float> utterance = _utterance ??= new List<float>();
		int maximum = MaximumSamplesLocked();
		for (int index = 0; index < samples.Length && utterance.Count < maximum; index++)
			utterance.Add(samples[index]);
	}

	private void EnsureFormatLocked(int sampleRate, int channels)
	{
		long rawCapacity = (long)sampleRate * channels * RealtimeVoiceLimits.PrerollMilliseconds / 1000L;
		int capacity = (int)Math.Clamp(rawCapacity, 1, int.MaxValue);
		if (_sampleRate == sampleRate && _channels == channels && _ring.Length == capacity) return;
		_sampleRate = sampleRate;
		_channels = channels;
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

	private int MaximumSamplesLocked() => RealtimeVoiceLimits.MaximumSamples(_sampleRate, _channels);

	private void ClearAudioLocked()
	{
		_utterance = null;
		_pendingSamples = null;
		_capturing = false;
		_ringCount = 0;
		_ringStart = 0;
		if (_ring.Length > 0) Array.Clear(_ring);
	}

	private void CancelTurnLocked()
	{
		_activeTurn = null;
		try { _turnCts?.Cancel(); }
		catch (ObjectDisposedException) { }
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
