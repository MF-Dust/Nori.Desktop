using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Nori.Core.Agent;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;
using Nori.Core.Voice;
using Nori.Core.Voice.Audio;
using Nori.Core.Voice.Metrics;
using Nori.Core.Voice.Realtime;
using Nori.Core.Voice.Stt;

namespace Nori.Core.Tests;

/// <summary>
/// 阶段 E 语音对话全链路集成测试：
/// 覆盖 6 阶段打断、连续十轮闭环、快速启停并发、Agent 防抢占与无 AEC 安全护栏。
/// </summary>
public sealed class VoiceConversationIntegrationTests : IDisposable
{
	private const int SampleRate = 16000;
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

	private readonly TempDatabase _tempDb = new("nori-voice-conv-int");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public VoiceConversationIntegrationTests()
	{
		_database = NoriDatabase.Open(_tempDb.Path);
		_config = new ConfigStore(_database);
		_config.InitDefaults("test");
		_config.Set("tts_provider", new ConfigValue.Text("openai"));
		_config.Set("tts_base_url", new ConfigValue.Text("http://127.0.0.1:9880/v1"));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task E1_Listening未开口打断按连续模式停止或复位(bool continuous)
	{
		CaptureOwnership ownership = new();
		ControlledCapture capture = new();
		int stopSpeechCalls = 0;
		using RealtimeVoiceSession session = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = continuous, InterruptEnabled = true },
			false,
			ownership,
			() => capture,
			(_, _) => Task.FromResult(""),
			() => Interlocked.Increment(ref stopSpeechCalls),
			false);

		await session.StartAsync().WaitAsync(Deadline);
		Assert.Equal(RealtimeVoicePhase.Listening, session.Phase);
		Assert.True(ownership.IsHeld);
		long genBefore = session.Status().Generation;

		Assert.True(session.Interrupt());
		Assert.Equal(1, Volatile.Read(ref stopSpeechCalls));
		Assert.True(session.Status().Generation > genBefore);

		if (continuous)
		{
			Assert.Equal(RealtimeVoicePhase.Listening, session.Phase);
			Assert.Equal(0, session.BufferedSampleCount);
			await session.StopAsync().WaitAsync(Deadline);
		}
		else
		{
			Assert.Equal(RealtimeVoicePhase.Idle, session.Phase);
			await WaitUntilAsync(() => !ownership.IsHeld);
			Assert.True(capture.DisposeCount >= 1);
		}

		// 关闭打断开关时，Listening 阶段也不执行打断。
		using RealtimeVoiceSession disabledSession = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = continuous, InterruptEnabled = false },
			false,
			null,
			null,
			(_, _) => Task.FromResult(""),
			() => Interlocked.Increment(ref stopSpeechCalls),
			false);
		await disabledSession.StartAsync().WaitAsync(Deadline);
		Assert.False(disabledSession.Interrupt());
		Assert.Equal(RealtimeVoicePhase.Listening, disabledSession.Phase);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task E1_Listening已开口流式打断取消上传并清空字幕与缓冲(bool continuous)
	{
		CaptureOwnership ownership = new();
		ControlledCapture capture = new();
		FakeStreamingSttSession stream = new();
		FakeStreamingSttProvider streamingProvider = new([stream]);
		List<RealtimeTranscriptSnapshot?> snapshots = [];

		using RealtimeVoiceSession session = new(
			() => RealtimeVoiceSettings.Defaults with
			{
				Enabled = true,
				Continuous = continuous,
				InterruptEnabled = true,
				SttMode = SttMode.RealtimeCloud,
			},
			false,
			ownership,
			() => capture,
			streamingProvider,
			null,
			() => { })
		{
			TurnAccepted = _ => Task.CompletedTask,
		};
		session.TranscriptChanged += s => { lock (snapshots) snapshots.Add(s); };

		await session.StartAsync().WaitAsync(Deadline);
		await capture.PushSpeechAsync(300);
		await stream.FirstAudioReceived.Task.WaitAsync(Deadline);
		stream.Emit(StreamingSttEventKind.Delta, "item-live", "说到一半的句子");
		await WaitUntilAsync(() => session.ActivePartialTranscript is not null);

		long genBefore = session.Status().Generation;
		Assert.True(session.Interrupt());
		await stream.Disposed.Task.WaitAsync(Deadline);

		Assert.True(session.Status().Generation > genBefore);
		Assert.Equal(0, session.BufferedSampleCount);
		Assert.Null(session.ActivePartialTranscript);
		Assert.Equal(0, stream.Commits);

		if (continuous)
		{
			Assert.Equal(RealtimeVoicePhase.Listening, session.Phase);
			await session.StopAsync().WaitAsync(Deadline);
		}
		else
		{
			Assert.Equal(RealtimeVoicePhase.Idle, session.Phase);
			await WaitUntilAsync(() => !ownership.IsHeld);
		}
	}

	[Fact]
	public async Task RealtimeVoiceSession_流式转写与首段播放采样且只计一次()
	{
		VoiceMetricsTracker metrics = new();
		FakeStreamingSttSession stream = new();
		SttProviderRegistry registry = new();
		registry.Register(new FakeStreamingSttProvider([stream]));
		using NativeAudioPlayback playback = new(() => new ImmediateAudioDevice(), (_, _) => Tone(frames: 4410));
		using HttpClient http = new(new ImmediateWavHandler());
		using VoiceService voice = new(http, _config, playback, () => null, metrics: metrics);
		TaskCompletionSource accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using RealtimeVoiceSession session = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, SttMode = SttMode.RealtimeCloud },
			false, null, null, null, null, voice.Stop, metrics: metrics, sttRegistry: registry);
		session.TurnAccepted = async turn =>
		{
			Assert.Equal("私密转写", turn.Text);
			await voice.SpeakAsync("这是实际播放的首段。", cancellationToken: turn.CancellationToken);
			accepted.TrySetResult();
		};

		await session.StartAsync().WaitAsync(Deadline);
		Feed(session, 300, 0.4f);
		await stream.FirstAudioReceived.Task.WaitAsync(Deadline);
		stream.Emit(StreamingSttEventKind.Delta, "item", "私密");
		stream.Emit(StreamingSttEventKind.Delta, "item", "转写");
		Feed(session, 850, 0);
		await stream.CommitCalled.Task.WaitAsync(Deadline);
		stream.Emit(StreamingSttEventKind.Completed, "item", "私密转写");
		await accepted.Task.WaitAsync(Deadline);

		VoiceMetricsSnapshot snapshot = metrics.CaptureSnapshot();
		Assert.Equal(1, snapshot.FirstDeltaLatencyMs.Count);
		Assert.Equal(1, snapshot.FinalTranscriptLatencyMs.Count);
		Assert.Equal(1, snapshot.TtsFirstSegmentLatencyMs.Count);
		Assert.True(snapshot.FirstDeltaLatencyMs.P50 > 0);
		Assert.True(snapshot.FinalTranscriptLatencyMs.P50 > 0);
		Assert.True(snapshot.TtsFirstSegmentLatencyMs.P50 > 0);
		Assert.True(snapshot.PeakAudioBufferBytes > 0);
	}

	[Fact]
	public async Task RealtimeVoiceSession_连续监听重开失败计入指标()
	{
		VoiceMetricsTracker metrics = new();
		ControlledCapture firstCapture = new();
		int attempts = 0;
		using RealtimeVoiceSession session = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = true },
			false, new CaptureOwnership(), null, (_, _) => Task.FromResult("转写"), () => { }, false,
			openDeviceById: _ => Interlocked.Increment(ref attempts) == 1 ? firstCapture : throw new AudioDeviceException("无法打开麦克风"),
			metrics: metrics);
		session.TurnAccepted = _ => Task.CompletedTask;
		await session.StartAsync().WaitAsync(Deadline);
		Feed(session, 300, 0.4f);
		Feed(session, 850, 0);
		await WaitUntilAsync(() => session.Phase == RealtimeVoicePhase.Error);
		Assert.Equal(1, metrics.CaptureSnapshot().DeviceRecoveryFailureCount);
	}

	[Fact]
	public async Task E1_Transcribing打断递增代号丢弃迟到结果并清空临时字幕()
	{
		TaskCompletionSource transcribeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<string> releaseTranscribe = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int acceptedTurns = 0;

		using RealtimeVoiceSession session = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = false, InterruptEnabled = true },
			false,
			null,
			null,
			async (_, _) =>
			{
				transcribeStarted.TrySetResult();
				return await releaseTranscribe.Task.ConfigureAwait(false);
			},
			() => { },
			false)
		{
			TurnAccepted = _ => { Interlocked.Increment(ref acceptedTurns); return Task.CompletedTask; },
		};

		await session.StartAsync().WaitAsync(Deadline);
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0f);
		await transcribeStarted.Task.WaitAsync(Deadline);
		Assert.Equal(RealtimeVoicePhase.Transcribing, session.Phase);

		long genBefore = session.Status().Generation;
		Assert.True(session.Interrupt());
		Assert.True(session.Status().Generation > genBefore);
		Assert.Equal(RealtimeVoicePhase.Idle, session.Phase);

		releaseTranscribe.TrySetResult("迟到的转写文本");
		await Task.Delay(50);
		Assert.Equal(0, Volatile.Read(ref acceptedTurns));
		Assert.Null(session.ActivePartialTranscript);
	}

	[Fact]
	public async Task E1_Thinking打断联动取消Agent会话与待决工具审批()
	{
		using AgentSessionCoordinator agentCoordinator = new();
		VoiceConversationCoordinator conversation = new(agentCoordinator);
		TaskCompletionSource approvalWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
		bool approvalCancelled = false;
		bool agentCancelled = false;

		using RealtimeVoiceSession session = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = false, InterruptEnabled = true },
			false,
			null,
			null,
			(_, _) => Task.FromResult("请帮我修改文件"),
			() => { },
			false)
		{
			TurnAccepted = turn => conversation.ExecuteTurnAsync(turn, async activeTurn =>
			{
				using AgentSessionLease lease = agentCoordinator.Start("voice-turn-" + activeTurn.Generation, activeTurn.CancellationToken);
				TaskCompletionSource<bool> approvalTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
				using CancellationTokenRegistration reg = lease.CancellationToken.Register(() =>
				{
					approvalCancelled = true;
					approvalTcs.TrySetCanceled(lease.CancellationToken);
				});
				approvalWaiting.TrySetResult();
				try
				{
					await approvalTcs.Task.ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					agentCancelled = true;
					throw;
				}
				return "不会到达";
			}),
		};

		await session.StartAsync().WaitAsync(Deadline);
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0f);
		await approvalWaiting.Task.WaitAsync(Deadline);
		Assert.Equal(RealtimeVoicePhase.Thinking, session.Phase);
		Assert.NotNull(agentCoordinator.ActiveSessionId);

		Assert.True(session.Interrupt());
		await WaitUntilAsync(() => agentCoordinator.ActiveSessionId is null && agentCancelled);

		Assert.True(approvalCancelled);
		Assert.True(agentCancelled);
		Assert.Equal(RealtimeVoicePhase.Idle, session.Phase);
	}

	[Fact]
	public async Task E1_Speaking打断立即取消后续TTS合成并排空播放队列与复位口型()
	{
		TaskCompletionSource chunk1Playing = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseChunk1 = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int synthCalls = 0;
		List<double> levels = [];
		List<bool> speakingStates = [];

		using NativeAudioPlayback playback = new(
			() => new ControlledAudioDevice(chunk1Playing, releaseChunk1),
			(_, _) => Tone(frames: 4410));
		playback.VolumeSampled += l => { lock (levels) levels.Add(l); };

		using HttpClient http = new(new DelayedChunkHandler(() => Interlocked.Increment(ref synthCalls), chunk1Playing.Task));
		using VoiceService voice = new(http, _config, playback, () => null);
		voice.SpeakingChanged += s => { lock (speakingStates) speakingStates.Add(s); };

		TaskCompletionSource turnFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using RealtimeVoiceSession? sessionHolder = CreateSpeakingSession(voice, turnFinished, out RealtimeVoiceSession session);

		await session.StartAsync().WaitAsync(Deadline);
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0f);
		await chunk1Playing.Task.WaitAsync(Deadline);
		Assert.Equal(RealtimeVoicePhase.Speaking, session.Phase);
		Assert.True(voice.IsSpeaking);
		Assert.True(playback.IsPlaying);

		Assert.True(session.Interrupt());
		releaseChunk1.TrySetResult();
		await turnFinished.Task.WaitAsync(Deadline);

		Assert.False(voice.IsSpeaking);
		Assert.False(playback.IsPlaying);
		Assert.Equal(RealtimeVoicePhase.Idle, session.Phase);
		// 仅合成了前几句，未把 10 句全部请求完。
		Assert.True(Volatile.Read(ref synthCalls) < 10);
		lock (levels) Assert.Equal(0, levels[^1]);
		lock (speakingStates) Assert.Equal([true, false], speakingStates);
	}

	[Fact]
	public void E1_Idle打断无副作用且返回False()
	{
		int stopSpeechCalls = 0;
		using RealtimeVoiceSession session = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, InterruptEnabled = true },
			false,
			null,
			null,
			(_, _) => Task.FromResult(""),
			() => Interlocked.Increment(ref stopSpeechCalls),
			false);

		long genBefore = session.Status().Generation;
		Assert.Equal(RealtimeVoicePhase.Idle, session.Phase);
		Assert.False(session.Interrupt());
		Assert.Equal(genBefore, session.Status().Generation);
		Assert.Equal(0, Volatile.Read(ref stopSpeechCalls));
		Assert.Equal(RealtimeVoicePhase.Idle, session.Phase);
	}

	[Fact]
	public async Task E3_连续十轮语音对话全链路按序交付且无状态串扰()
	{
		const int totalRounds = 10;
		CaptureOwnership ownership = new();
		ControlledCapture capture = new();
		FakeStreamingSttSession[] streams = Enumerable.Range(0, totalRounds).Select(_ => new FakeStreamingSttSession()).ToArray();
		FakeStreamingSttProvider streamingProvider = new(streams);

		using AgentSessionCoordinator agentCoordinator = new();
		VoiceConversationCoordinator conversation = new(agentCoordinator);
		using NativeAudioPlayback playback = new(() => new ImmediateAudioDevice(), (_, _) => Tone(frames: 441));
		using HttpClient http = new(new ImmediateWavHandler());
		using VoiceService voice = new(http, _config, playback, () => null);

		List<(long Generation, string Text)> delivered = [];
		TaskCompletionSource allDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

		RealtimeVoiceSession? session = null;
		session = new RealtimeVoiceSession(
			() => RealtimeVoiceSettings.Defaults with
			{
				Enabled = true,
				Continuous = true,
				InterruptEnabled = true,
				SttMode = SttMode.RealtimeCloud,
			},
			false,
			ownership,
			() => capture,
			streamingProvider,
			null,
			() => voice.Stop())
		{
			TurnAccepted = turn => conversation.ExecuteTurnAsync(
				turn,
				async activeTurn =>
				{
					Assert.True(activeTurn.TrySubmitToAgent());
					Assert.False(activeTurn.TrySubmitToAgent());
					using AgentSessionLease lease = agentCoordinator.Start("turn-" + activeTurn.Generation, activeTurn.CancellationToken);
					lock (delivered) delivered.Add((activeTurn.Generation, activeTurn.Text));
					await Task.Delay(5, lease.CancellationToken).ConfigureAwait(false);
					return $"回复第{delivered.Count}轮。";
				},
				async (reply, generation, ct) =>
				{
					Assert.True(session!.TryBeginSpeaking(generation));
					try
					{
						await voice.SpeakAsync(reply, cancellationToken: ct).ConfigureAwait(false);
					}
					finally
					{
						session.CompleteSpeaking(generation);
					}
					if (generation > 0 && delivered.Count == totalRounds)
						allDone.TrySetResult();
				}),
		};

		try
		{
			await session.StartAsync().WaitAsync(Deadline);
			long previousGen = session.Status().Generation;

			for (int round = 0; round < totalRounds; round++)
			{
				await WaitUntilAsync(() => session.Phase == RealtimeVoicePhase.Listening && !session.Status().CapturePaused);
				await capture.PushSpeechAsync(300);
				await streams[round].FirstAudioReceived.Task.WaitAsync(Deadline);
				streams[round].Emit(StreamingSttEventKind.Delta, $"item-{round}", $"第{round + 1}轮");
				await capture.PushSilenceAsync(850);
				await streams[round].CommitCalled.Task.WaitAsync(Deadline);
				streams[round].Emit(StreamingSttEventKind.InputCommitted, $"item-{round}");
				streams[round].Emit(StreamingSttEventKind.Completed, $"item-{round}", $"用户第{round + 1}轮输入");

				await WaitUntilAsync(() => delivered.Count == round + 1);
				long currentGen = delivered[round].Generation;
				Assert.True(currentGen > previousGen);
				previousGen = currentGen;
			}

			await allDone.Task.WaitAsync(Deadline);
			await WaitUntilAsync(() => session.Phase == RealtimeVoicePhase.Listening && !voice.IsSpeaking && !playback.IsPlaying);

			Assert.Equal(totalRounds, delivered.Count);
			for (int round = 0; round < totalRounds; round++)
				Assert.Equal($"用户第{round + 1}轮输入", delivered[round].Text);
			Assert.Null(session.ActivePartialTranscript);
			Assert.Equal(0, session.BufferedSampleCount);

			await session.StopAsync().WaitAsync(Deadline);
			Assert.Equal(RealtimeVoicePhase.Idle, session.Phase);
			Assert.False(ownership.IsHeld);
		}
		finally
		{
			session.Dispose();
		}
	}

	[Fact]
	public async Task E3_多次快速开始与停止并发交替无死锁且释放麦克风()
	{
		CaptureOwnership ownership = new();
		int openCount = 0;
		int disposeCount = 0;
		using RealtimeVoiceSession session = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = false },
			false,
			ownership,
			() =>
			{
				Interlocked.Increment(ref openCount);
				return new DisposableCountingCapture(() => Interlocked.Increment(ref disposeCount));
			},
			(_, _) => Task.FromResult("ok"),
			() => { },
			false);

		// 10 轮连续快速 Start / Stop 交替。
		for (int i = 0; i < 10; i++)
		{
			await session.StartAsync().WaitAsync(Deadline);
			await session.StopAsync().WaitAsync(Deadline);
		}

		// 再叠加并发调用 StartAsync / StopAsync，验证不会死锁或漏释放麦克风。
		for (int i = 0; i < 10; i++)
		{
			Task start = Task.Run(() => session.StartAsync());
			Task stop = Task.Run(() => session.StopAsync());
			try { await Task.WhenAll(start, stop).WaitAsync(Deadline); }
			catch (OperationCanceledException) { }
			catch (InvalidOperationException) { }
			await session.StopAsync().WaitAsync(Deadline);
		}

		Assert.Equal(RealtimeVoicePhase.Idle, session.Phase);
		Assert.False(ownership.IsHeld);
		Assert.Equal(Volatile.Read(ref openCount), Volatile.Read(ref disposeCount));
	}

	[Fact]
	public async Task E3_Agent已占用时不抢占旧会话并给出明确中文提示()
	{
		using AgentSessionCoordinator agentCoordinator = new();
		VoiceConversationCoordinator conversation = new(agentCoordinator);
		bool manualCancelled = false;

		AgentSessionLease manualLease = agentCoordinator.Start("manual-chat-1");
		using CancellationTokenRegistration manualReg = manualLease.CancellationToken.Register(() => manualCancelled = true);

		using RealtimeVoiceSession session = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = true },
			false,
			null,
			null,
			(_, _) => Task.FromResult("语音打过来的问题"),
			() => { },
			false)
		{
			TurnAccepted = turn => conversation.ExecuteTurnAsync(turn, _ =>
				throw new Xunit.Sdk.XunitException("Agent 已占用时绝不能进入新语音推理")),
		};

		await session.StartAsync().WaitAsync(Deadline);
		Feed(session, 300, 0.4f);
		Feed(session, 800, 0f);

		await WaitUntilAsync(() => session.Phase == RealtimeVoicePhase.Error);
		Assert.Contains("已有对话正在进行", session.Status().Error, StringComparison.Ordinal);
		Assert.False(manualCancelled);
		Assert.Equal("manual-chat-1", agentCoordinator.ActiveSessionId);

		// 释放手动对话后，可以重新正常开启语音轮次。
		manualLease.Dispose();
		await session.StartAsync().WaitAsync(Deadline);
		Assert.Equal(RealtimeVoicePhase.Listening, session.Phase);
	}

	[Fact]
	public async Task E4_无已验证AEC时严禁语音打断且播放期间保持采集暂停()
	{
		// 1. 默认 NullAecProcessor
		using RealtimeVoiceSession defaultSession = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = true, InterruptEnabled = true },
			false,
			null,
			null,
			(_, _) => Task.FromResult("你好"),
			() => { },
			false);
		Assert.False(defaultSession.VoiceBargeInSupported);
		Assert.False(defaultSession.Status().Aec.CanEnableVoiceBargeIn);

		// 2. 只有局部 AEC 标记但未通过 barge-in 验证时仍必须禁用
		using FakeAecProcessor unverifiedAec = new(new AecCapabilities
		{
			HasAcousticEchoCancellation = true,
			HasDoubleTalkDetection = true,
			SupportsPlaybackReferenceSync = true,
			IsVerifiedForBargeIn = false,
		});
		using RealtimeVoiceSession unverifiedSession = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = true, InterruptEnabled = true },
			false,
			null,
			null,
			(_, _) => Task.FromResult("你好"),
			() => { },
			false,
			aecProcessor: unverifiedAec);
		Assert.False(unverifiedSession.VoiceBargeInSupported);

		// 播放期间强制暂停采集，喂入再大声的音频也不触发转录或打断。
		TaskCompletionSource releaseTurn = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int stopSpeechCalls = 0;
		RealtimeVoiceSession? gatedSession = null;
		gatedSession = new RealtimeVoiceSession(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = true, InterruptEnabled = true },
			false,
			null,
			null,
			(_, _) => Task.FromResult("你好"),
			() => Interlocked.Increment(ref stopSpeechCalls),
			false,
			aecProcessor: NullAecProcessor.Instance)
		{
			TurnAccepted = async turn =>
			{
				Assert.True(gatedSession!.TryBeginSpeaking(turn.Generation));
				await releaseTurn.Task.WaitAsync(turn.CancellationToken).ConfigureAwait(false);
			},
		};
		using RealtimeVoiceSession gatedCleanup = gatedSession;
		await gatedSession.StartAsync().WaitAsync(Deadline);
		Feed(gatedSession, 300, 0.4f);
		Feed(gatedSession, 800, 0f);
		await WaitUntilAsync(() => gatedSession.Phase == RealtimeVoicePhase.Speaking);
		Assert.True(gatedSession.Status().CapturePaused);
		Feed(gatedSession, 500, 0.9f);
		Assert.Equal(RealtimeVoicePhase.Speaking, gatedSession.Phase);
		Assert.Equal(0, Volatile.Read(ref stopSpeechCalls));
		releaseTurn.TrySetResult();

		// 3. 注入已验证 AEC + 双讲检测器：回声不打断，近端双讲才触发打断。
		using FakeAecProcessor verifiedAec = new(new AecCapabilities
		{
			HasAcousticEchoCancellation = true,
			HasNoiseSuppression = true,
			HasDoubleTalkDetection = true,
			SupportsPlaybackReferenceSync = true,
			IsVerifiedForBargeIn = true,
		});
		TaskCompletionSource releaseVerifiedTurn = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int verifiedStopCalls = 0;
		RealtimeVoiceSession? verifiedSession = null;
		verifiedSession = new RealtimeVoiceSession(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = true, InterruptEnabled = true },
			false,
			null,
			null,
			(_, _) => Task.FromResult("第一句"),
			() => { Interlocked.Increment(ref verifiedStopCalls); releaseVerifiedTurn.TrySetResult(); },
			false,
			aecProcessor: verifiedAec)
		{
			TurnAccepted = async turn =>
			{
				Assert.True(verifiedSession!.TryBeginSpeaking(turn.Generation));
				await releaseVerifiedTurn.Task.WaitAsync(turn.CancellationToken).ConfigureAwait(false);
			},
		};
		try
		{
			Assert.True(verifiedSession.VoiceBargeInSupported);
			Assert.True(verifiedSession.Status().Aec.CanEnableVoiceBargeIn);
			await verifiedSession.StartAsync().WaitAsync(Deadline);
			Feed(verifiedSession, 300, 0.4f);
			Feed(verifiedSession, 800, 0f);
			await WaitUntilAsync(() => verifiedSession.Phase == RealtimeVoicePhase.Speaking);
			Assert.False(verifiedSession.Status().CapturePaused);

			// 仅扬声器回声（DoubleTalk = false）：不打断。
			verifiedAec.DoubleTalkNext = false;
			Feed(verifiedSession, 100, 0.5f);
			Assert.Equal(RealtimeVoicePhase.Speaking, verifiedSession.Phase);
			Assert.Equal(0, Volatile.Read(ref verifiedStopCalls));

			// 检测到近端人声双讲（DoubleTalk = true）：立即触发打断并回到 Listening。
			verifiedAec.DoubleTalkNext = true;
			Feed(verifiedSession, 100, 0.5f);
			Assert.Equal(1, Volatile.Read(ref verifiedStopCalls));
			Assert.Equal(RealtimeVoicePhase.Listening, verifiedSession.Phase);
		}
		finally
		{
			verifiedSession.Dispose();
		}
	}

	[Fact]
	public async Task E4_Speaking启用已验证AEC时通过真实设备恢复采集以支持语音打断()
	{
		using FakeAecProcessor verifiedAec = new(new AecCapabilities
		{
			HasAcousticEchoCancellation = true,
			HasDoubleTalkDetection = true,
			SupportsPlaybackReferenceSync = true,
			IsVerifiedForBargeIn = true,
		});
		int openedDevices = 0;
		ControlledCapture? activeCapture = null;
		TaskCompletionSource speakingCaptureReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseSpeaking = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int stopSpeechCalls = 0;

		RealtimeVoiceSession? session = null;
		session = new RealtimeVoiceSession(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = true, InterruptEnabled = true },
			false,
			null,
			() =>
			{
				ControlledCapture capture = new();
				activeCapture = capture;
				if (Interlocked.Increment(ref openedDevices) == 2)
					speakingCaptureReady.TrySetResult();
				return capture;
			},
			(_, _) => Task.FromResult("测试问题"),
			() => { Interlocked.Increment(ref stopSpeechCalls); releaseSpeaking.TrySetResult(); },
			false,
			aecProcessor: verifiedAec)
		{
			TurnAccepted = async turn =>
			{
				Assert.True(session!.TryBeginSpeaking(turn.Generation));
				await releaseSpeaking.Task.WaitAsync(turn.CancellationToken).ConfigureAwait(false);
			},
		};
		using RealtimeVoiceSession cleanup = session;

		await session.StartAsync().WaitAsync(Deadline);
		for (int i = 0; i < 6; i++) await activeCapture!.PushSpeechAsync(60);
		for (int i = 0; i < 15; i++) await activeCapture!.PushSilenceAsync(60);
		await speakingCaptureReady.Task.WaitAsync(Deadline);
		await WaitUntilAsync(() => session.Phase == RealtimeVoicePhase.Speaking && !session.Status().CapturePaused);

		// 确认 Speaking 阶段新打开的采集设备能实际推送双讲样本并打断播放。
		verifiedAec.DoubleTalkNext = true;
		await activeCapture!.PushSpeechAsync(60);
		await WaitUntilAsync(() => Volatile.Read(ref stopSpeechCalls) == 1);
		Assert.Equal(RealtimeVoicePhase.Listening, session.Phase);
		await session.StopAsync().WaitAsync(Deadline);
	}

	[Fact]
	public async Task P1_取消等待后旧回调仍未退出时第二轮不能提前闯入待真正退出后才可进入()
	{
		VoiceConversationCoordinator coordinator = new(() => false);
		TaskCompletionSource oldAgentEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseOldAgent = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using CancellationTokenSource externalCts = new();
		CancellationToken observedToken = CancellationToken.None;
		bool secondEntered = false;

		RealtimeVoiceTurn turn1 = new(1, "第一轮", CancellationToken.None);
		Task firstTurn = coordinator.ExecuteTurnAsync(
			turn1,
			async (_, ct) =>
			{
				observedToken = ct;
				oldAgentEntered.TrySetResult();
				await releaseOldAgent.Task.ConfigureAwait(false);
				return null;
			},
			cancellationToken: externalCts.Token);

		await oldAgentEntered.Task.WaitAsync(Deadline);
		Assert.True(coordinator.IsBusy);
		Assert.False(observedToken.IsCancellationRequested);

		// 取消外部等待，但旧回调故意忽略取消并保持未退出状态。
		externalCts.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstTurn.WaitAsync(Deadline));
		Assert.True(observedToken.IsCancellationRequested);
		Assert.True(coordinator.IsBusy);

		// 旧回调未退出前，第二轮必须被明确拒绝，绝不能提前闯入。
		RealtimeVoiceTurn turn2 = new(2, "第二轮", CancellationToken.None);
		await Assert.ThrowsAsync<VoiceConversationBusyException>(() =>
			coordinator.ExecuteTurnAsync(turn2, (_, _) =>
			{
				secondEntered = true;
				return Task.FromResult<string?>(null);
			}));
		Assert.False(secondEntered);
		Assert.True(coordinator.IsBusy);

		// 放行旧回调并等其真正退出后，第二轮方可正常进入。
		releaseOldAgent.TrySetResult();
		await WaitUntilAsync(() => !coordinator.IsBusy);

		await coordinator.ExecuteTurnAsync(turn2, (_, _) =>
		{
			secondEntered = true;
			return Task.FromResult<string?>(null);
		}).WaitAsync(Deadline);
		Assert.True(secondEntered);
		Assert.False(coordinator.IsBusy);
	}

	public void Dispose()
	{
		_database.Dispose();
		_tempDb.Dispose();
	}

	private RealtimeVoiceSession CreateSpeakingSession(VoiceService voice, TaskCompletionSource turnFinished, out RealtimeVoiceSession session)
	{
		RealtimeVoiceSession? local = null;
		local = new RealtimeVoiceSession(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = false, InterruptEnabled = true },
			false,
			null,
			null,
			(_, _) => Task.FromResult("请朗读十句话"),
			() => voice.Stop(),
			false)
		{
			TurnAccepted = async turn =>
			{
				Assert.True(local!.TryBeginSpeaking(turn.Generation));
				try
				{
					await voice.SpeakAsync(
						"第一句。第二句。第三句。第四句。第五句。第六句。第七句。第八句。第九句。第十句。",
						cancellationToken: turn.CancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) { }
				finally
				{
					local.CompleteSpeaking(turn.Generation);
					turnFinished.TrySetResult();
				}
			},
		};
		session = local;
		return local;
	}

	private static void Feed(RealtimeVoiceSession session, int milliseconds, float amplitude)
	{
		int frame = SampleRate * 20 / 1000;
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

	private static PcmAudio Tone(int frames, int sampleRate = 44100, float amplitude = 0.5f)
	{
		float[] samples = new float[frames];
		for (int index = 0; index < samples.Length; index++)
			samples[index] = amplitude * MathF.Sin(index * 0.05f);
		return new PcmAudio { Samples = samples, SampleRate = sampleRate, Channels = 1 };
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		using CancellationTokenSource cts = new(Deadline);
		while (!condition())
		{
			cts.Token.ThrowIfCancellationRequested();
			await Task.Delay(10, cts.Token);
		}
	}

	private sealed class FakeAecProcessor(AecCapabilities capabilities) : IAecProcessor
	{
		public bool DoubleTalkNext { get; set; }
		public AecCapabilities Capabilities { get; } = capabilities;
		public void FeedPlaybackReference(ReadOnlySpan<float> interleaved, int channels, int sampleRate) { }
		public int ProcessCapture(ReadOnlySpan<float> captured, Span<float> cleaned, int channels, int sampleRate)
		{
			int count = Math.Min(captured.Length, cleaned.Length);
			captured[..count].CopyTo(cleaned[..count]);
			return count;
		}
		public bool DetectDoubleTalk(ReadOnlySpan<float> cleaned, int channels, int sampleRate) => DoubleTalkNext;
		public void Dispose() { }
	}

	private sealed class ControlledCapture : IAudioCaptureDevice
	{
		private readonly Channel<float[]> _chunks = Channel.CreateUnbounded<float[]>();
		public int DisposeCount { get; private set; }
		public AudioFormat Open() => new(SampleRate, 1);

		public async Task PushSpeechAsync(int milliseconds)
		{
			int count = SampleRate * milliseconds / 1000;
			float[] samples = new float[count];
			for (int i = 0; i < count; i++) samples[i] = 0.4f * MathF.Sin(i * 0.17f);
			await _chunks.Writer.WriteAsync(samples);
		}

		public async Task PushSilenceAsync(int milliseconds)
		{
			int count = SampleRate * milliseconds / 1000;
			await _chunks.Writer.WriteAsync(new float[count]);
		}

		public int Read(Span<float> buffer, CancellationToken cancellationToken)
		{
			float[] next = _chunks.Reader.ReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
			int copy = Math.Min(buffer.Length, next.Length);
			next.AsSpan(0, copy).CopyTo(buffer);
			if (copy < next.Length)
				_chunks.Writer.TryWrite(next[copy..]);
			return copy;
		}

		public void Stop() { }
		public void Dispose() => DisposeCount++;
	}

	private sealed class DisposableCountingCapture(Action onDispose) : IAudioCaptureDevice
	{
		public AudioFormat Open() => new(SampleRate, 1);

		public int Read(Span<float> buffer, CancellationToken cancellationToken)
		{
			cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
			cancellationToken.ThrowIfCancellationRequested();
			return 0;
		}

		public void Stop() { }
		public void Dispose() => onDispose();
	}

	private sealed class FakeStreamingSttProvider(IReadOnlyList<FakeStreamingSttSession> sessions) : ISttProvider
	{
		private int _index;
		public string ProviderId => "fake-streaming";
		public SttMode Mode => SttMode.RealtimeCloud;
		public SttProviderCapabilities Capabilities { get; } = new()
		{
			SupportsRealtimePcm = true,
			SupportsIncrementalDelta = true,
		};

		public Task<IStreamingSttSession> OpenStreamingSessionAsync(SttRequestOptions options, CancellationToken cancellationToken)
		{
			int i = Interlocked.Increment(ref _index) - 1;
			return Task.FromResult<IStreamingSttSession>(sessions[Math.Min(i, sessions.Count - 1)]);
		}

		public Task<string> TranscribeAsync(RecordedAudio audio, SttRequestOptions options, CancellationToken cancellationToken) =>
			throw new NotSupportedException();
	}

	private sealed class FakeStreamingSttSession : IStreamingSttSession
	{
		private readonly Channel<StreamingSttEvent> _events = Channel.CreateUnbounded<StreamingSttEvent>();
		private int _commits;
		public string ProviderId => "fake-stream";
		public bool IsConnected => !Disposed.Task.IsCompleted;
		public ChannelReader<StreamingSttEvent> Events => _events.Reader;
		public TaskCompletionSource FirstAudioReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource CommitCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int Commits => Volatile.Read(ref _commits);

		public void Emit(StreamingSttEventKind kind, string? id, string? text = null) =>
			_events.Writer.TryWrite(new StreamingSttEvent { Kind = kind, ItemId = id, Text = text });

		public Task AppendAudioAsync(ReadOnlyMemory<byte> pcm16Mono24k, CancellationToken cancellationToken)
		{
			FirstAudioReceived.TrySetResult();
			return Task.CompletedTask;
		}

		public Task CommitAsync(CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _commits);
			CommitCalled.TrySetResult();
			return Task.CompletedTask;
		}

		public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		public ValueTask DisposeAsync() { Disposed.TrySetResult(); return ValueTask.CompletedTask; }
	}

	private sealed class ControlledAudioDevice(TaskCompletionSource writeEntered, TaskCompletionSource releaseWrite) : IAudioDevice
	{
		public double Volume { get; set; } = 1.0;
		public AudioFormat Open(int sampleRate, int channels) => new(sampleRate, channels);

		public int Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
		{
			writeEntered.TrySetResult();
			releaseWrite.Task.Wait(TimeSpan.FromSeconds(5), cancellationToken);
			return cancellationToken.IsCancellationRequested ? 0 : samples.Length;
		}

		public void Drain(CancellationToken cancellationToken) { }
		public void Stop() => releaseWrite.TrySetResult();
		public void Dispose() { }
	}

	private sealed class ImmediateAudioDevice : IAudioDevice
	{
		public double Volume { get; set; } = 1.0;
		public AudioFormat Open(int sampleRate, int channels) => new(sampleRate, channels);
		public int Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken) => samples.Length;
		public void Drain(CancellationToken cancellationToken) { }
		public void Stop() { }
		public void Dispose() { }
	}

	private sealed class ImmediateWavHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			ByteArrayContent content = new([1, 2, 3, 4]);
			content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
		}
	}

	private sealed class DelayedChunkHandler(Func<int> onCall, Task firstPlaying) : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			int index = onCall();
			if (index >= 2)
			{
				await firstPlaying.WaitAsync(cancellationToken).ConfigureAwait(false);
				await Task.Delay(50, cancellationToken).ConfigureAwait(false);
			}
			ByteArrayContent content = new([1, 2, 3, 4]);
			content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
		}
	}
}
