using Nori.Core.Agent;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;
using Nori.Core.Voice;
using Nori.Core.Voice.Audio;
using Nori.Core.Voice.Realtime;
using Nori.Core.Voice.Stt;

namespace Nori.Core.Tests;

/// <summary>实时语音的阶段、取消和本地分段。不打开真实麦克风，也不上传。</summary>
public sealed class RealtimeVoiceSessionTests
{
	private const int SampleRate = 16000;

	[Fact]
	public void RealtimeVoiceTurn记录轮次来源且只能标记交付一次()
	{
		RealtimeVoiceTurn turn = new(42, "最终文本", CancellationToken.None, providerItemId: "item-1");
		RealtimeVoiceTurn other = new(42, turn.Text, turn.CancellationToken);
		Assert.Equal(42, turn.Generation);
		Assert.True(Guid.TryParseExact(turn.TurnId, "N", out _));
		Assert.NotEqual(turn.TurnId, other.TurnId);
		Assert.Equal("item-1", turn.ProviderItemId);
		Assert.Null(other.ProviderItemId);
		Assert.Equal(VoiceTurnOrigin.Voice, turn.Origin);
		Assert.Equal(RealtimeVoicePhase.Thinking, turn.State);
		Assert.False(turn.FinalDelivered);
		Assert.True(turn.TryMarkFinalDelivered());
		Assert.True(turn.FinalDelivered);
		Assert.False(turn.TryMarkFinalDelivered());
		Assert.False(turn.TrySubmitToAgent());
	}

	[Fact]
	public async Task 停止后不等待忽略取消的识别也不交付迟到文本()
	{
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<string> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int accepted = 0;
		using RealtimeVoiceSession session = new(
			() => new RealtimeVoiceSettings(true, true, 0.5, 700, true, "", false),
			false, null, null, (_, _) => { entered.TrySetResult(); return result.Task; }, () => { }, false);
		session.TurnAccepted = _ => { Interlocked.Increment(ref accepted); return Task.CompletedTask; };
		await session.StartAsync();
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		try { await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
		finally { result.TrySetResult("迟到的文本"); }
		Assert.Equal(RealtimeVoicePhase.Idle, session.Phase);
		Assert.Equal(0, Volatile.Read(ref accepted));
	}

	[Fact]
	public async Task Dispose在不处理回调的同步上下文中释放麦克风()
	{
		BlockingCapture capture = new();
		CaptureOwnership ownership = new();
		RealtimeVoiceSession session = new(
			() => new RealtimeVoiceSettings(true, false, 0.5, 700, true, "", false),
			false, ownership, () => capture, null, () => { }, false);
		await session.StartAsync();
		int cleaned = 0;
		session.PhaseChanged += phase =>
		{
			if (phase == RealtimeVoicePhase.Idle && SynchronizationContext.Current is null)
				Interlocked.Increment(ref cleaned);
		};
		await Task.Run(() =>
		{
			SynchronizationContext? previous = SynchronizationContext.Current;
			SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
			try { session.Dispose(); }
			finally { SynchronizationContext.SetSynchronizationContext(previous); }
		}).WaitAsync(TimeSpan.FromSeconds(3));
		Assert.True(capture.DisposedOnce);
		Assert.Equal(1, Volatile.Read(ref cleaned));
		Assert.True(ownership.TryAcquire());
		ownership.Release();
		await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync());
	}

	private sealed class NonPumpingContext : SynchronizationContext
	{
		public override void Post(SendOrPostCallback d, object? state) { }
	}

	[Fact]
	public void Whisper整段接口不是流式()
	{
		Assert.False(WhisperUtteranceTranscriber.SupportsStreaming);
	}

	[Fact]
	public void 缺省配置关闭且不会覆盖已保存的值()
	{
		using TempDatabase database = new("nori-realtime-voice");
		using NoriDatabase opened = NoriDatabase.Open(database.Path);
		ConfigStore config = new(opened);
		config.InitDefaults("test");
		RealtimeVoiceSettings defaults = RealtimeVoiceSettings.Read(config);
		Assert.False(defaults.Enabled);
		Assert.False(defaults.Continuous);
		Assert.Equal(0.5, defaults.Sensitivity);
		Assert.Equal(700, defaults.SilenceMilliseconds);
		Assert.True(defaults.InterruptEnabled);
		Assert.Equal("", defaults.InputDeviceId);

		config.Set(RealtimeVoiceSettings.EnabledKey, new ConfigValue.Boolean(true));
		config.Set(RealtimeVoiceSettings.SilenceKey, new ConfigValue.Text("900"));
		config.InitDefaults("test");
		RealtimeVoiceSettings kept = RealtimeVoiceSettings.Read(config);
		Assert.True(kept.Enabled);
		Assert.Equal(900, kept.SilenceMilliseconds);
		config.Set(RealtimeVoiceSettings.SensitivityKey, new ConfigValue.Text("NaN"));
		Assert.Equal(RealtimeVoiceLimits.DefaultSensitivity, RealtimeVoiceSettings.Read(config).Sensitivity);
		Assert.Equal(2_880_000, RealtimeVoiceLimits.MaximumSamples(48_000, 2));
		Assert.Equal(2_646_000, RealtimeVoiceLimits.MaximumSamples(44_100, 2));
		Assert.True(RealtimeVoiceSettings.IsInvalidDevice("bad\nid"));
		Assert.True(RealtimeVoiceSettings.IsInvalidDevice(new string('a', 129)));
		Assert.False(RealtimeVoiceSettings.IsInvalidDevice(" default "));
	}

	[Fact]
	public async Task 未开启安全模式和指定设备都不会打开麦克风()
	{
		int opened = 0;
		RealtimeVoiceSettings settings = RealtimeVoiceSettings.Defaults;
		using RealtimeVoiceSession session = CreateSession(() => settings, () => opened++);
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
		settings = new RealtimeVoiceSettings(true, false, 0.5, 700, true, "hw:1", false);
		await session.ApplySettingsAsync(settings);
		InvalidOperationException rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
		Assert.Contains("默认", rejected.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("hw:1", rejected.Message, StringComparison.Ordinal);
		Assert.Equal(0, opened);

		using RealtimeVoiceSession safe = CreateSession(() => new RealtimeVoiceSettings(true, false, 0.5, 700, true, "", false), () => opened++, safeMode: true);
		InvalidOperationException blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => safe.StartAsync());
		Assert.Contains("安全模式", blocked.Message, StringComparison.Ordinal);
		Assert.Equal(0, opened);
	}

	[Fact]
	public async Task 关闭总开关会停止正在进行的监听()
	{
		Harness harness = Harness.Open();
		using RealtimeVoiceSession session = harness.Session;
		await session.StartAsync();
		Assert.Equal(RealtimeVoicePhase.Listening, session.Phase);
		harness.Settings = harness.Settings with { Enabled = false };
		await session.ApplySettingsAsync(harness.Settings);
		Assert.Equal(RealtimeVoicePhase.Idle, session.Phase);
		Feed(session, 1_000, 0.4f);
		Assert.Equal(0, harness.TranscribeCalls);
	}

	[Fact]
	public async Task SwitchingToLocalModeStopsListeningBeforeAnyCloudTranscription()
	{
		Harness harness = Harness.Open();
		using RealtimeVoiceSession session = harness.Session;
		await session.StartAsync();
		Feed(session, 200, 0.4f);
		harness.Settings = harness.Settings with { SttMode = SttMode.Local };
		await session.ApplySettingsAsync(harness.Settings);
		Feed(session, 1_000, 0);
		Assert.Equal(RealtimeVoicePhase.Idle, session.Phase);
		Assert.Equal(0, session.ReadPresentation().InputLevel);
		Assert.Equal(0, harness.TranscribeCalls);
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
	}

	[Fact]
	public async Task 静音不提交低噪声不提交说话后只提交一次()
	{
		Harness harness = Harness.Open();
		using RealtimeVoiceSession session = harness.Session;
		await session.StartAsync();
		Feed(session, 3_000, 0);
		Feed(session, 3_000, 0.004f);
		Assert.Equal(0, harness.TranscribeCalls);

		Feed(session, 40, 0.4f);
		Assert.Equal(0, harness.TranscribeCalls);

		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		string text = await harness.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal("你好", text);
		RealtimeVoiceTurn turn = await harness.TurnGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.True(turn.FinalDelivered);
		Assert.False(turn.TryMarkFinalDelivered());
		Assert.True(turn.TrySubmitToAgent());
		Assert.False(turn.TrySubmitToAgent());
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		Assert.Equal(1, harness.TranscribeCalls);
		Assert.Equal(1, harness.ToolRuns);
		Assert.Equal(0, session.BufferedSampleCount);
		Assert.False(harness.SawTranscriptInError);
	}

	[Fact]
	public async Task 没有接上对话时不上传()
	{
		Harness harness = Harness.Open(withHandler: false);
		using RealtimeVoiceSession session = harness.Session;
		await session.StartAsync();
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		await WaitForPhase(session, RealtimeVoicePhase.Error);
		Assert.Equal(0, harness.TranscribeCalls);
		Assert.Equal(0, harness.ToolRuns);
		Assert.Contains("没有上传", session.Status().Error, StringComparison.Ordinal);
		Assert.Equal(0, session.BufferedSampleCount);
	}

	[Fact]
	public async Task 打断会丢掉旧结果且工具只执行一次()
	{
		Harness harness = Harness.Open(delayTranscribe: true);
		using RealtimeVoiceSession session = harness.Session;
		await session.StartAsync();
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		await harness.TranscribeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		long stale = session.Status().Generation;
		Assert.True(session.Interrupt());
		Assert.Equal(1, harness.StopSpeech);
		Assert.False(session.Interrupt());
		Assert.Equal(1, harness.StopSpeech);
		harness.ReleaseTranscribe.TrySetResult();
		Assert.Equal(0, harness.ToolRuns);
		Assert.False(session.TryBeginSpeaking(stale));

		await session.StartAsync();
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		await harness.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(1, harness.ToolRuns);
		Assert.False(session.TryBeginSpeaking(stale));
	}

	[Fact]
	public async Task 朗读中暂停检测且显式打断仍然有效()
	{
		Harness harness = Harness.Open();
		harness.Settings = harness.Settings with { Continuous = true };
		using RealtimeVoiceSession session = harness.Session;
		await session.StartAsync();
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		RealtimeVoiceTurn turn = await harness.TurnGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.True(session.TryBeginSpeaking(turn.Generation));
		Assert.Equal(RealtimeVoicePhase.Speaking, session.Phase);
		Assert.False(session.VoiceBargeInSupported);
		int calls = harness.TranscribeCalls;
		Feed(session, 1_000, 0.4f);
		Assert.Equal(calls, harness.TranscribeCalls);
		Assert.True(session.Status().CapturePaused);

		Assert.True(session.Interrupt());
		Assert.Equal(1, harness.StopSpeech);
		await harness.TurnFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(1, harness.ToolRuns);
		Assert.Equal(RealtimeVoicePhase.Listening, session.Phase);
	}

	[Fact]
	public async Task 打断开关关闭时不会停止播放()
	{
		Harness harness = Harness.Open();
		harness.Settings = harness.Settings with { InterruptEnabled = false };
		using RealtimeVoiceSession session = harness.Session;
		await session.StartAsync();
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		RealtimeVoiceTurn turn = await harness.TurnGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.True(session.TryBeginSpeaking(turn.Generation));
		Assert.False(session.Interrupt());
		Assert.Equal(0, harness.StopSpeech);
		Assert.Equal(RealtimeVoicePhase.Speaking, session.Phase);
		session.CompleteSpeaking(turn.Generation);
		harness.ReleaseTurn.TrySetResult();
	}

	[Fact]
	public async Task 连续模式在外部播放时保持监听()
	{
		Harness harness = Harness.Open();
		harness.Settings = harness.Settings with { Continuous = true };
		using RealtimeVoiceSession session = harness.Session;
		await session.StartAsync();
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		RealtimeVoiceTurn turn = await harness.TurnGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.True(session.TryBeginSpeaking(turn.Generation));
		session.SetPlaybackHold(true);
		session.CompleteSpeaking(turn.Generation);
		harness.ReleaseTurn.TrySetResult();
		await harness.TurnFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(RealtimeVoicePhase.Listening, session.Phase);
		Assert.True(session.Status().CapturePaused);
		int calls = harness.TranscribeCalls;
		Feed(session, 1_000, 0.4f);
		Assert.Equal(calls, harness.TranscribeCalls);
		session.SetPlaybackHold(false);
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		await harness.SecondTurnEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(2, harness.ToolRuns);
	}

	[Fact]
	public async Task 打开设备时开始播放不会变成启动失败()
	{
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		HoldOpenCapture capture = new(entered, release);
		using RealtimeVoiceSession session = new(
			() => new RealtimeVoiceSettings(true, true, 0.5, 700, true, "", false),
			false,
			null,
			() => capture,
			(_, _) => Task.FromResult(""),
			() => { },
			false);
		Task starting = session.StartAsync();
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		session.SetPlaybackHold(true);
		release.TrySetResult();
		await starting.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(RealtimeVoicePhase.Listening, session.Phase);
		Assert.True(session.Status().CapturePaused);
		Assert.NotEqual(RealtimeVoicePhase.Error, session.Phase);
		session.SetPlaybackHold(false);
		await session.StopAsync();
	}

	[Fact]
	public async Task 上一轮还在收尾时取消启动不会再打开麦克风()
	{
		int opened = 0;
		HoldDisposeCapture capture = new();
		using RealtimeVoiceSession session = new(
			() => new RealtimeVoiceSettings(true, false, 0.5, 700, true, "", false),
			false,
			null,
			() =>
			{
				opened++;
				return opened == 1 ? capture : new BlockingCapture();
			},
			(_, _) => Task.FromResult(""),
			() => { },
			false);
		await session.StartAsync();
		Task stopping = session.StopAsync();
		await capture.DisposeEntered.WaitAsync(TimeSpan.FromSeconds(2));
		using CancellationTokenSource cancelled = new();
		Task starting = session.StartAsync(cancelled.Token);
		await Task.Delay(30);
		cancelled.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
		Assert.Equal(1, opened);
		Assert.Equal(RealtimeVoicePhase.Idle, session.Phase);
		capture.ReleaseDispose();
		await stopping.WaitAsync(TimeSpan.FromSeconds(2));
	}

	[Fact]
	public async Task 播放尚未结束时开始监听不会打开麦克风()
	{
		Harness harness = Harness.Open();
		using RealtimeVoiceSession session = harness.Session;
		session.SetPlaybackHold(true);
		await session.StartAsync();
		Assert.Equal(RealtimeVoicePhase.Listening, session.Phase);
		Assert.True(session.Status().CapturePaused);
		Feed(session, 1_000, 0.4f);
		Assert.Equal(0, harness.TranscribeCalls);
		session.SetPlaybackHold(false);
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		await harness.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(1, harness.ToolRuns);
	}

	[Fact]
	public async Task 打断后迟到的识别结果不会覆盖新一轮()
	{
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int tools = 0;
		RealtimeVoicePhase? replaced = null;
		using RealtimeVoiceSession session = new(
			() => new RealtimeVoiceSettings(true, false, 0.5, 700, true, "", false),
			false,
			null,
			null,
			async (_, _) =>
			{
				await release.Task.ConfigureAwait(false);
				return "旧回答";
			},
			() => { },
			false);
		session.TurnAccepted = turn =>
		{
			tools++;
			replaced = session.Phase;
			return Task.CompletedTask;
		};
		await session.StartAsync();
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		await WaitForPhase(session, RealtimeVoicePhase.Transcribing);
		Assert.True(session.Interrupt());
		await session.StartAsync();
		RealtimeVoicePhase listening = session.Phase;
		release.TrySetResult();
		await Task.Delay(50);
		Assert.Equal(0, tools);
		Assert.Equal(listening, session.Phase);
		Assert.Null(replaced);
	}

	[Fact]
	public async Task 外部播放会暂停监听且文字朗读路径不被空闲会话打断()
	{
		Harness harness = Harness.Open();
		using RealtimeVoiceSession session = harness.Session;
		await session.StartAsync();
		session.SetPlaybackHold(true);
		Feed(session, 1_000, 0.4f);
		Assert.Equal(0, harness.TranscribeCalls);
		session.SetPlaybackHold(false);
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		await harness.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(1, harness.ToolRuns);

		FakePlayback playback = new();
		using TempDatabase database = new("nori-realtime-tts");
		using NoriDatabase opened = NoriDatabase.Open(database.Path);
		ConfigStore config = new(opened);
		config.InitDefaults("test");
		config.Set("tts_provider", new ConfigValue.Text("openai"));
		using HttpClient http = new(new WavHandler());
		using VoiceService voice = new(http, config, playback, () => null);
		int stops = 0;
		using RealtimeVoiceSession idle = new(
			() => RealtimeVoiceSettings.Defaults,
			false,
			null,
			null,
			null,
			() =>
			{
				stops++;
				voice.Stop();
			},
			false);
		await voice.SpeakAsync("普通文字朗读");
		Assert.Equal(0, stops);
		Assert.False(voice.IsSpeaking);
		Assert.Single(playback.Played);
	}

	[Fact]
	public async Task 识别失败会结束本轮且可以再开始()
	{
		Harness harness = Harness.Open(failTranscribe: true);
		using RealtimeVoiceSession session = harness.Session;
		await session.StartAsync();
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		await WaitForPhase(session, RealtimeVoicePhase.Error);
		Assert.Equal(0, harness.ToolRuns);
		Assert.Contains("断开", session.Status().Error, StringComparison.Ordinal);
		Assert.DoesNotContain("SECRET-PHRASE", session.Status().Error, StringComparison.Ordinal);
		await session.StartAsync();
		Assert.Equal(RealtimeVoicePhase.Listening, session.Phase);
	}

	[Fact]
	public async Task 不支持流式时不会制造临时文本()
	{
		Harness harness = Harness.Open();
		using RealtimeVoiceSession session = harness.Session;
		int partials = 0;
		session.PartialTranscript += (_, _) => partials++;
		session.ReportPartial(1, "半句");
		session.ReportPartial(1, "SECRET-PHRASE");
		Assert.Equal(0, partials);
		Assert.False(session.TranscriberSupportsStreaming);

		int streamed = 0;
		RealtimeVoiceSession? streaming = null;
		streaming = new RealtimeVoiceSession(
			() => new RealtimeVoiceSettings(true, false, 0.5, 700, true, "", false),
			false,
			null,
			null,
			(audio, _) =>
			{
				streaming!.ReportPartial(streaming.Status().Generation, "临时");
				streaming.ReportPartial(streaming.Status().Generation, audio.FileName);
				return Task.FromResult("全文不是被切成两段的临时结果");
			},
			() => { },
			true);
		streaming.TurnAccepted = _ => Task.CompletedTask;
		streaming.PartialTranscript += (_, _) => streamed++;
		await streaming.StartAsync();
		Feed(streaming, 300, 0.4f);
		Feed(streaming, 800, 0);
		try
		{
			await WaitForPhase(streaming, RealtimeVoicePhase.Idle);
			Assert.Equal(2, streamed);
		}
		finally { streaming.Dispose(); }
	}

	[Fact]
	public async Task 按住说话没有声音不提交有声音立即提交()
	{
		Harness silence = Harness.Open();
		using RealtimeVoiceSession quiet = silence.Session;
		await quiet.StartAsync();
		quiet.SetPushToTalk(true);
		Feed(quiet, 500, 0);
		quiet.SetPushToTalk(false);
		Assert.Equal(0, silence.TranscribeCalls);

		Harness spoken = Harness.Open();
		using RealtimeVoiceSession session = spoken.Session;
		await session.StartAsync();
		session.SetPushToTalk(true);
		Feed(session, 300, 0.4f);
		session.SetPushToTalk(false);
		await spoken.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(1, spoken.ToolRuns);
	}

	[Fact]
	public async Task 重复开始被拒绝且超长语音只形成一轮有界请求()
	{
		Harness harness = Harness.Open();
		using RealtimeVoiceSession session = harness.Session;
		await session.StartAsync();
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
		Feed(session, 31_000, 0.4f);
		RecordedAudio audio = await harness.AudioGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(1, harness.TranscribeCalls);
		Assert.True(audio.Bytes.Length < SampleRate * 32 * 2);
		Assert.Equal(0, session.BufferedSampleCount);
	}

	[Fact]
	public async Task 实时监听和按键录音不能同时占用麦克风()
	{
		CaptureOwnership ownership = new();
		BlockingCapture capture = new();
		using RealtimeVoiceSession session = new(
			() => new RealtimeVoiceSettings(true, false, 0.5, 700, true, "", false),
			false,
			ownership,
			() => capture,
			(_, _) => Task.FromResult("不会上传"),
			() => { },
			false);
		await session.StartAsync();
		Assert.True(ownership.IsHeld);
		using NativeMicrophoneRecorder recorder = new(() => new BlockingCapture(), ownership);
		InvalidOperationException busy = await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync());
		Assert.Contains("占用", busy.Message, StringComparison.Ordinal);
		await session.StopAsync();
		Assert.False(ownership.IsHeld);
		Assert.True(capture.DisposedOnce);
		await recorder.StartAsync();
		await recorder.StopAsync();
	}

	[Fact]
	public async Task 识别期间已经放开麦克风()
	{
		CaptureOwnership ownership = new();
		ScriptedCapture capture = new(SpeechThenSilence());
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using RealtimeVoiceSession session = new(
			() => new RealtimeVoiceSettings(true, false, 0.5, 700, true, "", false),
			false,
			ownership,
			() => capture,
			(_, _) =>
			{
				Assert.False(ownership.IsHeld);
				entered.TrySetResult();
				return Task.FromResult("");
			},
			() => { },
			false);
		session.TurnAccepted = _ => Task.CompletedTask;
		await session.StartAsync();
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.True(capture.DisposeCount >= 1);
		await session.StopAsync();
	}

	[Fact]
	public async Task 低声起音被Preroll完整保留不裁剪句首()
	{
		Harness harness = Harness.Open();
		using RealtimeVoiceSession session = harness.Session;
		await session.StartAsync();

		// 先喂入 80ms 极低声起音（0.025f，低于开口门槛，位于 300ms preroll 内），
		// 再接 260ms 正常讲话（0.4f），最后 800ms 静音结束。
		Feed(session, 80, 0.025f);
		Feed(session, 260, 0.4f);
		Feed(session, 800, 0);

		RecordedAudio audio = await harness.AudioGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
		PcmAudio decoded = WaveDecoder.Decode(audio.Bytes);
		Assert.Equal(16000, decoded.SampleRate);
		Assert.Equal(1, decoded.Channels);

		// 句首前 80ms（1280 帧）应完整保留起音能量，而不是被裁剪后直接从 0.4f 开始。
		int onsetFrames = SampleRate * 80 / 1000;
		Assert.True(decoded.Samples.Length > onsetFrames + 3200);
		float maxOnset = 0;
		for (int index = 0; index < onsetFrames; index++)
			maxOnset = Math.Max(maxOnset, Math.Abs(decoded.Samples[index]));
		Assert.InRange(maxOnset, 0.015f, 0.04f);
	}

	[Fact]
	public async Task 输入电平在讲话时大于零且停止或空闲后归零()
	{
		Harness harness = Harness.Open();
		using RealtimeVoiceSession session = harness.Session;
		List<double> levels = [];
		TaskCompletionSource sawPositive = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource sawZeroAfterPositive = new(TaskCreationOptions.RunContinuationsAsynchronously);
		bool hadPositive = false;
		session.InputLevelChanged += level =>
		{
			lock (levels)
			{
				levels.Add(level);
				if (level > 0)
				{
					hadPositive = true;
					sawPositive.TrySetResult();
				}
				else if (hadPositive && level == 0)
				{
					sawZeroAfterPositive.TrySetResult();
				}
			}
		};

		Assert.Equal(0, session.CurrentInputLevel);
		await session.StartAsync();
		Feed(session, 100, 0);
		Assert.Equal(0, session.CurrentInputLevel);

		Feed(session, 100, 0.4f);
		Assert.InRange(session.CurrentInputLevel, 0.05, 1.0);
		await sawPositive.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.InRange(session.ReadPresentation().InputLevel, 0.05, 1.0);

		await session.StopAsync();
		Assert.Equal(0, session.CurrentInputLevel);
		var stopped = session.ReadPresentation();
		Assert.Equal(0, stopped.InputLevel);
		Assert.Null(stopped.Transcript);
		await sawZeroAfterPositive.Task.WaitAsync(TimeSpan.FromSeconds(2));
		lock (levels)
		{
			Assert.Contains(levels, l => l > 0);
			Assert.Equal(0, levels[^1]);
		}
	}

	[Fact]
	public async Task 真实设备读取循环中InputLevelChanged内同步Dispose不死锁且最终归零()
	{
		ContinuousSpeechCapture capture = new();
		List<double> received = [];
		TaskCompletionSource disposedInsideCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource finalZeroPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int disposeTriggered = 0;

		RealtimeVoiceSession session = new(
			() => new RealtimeVoiceSettings(true, false, 0.5, 700, true, "", false),
			false,
			null,
			() => capture,
			(_, _) => Task.FromResult(""),
			() => { },
			false);

		session.InputLevelChanged += level =>
		{
			bool shouldDispose = false;
			lock (received)
			{
				received.Add(level);
				if (level > 0 && Interlocked.Exchange(ref disposeTriggered, 1) == 0)
					shouldDispose = true;
			}
			if (shouldDispose)
			{
				session.Dispose();
				disposedInsideCallback.TrySetResult();
			}
			else if (Volatile.Read(ref disposeTriggered) == 1 && level == 0)
			{
				finalZeroPublished.TrySetResult();
			}
		};

		await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
		await disposedInsideCallback.Task.WaitAsync(TimeSpan.FromSeconds(3));
		await finalZeroPublished.Task.WaitAsync(TimeSpan.FromSeconds(3));

		Assert.True(capture.DisposedOnce);
		Assert.Equal(0, session.CurrentInputLevel);
		lock (received)
		{
			Assert.Contains(received, l => l > 0);
			Assert.Equal(0, received[^1]);
		}
	}

	[Fact]
	public async Task 长确认窗口与大块输入下Preroll仍完整保留句首起音()
	{
		TaskCompletionSource<RecordedAudio> audioGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using RealtimeVoiceSession session = new(
			() => new RealtimeVoiceSettings(true, false, 0.5, 700, true, "", false),
			false,
			null,
			null,
			(audio, _) =>
			{
				audioGate.TrySetResult(audio);
				return Task.FromResult("ok");
			},
			() => { },
			false)
		{
			VadOptions = new EnergyVoiceActivityOptions { MinimumSpeechMilliseconds = 400 },
			TurnAccepted = _ => Task.CompletedTask,
		};

		await session.StartAsync();
		// 以 50ms 大块喂入：250ms 低声起音（位于 Preroll 300ms 内）+ 450ms 正常语音（覆盖 400ms 确认窗口）+ 800ms 静音。
		FeedChunked(session, 250, 0.025f, chunkMs: 50);
		FeedChunked(session, 450, 0.4f, chunkMs: 50);
		FeedChunked(session, 800, 0f, chunkMs: 50);

		RecordedAudio audio = await audioGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
		PcmAudio decoded = WaveDecoder.Decode(audio.Bytes);
		int onsetFrames = SampleRate * 250 / 1000;
		Assert.True(decoded.Samples.Length >= onsetFrames + (SampleRate * 400 / 1000));
		float maxOnset = 0;
		for (int index = 0; index < onsetFrames; index++)
			maxOnset = Math.Max(maxOnset, Math.Abs(decoded.Samples[index]));
		Assert.InRange(maxOnset, 0.015f, 0.04f);
	}

	[Fact]
	public async Task 短停顿不拆句且长停顿切段后两轮连续讲话互不干扰()
	{
		int calls = 0;
		TaskCompletionSource firstDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource secondDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using RealtimeVoiceSession session = new(
			() => new RealtimeVoiceSettings(true, true, 0.5, 700, true, "", false),
			false,
			null,
			null,
			(_, _) =>
			{
				int count = Interlocked.Increment(ref calls);
				return Task.FromResult(count == 1 ? "第一句" : "第二句");
			},
			() => { },
			false);
		session.TurnAccepted = turn =>
		{
			if (turn.Text == "第一句") firstDone.TrySetResult();
			else if (turn.Text == "第二句") secondDone.TrySetResult();
			return Task.CompletedTask;
		};

		await session.StartAsync();
		// 第一句中间有 300ms 短停顿（< 700ms），应合并成同一轮。
		Feed(session, 300, 0.4f);
		Feed(session, 300, 0);
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(1, Volatile.Read(ref calls));

		// 连续模式下等待回到 Listening 后开始第二句。
		await WaitForPhase(session, RealtimeVoicePhase.Listening);
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0);
		await secondDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(2, Volatile.Read(ref calls));
	}

	private static RealtimeVoiceSession CreateSession(Func<RealtimeVoiceSettings> settings, Action opened, bool safeMode = false) =>
		new(settings, safeMode, null, () =>
		{
			opened();
			return new BlockingCapture();
		}, (_, _) => Task.FromResult("文本"), () => { }, false);

	private static async Task WaitForPhase(RealtimeVoiceSession session, RealtimeVoicePhase phase)
	{
		if (session.Phase == phase) return;
		TaskCompletionSource<RealtimeVoicePhase> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnPhase(RealtimeVoicePhase next)
		{
			if (next == phase) seen.TrySetResult(next);
		}
		session.PhaseChanged += OnPhase;
		if (session.Phase == phase) seen.TrySetResult(phase);
		await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
		session.PhaseChanged -= OnPhase;
	}

	private static void Feed(RealtimeVoiceSession session, int milliseconds, float amplitude) =>
		FeedChunked(session, milliseconds, amplitude, chunkMs: 20);

	private static void FeedChunked(RealtimeVoiceSession session, int milliseconds, float amplitude, int chunkMs)
	{
		int frame = Math.Max(1, SampleRate * chunkMs / 1000);
		int total = Math.Max(frame, SampleRate * milliseconds / 1000);
		bool silent = amplitude <= 0f;
		for (int offset = 0; offset < total; offset += frame)
		{
			int count = Math.Min(frame, total - offset);
			float[] samples = new float[count];
			for (int index = 0; index < count; index++)
				samples[index] = silent ? 0f : amplitude * MathF.Sin((offset + index) * 0.17f);
			session.Observe(samples, 1, SampleRate);
		}
	}

	private static float[] SpeechThenSilence()
	{
		float[] samples = new float[SampleRate * 2];
		for (int index = 0; index < SampleRate; index++)
			samples[index] = 0.4f * MathF.Sin(index * 0.17f);
		return samples;
	}

	private sealed class Harness
	{
		public RealtimeVoiceSettings Settings { get; set; } = new(true, false, 0.5, 700, true, "", false);
		public int TranscribeCalls { get; private set; }
		public int StopSpeech { get; private set; }
		public int ToolRuns { get; private set; }
		public bool SawTranscriptInError { get; private set; }
		public TaskCompletionSource<string> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource TranscribeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseTranscribe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<RealtimeVoiceTurn> TurnGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource SecondTurnEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseTurn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource TurnFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<RecordedAudio> AudioGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public RealtimeVoiceSession Session { get; }
		private readonly bool _delay;
		private readonly bool _fail;
		private readonly AgentSessionCoordinator _coordinator = new();

		private Harness(bool delay, bool fail, bool withHandler)
		{
			_delay = delay;
			_fail = fail;
			Session = new RealtimeVoiceSession(
				() => Settings,
				false,
				null,
				null,
				TranscribeAsync,
				() => StopSpeech++,
				false);
			if (withHandler) Session.TurnAccepted = OnTurnAsync;
			Session.PhaseChanged += phase =>
			{
				if (Session.Status().Error.Contains("SECRET-PHRASE", StringComparison.Ordinal)) SawTranscriptInError = true;
				_ = phase;
			};
		}

		public static Harness Open(bool delayTranscribe = false, bool failTranscribe = false, bool withHandler = true) =>
			new(delayTranscribe, failTranscribe, withHandler);

		private async Task<string> TranscribeAsync(RecordedAudio audio, CancellationToken cancellationToken)
		{
			TranscribeCalls++;
			AudioGate.TrySetResult(audio);
			TranscribeEntered.TrySetResult();
			Assert.Equal(0, Session.BufferedSampleCount);
			if (_fail) throw new HttpRequestException("SECRET-PHRASE");
			if (_delay) await ReleaseTranscribe.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			return "你好";
		}

		private async Task OnTurnAsync(RealtimeVoiceTurn turn)
		{
			Assert.False(turn.CancellationToken.IsCancellationRequested);
			using AgentSessionLease lease = _coordinator.Start("voice-" + turn.Generation.ToString(), turn.CancellationToken);
			ToolRuns++;
			if (ToolRuns == 2) SecondTurnEntered.TrySetResult();
			TurnGate.TrySetResult(turn);
			Entered.TrySetResult(turn.Text);
			try
			{
				await ReleaseTurn.Task.WaitAsync(turn.CancellationToken).ConfigureAwait(false);
			}
			finally
			{
				TurnFinished.TrySetResult();
			}
		}
	}

	private sealed class HoldOpenCapture(TaskCompletionSource entered, TaskCompletionSource release) : IAudioCaptureDevice
	{
		public AudioFormat Open()
		{
			entered.TrySetResult();
			Assert.True(release.Task.Wait(TimeSpan.FromSeconds(5)));
			return new AudioFormat(SampleRate, 1);
		}

		public int Read(Span<float> buffer, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return 0;
		}

		public void Stop() { }

		public void Dispose() { }
	}

	private sealed class HoldDisposeCapture : IAudioCaptureDevice
	{
		private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task DisposeEntered => _entered.Task;

		public void ReleaseDispose() => _release.TrySetResult();

		public AudioFormat Open() => new(SampleRate, 1);

		public int Read(Span<float> buffer, CancellationToken cancellationToken)
		{
			if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
				throw new TimeoutException("麦克风读取没有被取消");
			cancellationToken.ThrowIfCancellationRequested();
			return 0;
		}

		public void Stop() { }

		public void Dispose()
		{
			_entered.TrySetResult();
			Assert.True(_release.Task.Wait(TimeSpan.FromSeconds(5)));
		}
	}

	private sealed class ContinuousSpeechCapture : IAudioCaptureDevice
	{
		private volatile bool _stopped;
		private int _disposed;
		public bool DisposedOnce => Volatile.Read(ref _disposed) == 1;

		public AudioFormat Open() => new(SampleRate, 1);

		public int Read(Span<float> buffer, CancellationToken cancellationToken)
		{
			if (_stopped || cancellationToken.IsCancellationRequested) return 0;
			for (int index = 0; index < buffer.Length; index++)
				buffer[index] = 0.4f * MathF.Sin(index * 0.17f);
			Thread.Sleep(5);
			return buffer.Length;
		}

		public void Stop() => _stopped = true;

		public void Dispose() => Interlocked.Increment(ref _disposed);
	}

	private sealed class BlockingCapture : IAudioCaptureDevice
	{
		private int _disposed;
		public bool DisposedOnce => _disposed == 1;

		public AudioFormat Open() => new(SampleRate, 1);

		public int Read(Span<float> buffer, CancellationToken cancellationToken)
		{
			if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
				throw new TimeoutException("麦克风读取没有被取消");
			cancellationToken.ThrowIfCancellationRequested();
			return 0;
		}

		public void Stop() { }

		public void Dispose() => Interlocked.Increment(ref _disposed);
	}

	private sealed class ScriptedCapture(float[] samples) : IAudioCaptureDevice
	{
		private int _at;
		public int DisposeCount { get; private set; }

		public AudioFormat Open() => new(SampleRate, 1);

		public int Read(Span<float> buffer, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_at >= samples.Length) return 0;
			int take = Math.Min(buffer.Length, samples.Length - _at);
			samples.AsSpan(_at, take).CopyTo(buffer);
			_at += take;
			return take;
		}

		public void Stop() { }

		public void Dispose() => DisposeCount++;
	}

	private sealed class WavHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			ByteArrayContent content = new([1, 2, 3, 4]);
			content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
			return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
		}
	}

	private sealed class FakePlayback : IAudioPlayback
	{
		public List<EncodedAudio> Played { get; } = [];
		public bool IsPlaying => false;
		public event Action<bool>? PlayingChanged;
		public event Action<double>? VolumeSampled;

		public Task PlayAsync(byte[] data, string? mime, CancellationToken cancellationToken)
		{
			Played.Add(new EncodedAudio(data, mime ?? "audio/wav"));
			PlayingChanged?.Invoke(true);
			VolumeSampled?.Invoke(0.4);
			PlayingChanged?.Invoke(false);
			VolumeSampled?.Invoke(0);
			return Task.CompletedTask;
		}

		public void Stop()
		{
			VolumeSampled?.Invoke(0);
			PlayingChanged?.Invoke(false);
		}

		public void Dispose() { }
	}
}
