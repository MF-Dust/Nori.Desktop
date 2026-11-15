using Nori.Core.Agent;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;
using Nori.Core.Voice;
using Nori.Core.Voice.Audio;
using Nori.Core.Voice.Realtime;

namespace Nori.Core.Tests;

/// <summary>实时语音的阶段、取消和本地分段。不打开真实麦克风，也不上传。</summary>
public sealed class RealtimeVoiceSessionTests
{
	private const int SampleRate = 16000;

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

	private static void Feed(RealtimeVoiceSession session, int milliseconds, float amplitude)
	{
		int frame = SampleRate / 50;
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
