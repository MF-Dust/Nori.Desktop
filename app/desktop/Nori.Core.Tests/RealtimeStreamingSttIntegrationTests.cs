using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;
using Nori.Core.Voice;
using Nori.Core.Voice.Audio;
using Nori.Core.Voice.Realtime;
using Nori.Core.Voice.Stt;

namespace Nori.Core.Tests;

public sealed class RealtimeStreamingSttIntegrationTests
{
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

	[Fact]
	public async Task RealtimeVoiceSession边采集边发送PCM并在提交前发布真实增量()
	{
		using Harness harness = new();
		await harness.StartSpeakingAsync();
		byte[] preroll = await harness.Stream.Audio.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
		Assert.Equal(10240, preroll.Length);
		Assert.Equal(8192, BinaryPrimitives.ReadInt16LittleEndian(preroll));
		harness.Stream.Emit(StreamingSttEventKind.Delta, "item", "临时");
		RealtimeTranscriptSnapshot partial = Assert.IsType<RealtimeTranscriptSnapshot>(
			await harness.Changes.Reader.ReadAsync().AsTask().WaitAsync(Deadline));
		Assert.False(partial.IsFinal);
		Assert.Equal("临时", partial.Text);
		Assert.Same(partial, harness.Session.ReadPresentation().Transcript);
		Assert.Equal((partial.Generation, "临时"), await harness.Partials.Reader.ReadAsync().AsTask().WaitAsync(Deadline));
		Assert.Equal(RealtimeVoicePhase.Listening, harness.Session.Phase);
		Assert.Equal(0, harness.Stream.Commits);

		await harness.Capture.FeedAsync(2);
		harness.Stream.Emit(StreamingSttEventKind.Delta, "item", "字幕");
		Assert.Equal((partial.Generation, "临时字幕"), await harness.Partials.Reader.ReadAsync().AsTask().WaitAsync(Deadline));
		harness.Session.SetPushToTalk(false);
		await harness.Stream.CommitCalled.Task.WaitAsync(Deadline);
		harness.Stream.Emit(StreamingSttEventKind.InputCommitted, "item");
		harness.Stream.Emit(StreamingSttEventKind.Delta, "unrelated", "不属于本轮");
		harness.Stream.Emit(StreamingSttEventKind.Completed, "item", "最终文本");
		harness.Stream.Emit(StreamingSttEventKind.Completed, "item", "重复");
		harness.Stream.Emit(StreamingSttEventKind.Delta, "item", "迟到");
		await harness.Ended.Task.WaitAsync(Deadline);
		RealtimeVoiceTurn turn = Assert.Single(harness.Turns);
		Assert.Equal("最终文本", turn.Text);
		Assert.Equal("item", turn.ProviderItemId);
		Assert.Equal(partial.TurnId, turn.TurnId);
		Assert.Equal(partial.Generation, turn.Generation);
		Assert.Equal(1, harness.Stream.Commits);
		Assert.False(harness.Partials.Reader.TryRead(out _));
		Assert.Equal("临时字幕", (await harness.Changes.Reader.ReadAsync().AsTask().WaitAsync(Deadline))?.Text);
		RealtimeTranscriptSnapshot final = Assert.IsType<RealtimeTranscriptSnapshot>(await harness.Changes.Reader.ReadAsync().AsTask().WaitAsync(Deadline));
		Assert.True(final.IsFinal);
		Assert.Equal("最终文本", final.Text);
		Assert.Null(await harness.Changes.Reader.ReadAsync().AsTask().WaitAsync(Deadline));
		Assert.Null(harness.Session.ActivePartialTranscript);
		Assert.Null(harness.Session.ReadPresentation().Transcript);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealtimeVoiceSession停止或打断后不交付旧连接事件(bool interrupt)
	{
		using Harness harness = new();
		await harness.StartSpeakingAsync();
		await harness.Stream.Audio.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
		harness.Stream.Emit(StreamingSttEventKind.Delta, "old", "旧字幕");
		await harness.Partials.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
		long generation = harness.Session.Status().Generation;
		if (interrupt) Assert.True(harness.Session.Interrupt());
		else await harness.Session.StopAsync().WaitAsync(Deadline);
		harness.Stream.Emit(StreamingSttEventKind.Delta, "old", "迟到增量");
		harness.Stream.Emit(StreamingSttEventKind.Completed, "old", "迟到最终结果");
		await harness.Stream.Disposed.Task.WaitAsync(Deadline);
		Assert.True(harness.Session.Status().Generation > generation);
		Assert.Null(harness.Session.ActivePartialTranscript);
		Assert.False(harness.Partials.Reader.TryRead(out _));
		Assert.Empty(harness.Turns);
		Assert.Equal(0, harness.Stream.Commits);
		Assert.Equal(0, harness.Batch.BatchCalls);
	}

	[Fact]
	public async Task RealtimeVoiceSession非连续流式监听打断会停止并释放麦克风()
	{
		using Harness harness = new();
		await harness.StartSpeakingAsync();
		Assert.Equal(RealtimeVoicePhase.Listening, harness.Session.Phase);
		Assert.True(harness.Ownership.IsHeld);
		Assert.True(harness.Session.Interrupt());
		Assert.Equal(RealtimeVoicePhase.Idle, harness.Session.Phase);
		Assert.True(harness.Capture.Stopped.Task.IsCompletedSuccessfully);
		using CancellationTokenSource deadline = new(Deadline);
		while (harness.Ownership.IsHeld) await Task.Delay(1, deadline.Token);
		Assert.True(harness.Ownership.TryAcquire());
		harness.Ownership.Release();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealtimeVoiceSession字幕回调可跨线程读取和打断且清空不会倒序(bool interruptFromPartial)
	{
		using Harness harness = new();
		TaskCompletionSource checkedClear = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource checkedInterrupt = new(TaskCreationOptions.RunContinuationsAsynchronously);
		harness.Session.TranscriptChanged += snapshot =>
		{
			if (snapshot is null)
			{
				CheckOffCallbackThread(checkedClear, () =>
				{
					Assert.Equal(RealtimeVoicePhase.Idle, harness.Session.Status().Phase);
					Assert.Null(harness.Session.ActivePartialTranscript);
				});
			}
			else if (!interruptFromPartial) CheckInterrupt();
		};
		if (interruptFromPartial) harness.Session.PartialTranscript += (_, _) => CheckInterrupt();

		await harness.StartSpeakingAsync();
		harness.Stream.Emit(StreamingSttEventKind.Delta, "item", "临时字幕");
		await checkedInterrupt.Task.WaitAsync(Deadline + Deadline);
		await checkedClear.Task.WaitAsync(Deadline);
		Assert.Equal("临时字幕", (await harness.Changes.Reader.ReadAsync().AsTask().WaitAsync(Deadline))?.Text);
		Assert.Null(await harness.Changes.Reader.ReadAsync().AsTask().WaitAsync(Deadline));
		Assert.False(harness.Changes.Reader.TryRead(out _));
		if (!interruptFromPartial) Assert.False(harness.Partials.Reader.TryRead(out _));
		Assert.Empty(harness.Turns);

		void CheckInterrupt() => CheckOffCallbackThread(checkedInterrupt, () =>
		{
			Assert.Equal(RealtimeVoicePhase.Listening, harness.Session.Status().Phase);
			Assert.Equal("临时字幕", harness.Session.ActivePartialTranscript?.Text);
			Assert.True(harness.Session.Interrupt());
		});
	}

	// 另外一个线程也能进入会话才证明事件不是在可重入的 _gate 内触发。
	private static void CheckOffCallbackThread(TaskCompletionSource result, Action check)
	{
		try
		{
			Task.Run(check).WaitAsync(Deadline).GetAwaiter().GetResult();
			result.TrySetResult();
		}
		catch (Exception exception) { result.TrySetException(exception); }
	}

	[Theory]
	[InlineData("")]
	[InlineData("  \t ")]
	public async Task RealtimeVoiceSession空白最终文本不触发对话(string final)
	{
		using Harness harness = new();
		await harness.StartSpeakingAsync();
		harness.Session.SetPushToTalk(false);
		await harness.Stream.CommitCalled.Task.WaitAsync(Deadline);
		harness.Stream.Emit(StreamingSttEventKind.Delta, "item", "不是最终文本");
		harness.Stream.Emit(StreamingSttEventKind.Completed, "item", final);
		await harness.Ended.Task.WaitAsync(Deadline);
		Assert.Equal(RealtimeVoicePhase.Idle, harness.Session.Phase);
		Assert.Empty(harness.Turns);
		Assert.Null(harness.Session.ActivePartialTranscript);
		Assert.Equal(0, harness.Batch.BatchCalls);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealtimeVoiceSession打开连接或提交前发送失败时回退整段(bool appendFails)
	{
		using Harness harness = new(openFails: !appendFails, appendFails: appendFails);
		await harness.StartSpeakingAsync();
		Assert.Equal(0, harness.Batch.BatchCalls);
		await harness.Capture.FeedAsync(3);
		harness.Session.SetPushToTalk(false);
		await harness.Ended.Task.WaitAsync(Deadline);
		Assert.True(harness.Session.UsedBatchFallback);
		Assert.Contains("提交前", harness.Session.FallbackReason);
		Assert.Equal(1, harness.Batch.BatchCalls);
		Assert.Equal("gpt-transcribe", harness.Batch.Options?.Model);
		Assert.Equal("zh", harness.Batch.Options?.Language);
		Assert.Equal(10284, harness.Batch.RecordingBytes);
		Assert.Equal(0, harness.Stream.Commits);
		Assert.Equal(1, harness.Streaming.OpenCalls);
		Assert.Null(harness.Session.ActivePartialTranscript);
		RealtimeVoiceTurn turn = Assert.Single(harness.Turns);
		Assert.Equal("整段识别", turn.Text);
		Assert.Null(turn.ProviderItemId);
	}

	[Theory]
	[InlineData(StreamingSttEventKind.Failed)]
	[InlineData(StreamingSttEventKind.Closed)]
	[InlineData(null)]
	public async Task RealtimeVoiceSession提交后故障或提交发送结果不确定时不重放(StreamingSttEventKind? failure)
	{
		using Harness harness = new(commitFails: failure is null);
		await harness.StartSpeakingAsync();
		harness.Session.SetPushToTalk(false);
		await harness.Stream.CommitCalled.Task.WaitAsync(Deadline);
		if (failure is { } kind) harness.Stream.Emit(kind, "item", "不应出现在错误中的内容");
		await harness.Ended.Task.WaitAsync(Deadline);
		Assert.Equal(RealtimeVoicePhase.Error, harness.Session.Phase);
		Assert.Contains("未重发", harness.Session.Status().Error);
		Assert.DoesNotContain("不应", harness.Session.Status().Error);
		Assert.False(harness.Session.UsedBatchFallback);
		Assert.Equal(0, harness.Batch.BatchCalls);
		Assert.Equal(1, harness.Stream.Commits);
		Assert.Equal(1, harness.Streaming.OpenCalls);
		Assert.Empty(harness.Turns);
	}

	[Theory]
	[InlineData(true, SttMode.RealtimeCloud, "安全模式")]
	[InlineData(true, SttMode.BatchCloud, "安全模式")]
	[InlineData(false, SttMode.Local, "本地")]
	public async Task RealtimeVoiceSession禁止的模式不会连接云端(bool safeMode, SttMode mode, string error)
	{
		using Harness harness = new(safeMode: safeMode, mode: mode);
		Assert.False(harness.Session.SupportsStreaming);
		InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Session.StartAsync());
		Assert.Contains(error, exception.Message);
		Assert.Equal(0, harness.Streaming.OpenCalls);
		Assert.Equal(0, harness.Batch.BatchCalls);
	}

	[Fact]
	public void RealtimeTranscriptionCoordinator丢弃旧代号且有界截断并锁定最终态()
	{
		RealtimeTranscriptionCoordinator coordinator = new(2, "turn");
		Assert.Equal(TranscriptionEventResult.Ignored, coordinator.Handle(1, Event(StreamingSttEventKind.Delta, "old", "旧")));
		Assert.Equal(TranscriptionEventResult.Ignored, coordinator.Handle(1, Event(StreamingSttEventKind.Completed, "old", "旧")));
		Assert.Null(coordinator.Snapshot);
		string prefix = new('字', RealtimeTranscriptionCoordinator.MaxTranscriptCharacters - 1);
		Assert.Equal(TranscriptionEventResult.Partial, coordinator.Handle(2, Event(StreamingSttEventKind.Delta, "item", prefix)));
		Assert.Equal(TranscriptionEventResult.Partial, coordinator.Handle(2, Event(StreamingSttEventKind.Delta, "item", "😀" + prefix)));
		Assert.Equal(prefix, coordinator.Snapshot!.Text);
		Assert.True(coordinator.Snapshot.Truncated);
		Assert.Equal(TranscriptionEventResult.Committed, coordinator.Handle(2, Event(StreamingSttEventKind.InputCommitted, "item")));
		Assert.Equal(TranscriptionEventResult.Ignored, coordinator.Handle(2, Event(StreamingSttEventKind.Completed, "other", "错项")));
		Assert.Equal(TranscriptionEventResult.Completed, coordinator.Handle(2, Event(StreamingSttEventKind.Completed, "item", "完成")));
		RealtimeTranscriptSnapshot final = coordinator.Snapshot!;
		Assert.True(final.IsFinal);
		Assert.False(final.Truncated);
		Assert.Equal("完成", final.Text);
		Assert.Equal(TranscriptionEventResult.Ignored, coordinator.Handle(2, Event(StreamingSttEventKind.Completed, "item", "重复")));
		Assert.Equal(TranscriptionEventResult.Ignored, coordinator.Handle(2, Event(StreamingSttEventKind.Delta, "item", "迟到")));
		Assert.Same(final, coordinator.Snapshot);
	}

	[Fact]
	public void RealtimeVoiceSettings读取STT默认值和保存值()
	{
		using TempDatabase file = new("nori-streaming-settings");
		using NoriDatabase database = NoriDatabase.Open(file.Path);
		ConfigStore config = new(database);
		config.InitDefaults("test");
		RealtimeVoiceSettings defaults = RealtimeVoiceSettings.Read(config);
		Assert.Equal(SttMode.BatchCloud, defaults.SttMode);
		Assert.Equal("gpt-live-transcribe", defaults.StreamingSttModel);
		Assert.Equal("gpt-transcribe", defaults.BatchSttModel);
		Assert.Equal("zh", defaults.Language);
		config.Set(RealtimeVoiceSettings.SttModeKey, new ConfigValue.Text(" realtime "));
		config.Set(RealtimeVoiceSettings.StreamingSttModelKey, new ConfigValue.Text(" stream-model "));
		config.Set(RealtimeVoiceSettings.BatchSttModelKey, new ConfigValue.Text("whisper-1"));
		config.Set(RealtimeVoiceSettings.LanguageKey, new ConfigValue.Text(""));
		RealtimeVoiceSettings saved = RealtimeVoiceSettings.Read(config);
		Assert.Equal(SttMode.RealtimeCloud, saved.SttMode);
		Assert.Equal("stream-model", saved.StreamingSttModel);
		Assert.Equal("whisper-1", saved.BatchSttModel);
		Assert.Equal("", saved.Language);
		config.Set(RealtimeVoiceSettings.SttModeKey, new ConfigValue.Text("local"));
		Assert.Equal(SttMode.Local, RealtimeVoiceSettings.Read(config).SttMode);
		config.Set(RealtimeVoiceSettings.SttModeKey, new ConfigValue.Text("unknown"));
		Assert.Equal(SttMode.BatchCloud, RealtimeVoiceSettings.Read(config).SttMode);
	}

	[Fact]
	public async Task 慢消费网络写入触发有界队列溢出并重置本轮且峰值受限()
	{
		TaskCompletionSource appendEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseAppend = new(TaskCreationOptions.RunContinuationsAsynchronously);
		SlowAppendStream slowStream = new(appendEntered, releaseAppend);
		FakeProvider streaming = new(slowStream, true, false);
		using RealtimeVoiceSession session = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, SttMode = SttMode.RealtimeCloud },
			false,
			null,
			null,
			streaming,
			null,
			() => { })
		{
			MaxStreamingBufferedBytes = 20_480,
			TurnAccepted = _ => Task.CompletedTask,
		};

		await session.StartAsync().WaitAsync(Deadline);
		float[] speech = Enumerable.Repeat(0.4f, 320).ToArray(); // 1280 bytes mono @ 16kHz (20ms)
		for (int i = 0; i < 15; i++)
			session.Observe(speech, 1, 16000);

		await appendEntered.Task.WaitAsync(Deadline);
		for (int i = 0; i < 24 && session.Phase == RealtimeVoicePhase.Listening; i++)
			session.Observe(speech, 1, 16000);

		Assert.Equal(RealtimeVoicePhase.Error, session.Phase);
		Assert.Contains("网络发送过慢", session.Status().Error, StringComparison.Ordinal);
		Assert.Equal(0, session.BufferedSampleCount);
		Assert.Equal(0, session.CurrentInputLevel);
		Assert.InRange(session.LastPeakBufferedBytes, 1, 20_480);
		releaseAppend.TrySetResult();

		// 溢出后可立即恢复开启新一轮。
		await session.StartAsync().WaitAsync(Deadline);
		Assert.Equal(RealtimeVoicePhase.Listening, session.Phase);
	}

	[Fact]
	public async Task 慢消费溢出会回退整段识别且峰值不超过上限()
	{
		TaskCompletionSource appendEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseAppend = new(TaskCreationOptions.RunContinuationsAsynchronously);
		SlowAppendStream slowStream = new(appendEntered, releaseAppend);
		FakeProvider streaming = new(slowStream, true, false);
		FakeProvider batch = new(null, false, false);
		ConcurrentQueue<RealtimeVoiceTurn> turns = new();
		TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

		using RealtimeVoiceSession session = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, SttMode = SttMode.RealtimeCloud },
			false,
			null,
			null,
			streaming,
			batch,
			() => { })
		{
			MaxStreamingBufferedBytes = 20_480,
			TurnAccepted = turn => { turns.Enqueue(turn); return Task.CompletedTask; },
		};
		session.PhaseChanged += phase =>
		{
			if (phase == RealtimeVoicePhase.Idle) ended.TrySetResult();
		};

		await session.StartAsync().WaitAsync(Deadline);
		session.SetPushToTalk(true);
		float[] speech = Enumerable.Repeat(0.4f, 320).ToArray();
		for (int i = 0; i < 15; i++)
			session.Observe(speech, 1, 16000);
		await appendEntered.Task.WaitAsync(Deadline);
		for (int i = 0; i < 24; i++)
			session.Observe(speech, 1, 16000);

		session.SetPushToTalk(false);
		await ended.Task.WaitAsync(Deadline);
		releaseAppend.TrySetResult();

		Assert.True(session.UsedBatchFallback);
		Assert.Contains("网络发送过慢", session.FallbackReason, StringComparison.Ordinal);
		Assert.Equal(1, batch.BatchCalls);
		Assert.InRange(session.LastPeakBufferedBytes, 1, 20_480);
		Assert.Equal("整段识别", Assert.Single(turns).Text);
	}

	[Fact]
	public async Task 第一轮打断后第二轮绝不会收到第一轮残留音频帧()
	{
		FakeStream firstStream = new(false, false);
		FakeStream secondStream = new(false, false);
		SequenceProvider streaming = new([firstStream, secondStream]);
		FakeProvider batch = new(null, false, false);
		ConcurrentQueue<RealtimeVoiceTurn> turns = new();

		using RealtimeVoiceSession session = new(
			() => RealtimeVoiceSettings.Defaults with { Enabled = true, Continuous = true, SttMode = SttMode.RealtimeCloud },
			false,
			null,
			null,
			streaming,
			batch,
			() => { })
		{
			TurnAccepted = turn => { turns.Enqueue(turn); return Task.CompletedTask; },
		};

		// 第一轮输入特征幅值 0.2f（24kHz PCM16 约为 6553），未提交前打断。
		await session.StartAsync().WaitAsync(Deadline);
		float[] turn1Speech = Enumerable.Repeat(0.2f, 320).ToArray();
		for (int i = 0; i < 14; i++)
			session.Observe(turn1Speech, 1, 16000);
		await firstStream.Audio.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
		Assert.True(session.Interrupt());
		await firstStream.Disposed.Task.WaitAsync(Deadline);

		// 第二轮输入特征幅值 0.6f（24kHz PCM16 约为 19660）。
		float[] turn2Speech = Enumerable.Repeat(0.6f, 320).ToArray();
		for (int i = 0; i < 14; i++)
			session.Observe(turn2Speech, 1, 16000);
		byte[] secondPreroll = await secondStream.Audio.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
		Assert.True(secondPreroll.Length >= 2);
		for (int offset = 0; offset < secondPreroll.Length; offset += 2)
		{
			short pcm = BinaryPrimitives.ReadInt16LittleEndian(secondPreroll.AsSpan(offset, 2));
			Assert.True(pcm >= 19000, $"第二轮首段音频出现意外样本 {pcm}，疑似第一轮残留");
		}

		float[] silence = new float[320];
		for (int i = 0; i < 40; i++)
			session.Observe(silence, 1, 16000);
		await secondStream.CommitCalled.Task.WaitAsync(Deadline);
		secondStream.Emit(StreamingSttEventKind.Completed, "item-2", "第二轮文本");

		using CancellationTokenSource waitTurn = new(Deadline);
		while (turns.IsEmpty) await Task.Delay(5, waitTurn.Token);
		Assert.Equal("第二轮文本", Assert.Single(turns).Text);
	}

	private sealed class SequenceProvider(IReadOnlyList<FakeStream> streams) : ISttProvider
	{
		private int _index;
		public string ProviderId => "seq";
		public SttMode Mode => SttMode.RealtimeCloud;
		public SttProviderCapabilities Capabilities { get; } = new()
		{
			SupportsRealtimePcm = true,
			SupportsIncrementalDelta = true,
		};

		public Task<IStreamingSttSession> OpenStreamingSessionAsync(SttRequestOptions options, CancellationToken cancellationToken)
		{
			int i = Interlocked.Increment(ref _index) - 1;
			return Task.FromResult<IStreamingSttSession>(streams[Math.Min(i, streams.Count - 1)]);
		}

		public Task<string> TranscribeAsync(RecordedAudio audio, SttRequestOptions options, CancellationToken cancellationToken) =>
			throw new NotSupportedException();
	}

	private sealed class SlowAppendStream(TaskCompletionSource appendEntered, TaskCompletionSource releaseAppend) : IStreamingSttSession
	{
		private readonly Channel<StreamingSttEvent> _events = Channel.CreateUnbounded<StreamingSttEvent>();
		public string ProviderId => "slow";
		public bool IsConnected => true;
		public ChannelReader<StreamingSttEvent> Events => _events.Reader;

		public async Task AppendAudioAsync(ReadOnlyMemory<byte> pcm16Mono24k, CancellationToken cancellationToken)
		{
			appendEntered.TrySetResult();
			await releaseAppend.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
		}

		public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private static StreamingSttEvent Event(StreamingSttEventKind kind, string? id, string? text = null) =>
		new() { Kind = kind, ItemId = id, Text = text };

	private sealed class Harness : IDisposable
	{
		public FakeCapture Capture { get; } = new();
		public CaptureOwnership Ownership { get; } = new();
		public FakeStream Stream { get; }
		public FakeProvider Streaming { get; }
		public FakeProvider Batch { get; } = new(null, false, false);
		public RealtimeVoiceSession Session { get; }
		public Channel<RealtimeTranscriptSnapshot?> Changes { get; } = Channel.CreateUnbounded<RealtimeTranscriptSnapshot?>();
		public Channel<(long, string)> Partials { get; } = Channel.CreateUnbounded<(long, string)>();
		public ConcurrentQueue<RealtimeVoiceTurn> Turns { get; } = new();
		public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Harness(bool openFails = false, bool appendFails = false, bool commitFails = false,
			bool safeMode = false, SttMode mode = SttMode.RealtimeCloud)
		{
			Stream = new(appendFails, commitFails);
			Streaming = new(Stream, true, openFails);
			Session = new(() => RealtimeVoiceSettings.Defaults with { Enabled = true, SttMode = mode },
				safeMode, Ownership, () => Capture, Streaming, Batch, () => { });
			Session.TranscriptChanged += snapshot => Changes.Writer.TryWrite(snapshot);
			Session.PartialTranscript += (generation, text) => Partials.Writer.TryWrite((generation, text));
			Session.TurnAccepted = turn => { Turns.Enqueue(turn); return Task.CompletedTask; };
			Session.PhaseChanged += phase =>
			{
				if (phase is RealtimeVoicePhase.Idle or RealtimeVoicePhase.Error) Ended.TrySetResult();
			};
		}

		public async Task StartSpeakingAsync()
		{
			Assert.True(Session.SupportsStreaming);
			await Session.StartAsync().WaitAsync(Deadline);
			Session.SetPushToTalk(true);
			await Capture.FeedAsync(12);
		}

		public void Dispose() => Session.Dispose();
	}

	private sealed class FakeCapture : IAudioCaptureDevice
	{
		private readonly BlockingCollection<TaskCompletionSource?> _frames = new();
		public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public AudioFormat Open() => new(48000, 2);

		public async Task FeedAsync(int frames)
		{
			for (int index = 0; index < frames; index++) _frames.Add(null);
			TaskCompletionSource consumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
			_frames.Add(consumed);
			await consumed.Task.WaitAsync(Deadline);
		}

		public int Read(Span<float> buffer, CancellationToken cancellationToken)
		{
			while (_frames.Take(cancellationToken) is { } consumed) consumed.TrySetResult();
			for (int index = 0; index < buffer.Length; index += 2)
			{
				buffer[index] = 0.5f;
				buffer[index + 1] = 0;
			}
			return buffer.Length;
		}

		public void Stop() => Stopped.TrySetResult();
		public void Dispose() => _frames.Dispose();
	}

	private sealed class FakeProvider(IStreamingSttSession? stream, bool streaming, bool openFails) : ISttProvider
	{
		public string ProviderId => "fake";
		public SttMode Mode => streaming ? SttMode.RealtimeCloud : SttMode.BatchCloud;
		public SttProviderCapabilities Capabilities { get; } = new()
		{
			SupportsRealtimePcm = streaming, SupportsIncrementalDelta = streaming, SupportsBatch = !streaming,
		};
		public int OpenCalls { get; private set; }
		public int BatchCalls { get; private set; }
		public int RecordingBytes { get; private set; }
		public SttRequestOptions? Options { get; private set; }

		public Task<IStreamingSttSession> OpenStreamingSessionAsync(SttRequestOptions options, CancellationToken cancellationToken)
		{
			OpenCalls++;
			if (openFails) throw new InvalidOperationException("模拟打开失败");
			return Task.FromResult<IStreamingSttSession>(stream!);
		}

		public Task<string> TranscribeAsync(RecordedAudio audio, SttRequestOptions options, CancellationToken cancellationToken)
		{
			BatchCalls++;
			RecordingBytes = audio.Bytes.Length;
			Options = options;
			return Task.FromResult("整段识别");
		}
	}

	private sealed class FakeStream(bool appendFails, bool commitFails) : IStreamingSttSession
	{
		private readonly Channel<StreamingSttEvent> _events = Channel.CreateUnbounded<StreamingSttEvent>();
		private int _commits;
		public string ProviderId => "fake";
		public bool IsConnected => !Disposed.Task.IsCompleted;
		public ChannelReader<StreamingSttEvent> Events => _events.Reader;
		public TaskCompletionSource CommitCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Channel<byte[]> Audio { get; } = Channel.CreateUnbounded<byte[]>();
		public int Commits => Volatile.Read(ref _commits);

		public void Emit(StreamingSttEventKind kind, string? id, string? text = null) => _events.Writer.TryWrite(Event(kind, id, text));

		public Task AppendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
		{
			if (appendFails) throw new InvalidOperationException("模拟发送失败");
			Audio.Writer.TryWrite(pcm.ToArray());
			return Task.CompletedTask;
		}

		public Task CommitAsync(CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _commits);
			CommitCalled.TrySetResult();
			if (commitFails) throw new InvalidOperationException("模拟提交发送结果不确定");
			return Task.CompletedTask;
		}

		public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		public ValueTask DisposeAsync() { Disposed.TrySetResult(); return ValueTask.CompletedTask; }
	}
}
