using System.Net;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;
using Nori.Core.Voice;
using Nori.Core.Voice.Audio;

namespace Nori.Core.Tests;

/// <summary>语音流水线、取消和可观察状态测试。</summary>
public class VoiceServiceTests : IDisposable
{
	private readonly TempDatabase _tempDatabase = new("nori-voice-service");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public VoiceServiceTests()
	{
		_database = NoriDatabase.Open(_tempDatabase.Path);
		_config = new ConfigStore(_database);
		_config.InitDefaults("0.1.0");
		_config.Set("tts_provider", new ConfigValue.Text("openai"));
		_config.Set("tts_base_url", new ConfigValue.Text("http://127.0.0.1:9880/v1"));
	}

	[Theory]
	[InlineData("gemini", typeof(GeminiTtsProvider))]
	[InlineData("minimax", typeof(MiniMaxTtsProvider))]
	[InlineData("indextts", typeof(IndexTtsProvider))]
	public void 按名称创建对应语音提供商(string name, Type expected)
	{
		using HttpClient client = new(new AudioHandler());
		using VoiceService service = new(client, _config, null, () => null);
		Assert.IsType(expected, service.CreateProvider(name));
	}

	[Fact]
	public async Task 句子流水线先播放已完成段并最终结束()
	{
		FakePlayback playback = new();
		using HttpClient client = new(new AudioHandler());
		using VoiceService voice = new(client, _config, playback, () => null);

		await voice.SpeakAsync("第一句。第二句！");

		Assert.Equal(2, playback.Played.Count);
		Assert.All(playback.Played, audio => Assert.Equal("audio/wav", audio.Mime));
		Assert.False(voice.IsSpeaking);
	}

	[Fact]
	public async Task Stop取消合成播放并发出状态变化()
	{
		FakePlayback playback = new() {WaitForCancellation = true};
		using HttpClient client = new(new AudioHandler());
		using VoiceService voice = new(client, _config, playback, () => null);
		List<bool> states = [];
		voice.SpeakingChanged += states.Add;

		Task speak = voice.SpeakAsync("请停止这段朗读。", cancellationToken: CancellationToken.None);
		await playback.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
		voice.Stop();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => speak);
		Assert.False(voice.IsSpeaking);
		Assert.Equal([true, false], states);
	}

	[Fact]
	public async Task 合成失败不会被流水线取消异常覆盖()
	{
		using HttpClient client = new(new FailingHandler());
		using VoiceService voice = new(client, _config, new FakePlayback(), () => null);

		VoiceProviderException error = await Assert.ThrowsAsync<VoiceProviderException>(() => voice.SpeakAsync("失败测试"));
		Assert.IsType<HttpRequestException>(error.InnerException);
		Assert.False(voice.IsSpeaking);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task 播放失败时已释放取消回调不会覆盖原始异常(bool nested)
	{
		IOException primary = new("播放失败");
		ObjectDisposedException disposed = new("已释放的播放资源");
		FakePlayback playback = new()
		{
			Failure = primary,
			CancellationFailure = nested ? new AggregateException(disposed) : disposed,
		};
		using HttpClient client = new(new AudioHandler());
		using VoiceService voice = new(client, _config, playback, () => null);

		IOException error = await Assert.ThrowsAsync<IOException>(() => voice.SpeakAsync("第一句。第二句。"));

		Assert.Same(primary, error);
		Assert.Equal(1, playback.CancellationCallbackCount);
		Assert.Equal(1, playback.StopCount);
		Assert.False(voice.IsSpeaking);
	}

	[Fact]
	public async Task 后续合成失败时已释放播放回调不会覆盖提供商异常()
	{
		FakePlayback playback = new()
		{
			WaitForCancellation = true,
			CancellationFailure = new ObjectDisposedException("已释放的播放资源"),
		};
		using HttpClient client = new(new AudioHandler(playback.Started.Task));
		using VoiceService voice = new(client, _config, playback, () => null);

		VoiceProviderException error = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			voice.SpeakAsync("第一句。第二句。").WaitAsync(TimeSpan.FromSeconds(5)));

		Assert.IsType<HttpRequestException>(error.InnerException);
		Assert.Equal(1, playback.CancellationCallbackCount);
		Assert.Equal(1, playback.StopCount);
		Assert.False(voice.IsSpeaking);
	}

	[Fact]
	public async Task Stop忽略已释放回调并继续停止播放()
	{
		FakePlayback playback = new()
		{
			WaitForCancellation = true,
			CancellationFailure = new ObjectDisposedException("已释放的播放资源"),
		};
		using HttpClient client = new(new AudioHandler());
		using VoiceService voice = new(client, _config, playback, () => null);
		Task speak = voice.SpeakAsync("停止测试。");
		await playback.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		voice.Stop();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => speak);
		Assert.Equal(1, playback.CancellationCallbackCount);
		Assert.True(playback.StopCount > 0);
		Assert.False(voice.IsSpeaking);
	}

	[Fact]
	public async Task Stop不会吞掉混合聚合中的其他回调错误()
	{
		InvalidOperationException unexpected = new("取消回调失败");
		FakePlayback playback = new()
		{
			WaitForCancellation = true,
			CancellationFailure = new AggregateException(new ObjectDisposedException("已释放资源"), unexpected),
		};
		using HttpClient client = new(new AudioHandler());
		using VoiceService voice = new(client, _config, playback, () => null);
		Task speak = voice.SpeakAsync("停止测试。");
		await playback.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		AggregateException error = Assert.Throws<AggregateException>(voice.Stop);

		Assert.Contains(unexpected, error.Flatten().InnerExceptions);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => speak);
		Assert.False(voice.IsSpeaking);
	}

	[Fact]
	public async Task IndexTTS整段一次合成不拆段()
	{
		_config.Set("tts_provider", new ConfigValue.Text("indextts"));
		_config.Set("tts_base_url", new ConfigValue.Text("https://api.modelverse.cn/v1"));
		_config.Set("tts_api_key", new ConfigValue.Text("test-secret"));
		_config.Set("tts_voice", new ConfigValue.Text("uspeech:test-voice"));
		FakePlayback playback = new();
		CountingHandler handler = new();
		using HttpClient client = new(handler);
		using VoiceService voice = new(client, _config, playback, () => null);

		await voice.SpeakAsync("第一句。第二句！第三句？");

		Assert.Equal(1, handler.RequestCount); // 整段一次合成，不因三个句子拆成三次
		Assert.Single(playback.Played);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task 识别失败后的录音状态区分采集停止与网络转写(bool stopFails)
	{
		TestRecorder recorder = new() { StopFails = stopFails };
		using HttpClient client = new(new FailingHandler());
		using VoiceService voice = new(client, _config, null, () => recorder);
		Assert.False(voice.IsRecording);
		await voice.StartListeningAsync();
		Assert.True(voice.IsRecording);
		if (stopFails)
			await Assert.ThrowsAsync<IOException>(() => voice.StopListeningAndTranscribeAsync());
		else
			await Assert.ThrowsAsync<VoiceProviderException>(() => voice.StopListeningAndTranscribeAsync());
		Assert.Equal(stopFails, voice.IsRecording);
	}

	[Fact]
	public async Task E2_首段合成完毕立即开始播放且有界预取不一次性合成全部二十句()
	{
		TaskCompletionSource chunk1Playing = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseChunk1 = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource chunk2Synthesizing = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseChunk2 = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int requestCount = 0;

		using NativeAudioPlayback playback = new(
			() => new GateDevice(chunk1Playing, releaseChunk1),
			(_, _) => new PcmAudio { Samples = new float[441], SampleRate = 44100, Channels = 1 });
		using HttpClient client = new(new GatedTtsHandler(
			() => Interlocked.Increment(ref requestCount),
			chunk2Synthesizing,
			releaseChunk2));
		using VoiceService voice = new(client, _config, playback, () => null);

		string twentySentences = string.Concat(Enumerable.Range(1, 20).Select(i => $"第{i}句。"));
		Task speak = voice.SpeakAsync(twentySentences);

		// 第 1 句合成完毕立刻进入播放，此时第 2 句仍在合成等待中。
		await chunk1Playing.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await chunk2Synthesizing.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(playback.IsPlaying);
		Assert.NotNull(voice.LastPlaybackMetrics?.FirstChunkSynthesizedAtUtc);
		Assert.NotNull(voice.LastPlaybackMetrics?.FirstChunkPlaybackStartedAtUtc);

		// 放行第 2、3 句合成但继续阻塞第 1 句播放：由于通道有界（容量 2），最多只预取到第 3 句，绝不会把 20 句全部请求完。
		releaseChunk2.TrySetResult();
		await Task.Delay(80);
		int prefetchedWhilePlayingFirst = Volatile.Read(ref requestCount);
		Assert.InRange(prefetchedWhilePlayingFirst, 2, VoiceAudioLimits.SynthesisQueueCapacity + 1);

		// 中途停止：立即取消在途合成、清空队列并复位状态。
		voice.Stop();
		releaseChunk1.TrySetResult();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => speak);
		Assert.False(voice.IsSpeaking);
		Assert.False(playback.IsPlaying);
		Assert.True(voice.LastPlaybackMetrics?.Cancelled);
		Assert.True(Volatile.Read(ref requestCount) < 20);
	}

	[Fact]
	public async Task E2_设备中途异常后状态可靠复位且下次朗读自动恢复并保持口型同步()
	{
		int openAttempt = 0;
		List<bool> speakingStates = [];
		List<bool> playingStates = [];
		List<double> levels = [];

		using NativeAudioPlayback playback = new(
			() => Interlocked.Increment(ref openAttempt) == 1
				? new FailingMidWriteDevice()
				: new ImmediateDevice(),
			(_, _) =>
			{
				float[] samples = new float[4410];
				for (int i = 0; i < samples.Length; i++) samples[i] = 0.5f * MathF.Sin(i * 0.05f);
				return new PcmAudio { Samples = samples, SampleRate = 44100, Channels = 1 };
			});
		playback.PlayingChanged += playingStates.Add;
		playback.VolumeSampled += levels.Add;

		using HttpClient client = new(new AudioHandler());
		using VoiceService voice = new(client, _config, playback, () => null);
		voice.SpeakingChanged += speakingStates.Add;

		await Assert.ThrowsAsync<AudioDeviceException>(() => voice.SpeakAsync("设备中途拔出测试。"));
		Assert.False(voice.IsSpeaking);
		Assert.False(playback.IsPlaying);
		Assert.Equal([true, false], speakingStates);
		Assert.Equal([true, false], playingStates);
		Assert.Equal(0, levels[^1]);
		Assert.True(voice.LastPlaybackMetrics?.Faulted);

		// 恢复后下次朗读打开新设备正常完成。
		speakingStates.Clear();
		playingStates.Clear();
		await voice.SpeakAsync("恢复后的正常朗读。");
		Assert.False(voice.IsSpeaking);
		Assert.False(playback.IsPlaying);
		Assert.Equal([true, false], speakingStates);
		Assert.Equal([true, false], playingStates);
		Assert.Equal(0, levels[^1]);
		Assert.Contains(levels, l => l > 0.1);
	}

	[Fact]
	public async Task E2_颜文字与空行混排时不因空白段失败()
	{
		FakePlayback playback = new();
		using HttpClient client = new(new AudioHandler());
		using VoiceService voice = new(client, _config, playback, () => null);

		await voice.SpeakAsync("第一句！\n(๑•̀ㅂ•́)و✧\n第二句？");

		Assert.Equal(2, playback.Played.Count);
		Assert.False(voice.IsSpeaking);
	}

	[Fact]
	public async Task P0_卡死播放设备忽略取消时调用RecoverPlayback立即复位并允许后续SpeakAsync完成()
	{
		TaskCompletionSource stuckEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseStuck = new(TaskCreationOptions.RunContinuationsAsynchronously);
		StuckUncancellableDevice stuckDevice = new(stuckEntered, releaseStuck);
		RecordingDevice healthyDevice = new();
		int openCount = 0;

		List<bool> playingStates = [];
		List<bool> speakingStates = [];
		List<double> levels = [];

		using NativeAudioPlayback playback = new(
			() => Interlocked.Increment(ref openCount) == 1 ? stuckDevice : healthyDevice,
			(_, _) =>
			{
				float[] samples = new float[4410];
				for (int i = 0; i < samples.Length; i++) samples[i] = 0.5f * MathF.Sin(i * 0.05f);
				return new PcmAudio { Samples = samples, SampleRate = 44100, Channels = 1 };
			});
		playback.PlayingChanged += playingStates.Add;
		playback.VolumeSampled += levels.Add;

		using HttpClient client = new(new AudioHandler());
		using VoiceService voice = new(client, _config, playback, () => null);
		voice.SpeakingChanged += speakingStates.Add;

		Task firstSpeak = voice.SpeakAsync("卡死播放设备测试。");
		await stuckEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(voice.IsSpeaking);
		Assert.True(playback.IsPlaying);

		try
		{
			// 严禁提前放行旧设备：验证 RecoverPlayback 立即解绑、归零并释放队列。
			voice.RecoverPlayback();

			Assert.False(voice.IsSpeaking);
			Assert.False(playback.IsPlaying);
			Assert.Equal([true, false], playingStates);
			Assert.Equal([true, false], speakingStates);
			Assert.Equal(0, levels[^1]);
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstSpeak.WaitAsync(TimeSpan.FromSeconds(5)));

			// 在旧设备仍然卡死阻塞的前提下，紧接着的新朗读必须正常打开新设备并完成。
			playingStates.Clear();
			speakingStates.Clear();
			await voice.SpeakAsync("恢复后的下一句朗读。").WaitAsync(TimeSpan.FromSeconds(5));

			Assert.False(voice.IsSpeaking);
			Assert.False(playback.IsPlaying);
			Assert.Equal([true, false], playingStates);
			Assert.Equal([true, false], speakingStates);
			Assert.Equal(0, levels[^1]);
			Assert.NotEmpty(healthyDevice.Written);
			Assert.True(healthyDevice.Drained);
			Assert.Equal(2, Volatile.Read(ref openCount));
		}
		finally
		{
			releaseStuck.TrySetResult();
			await stuckDevice.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}
	}

	[Fact]
	public async Task P1_抢占旧轮次时Stop异常不泄漏状态且后续接管不被误停()
	{
		ThrowOnStopPlayback playback = new() { ThrowOnStop = true };
		using HttpClient client = new(new AudioHandler());
		using VoiceService voice = new(client, _config, playback, () => null);

		Task first = voice.SpeakAsync("旧轮次朗读。");
		await playback.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(voice.IsSpeaking);

		// 第二轮抢占旧轮次时，旧轮次绑定的 Stop() 抛出异常；新轮次必须可靠清理自身状态，不泄漏 _speechCts 或卡死 IsSpeaking。
		AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => voice.SpeakAsync("抢占新轮次。"));
		Assert.Contains(error.Flatten().InnerExceptions, ex => ex is InvalidOperationException);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.False(voice.IsSpeaking);

		// 下一轮可正常完成且不会被残留停止回调误停。
		playback.WaitForCancellation = false;
		await voice.SpeakAsync("恢复正常朗读。").WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(voice.IsSpeaking);
		Assert.Equal(2, playback.PlayCount);
	}

	private sealed class StuckUncancellableDevice(TaskCompletionSource writeEntered, TaskCompletionSource releaseWrite) : IAudioDevice
	{
		public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public double Volume { get; set; } = 1.0;
		public AudioFormat Open(int sampleRate, int channels) => new(sampleRate, channels);
		public int Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
		{
			writeEntered.TrySetResult();
			// 故意忽略 cancellationToken 与 Stop()，模拟底层驱动彻底卡死。
			Assert.True(releaseWrite.Task.Wait(TimeSpan.FromSeconds(10)), "测试结束前未释放卡死设备");
			return 0;
		}
		public void Drain(CancellationToken cancellationToken) { }
		public void Stop() { }
		public void Dispose() => Disposed.TrySetResult();
	}

	private sealed class RecordingDevice : IAudioDevice
	{
		public List<float> Written { get; } = [];
		public bool Drained { get; private set; }
		public double Volume { get; set; } = 1.0;
		public AudioFormat Open(int sampleRate, int channels) => new(sampleRate, channels);
		public int Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
		{
			Written.AddRange(samples.ToArray());
			return samples.Length;
		}
		public void Drain(CancellationToken cancellationToken) => Drained = true;
		public void Stop() { }
		public void Dispose() { }
	}

	private sealed class ThrowOnStopPlayback : IAudioPlayback
	{
		private int _stopCalls;
		public bool WaitForCancellation { get; set; } = true;
		public bool ThrowOnStop { get; set; }
		public int PlayCount { get; private set; }
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool IsPlaying { get; private set; }
		public event Action<bool>? PlayingChanged;
		event Action<double>? IAudioPlayback.VolumeSampled
		{
			add { }
			remove { }
		}

		public async Task PlayAsync(EncodedAudio audio, CancellationToken cancellationToken)
		{
			PlayCount++;
			IsPlaying = true;
			PlayingChanged?.Invoke(true);
			Started.TrySetResult();
			if (WaitForCancellation)
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			IsPlaying = false;
			PlayingChanged?.Invoke(false);
		}

		public void Stop()
		{
			IsPlaying = false;
			PlayingChanged?.Invoke(false);
			if (ThrowOnStop && Interlocked.Increment(ref _stopCalls) == 1)
				throw new InvalidOperationException("模拟停止播放失败");
		}

		public void Dispose() { }
	}

	private sealed class GateDevice(TaskCompletionSource writeEntered, TaskCompletionSource releaseWrite) : IAudioDevice
	{
		private bool _first = true;
		public double Volume { get; set; } = 1.0;
		public AudioFormat Open(int sampleRate, int channels) => new(sampleRate, channels);
		public int Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
		{
			if (_first)
			{
				_first = false;
				writeEntered.TrySetResult();
				releaseWrite.Task.Wait(TimeSpan.FromSeconds(5), cancellationToken);
			}
			return cancellationToken.IsCancellationRequested ? 0 : samples.Length;
		}
		public void Drain(CancellationToken cancellationToken) { }
		public void Stop() => releaseWrite.TrySetResult();
		public void Dispose() { }
	}

	private sealed class FailingMidWriteDevice : IAudioDevice
	{
		public double Volume { get; set; } = 1.0;
		public AudioFormat Open(int sampleRate, int channels) => new(sampleRate, channels);
		public int Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken) =>
			throw new AudioDeviceException("模拟播放设备断开");
		public void Drain(CancellationToken cancellationToken) { }
		public void Stop() { }
		public void Dispose() { }
	}

	private sealed class ImmediateDevice : IAudioDevice
	{
		public double Volume { get; set; } = 1.0;
		public AudioFormat Open(int sampleRate, int channels) => new(sampleRate, channels);
		public int Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken) => samples.Length;
		public void Drain(CancellationToken cancellationToken) { }
		public void Stop() { }
		public void Dispose() { }
	}

	private sealed class GatedTtsHandler(Func<int> nextIndex, TaskCompletionSource chunk2Entered, TaskCompletionSource releaseChunk2) : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			int index = nextIndex();
			if (index == 2)
			{
				chunk2Entered.TrySetResult();
				await releaseChunk2.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
			}
			ByteArrayContent content = new([1, 2, 3]);
			content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
		}
	}

	private sealed class TestRecorder : IMicrophoneRecorder
	{
		public bool StopFails { get; init; }
		public bool IsRecording { get; private set; }
		public Task StartAsync(CancellationToken cancellationToken = default)
		{
			IsRecording = true;
			return Task.CompletedTask;
		}
		public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
		{
			if (StopFails) throw new IOException("采集停止失败");
			IsRecording = false;
			return Task.FromResult(new RecordedAudio([1, 2, 3], "audio/wav", "recording.wav"));
		}
		public void Dispose() { }
	}

	public void Dispose()
	{
		_database.Dispose();
		_tempDatabase.Dispose();
	}

	private sealed class AudioHandler(Task? failAfterPlaybackStarted = null) : HttpMessageHandler
	{
		private int _requestCount;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (++_requestCount == 2 && failAfterPlaybackStarted is not null)
			{
				await failAfterPlaybackStarted.WaitAsync(cancellationToken);
				throw new HttpRequestException("后续合成失败");
			}
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = AudioContent([1, 2, 3]),
			};
		}

		private static ByteArrayContent AudioContent(byte[] bytes)
		{
			ByteArrayContent content = new(bytes);
			content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
			return content;
		}
	}

	private sealed class FailingHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			throw new HttpRequestException("test connection failure");
	}

	private sealed class CountingHandler : HttpMessageHandler
	{
		public int RequestCount { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			RequestCount++;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new ByteArrayContent([1, 2, 3])
				{
					Headers = {ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav")},
				},
			});
		}
	}

	private sealed class FakePlayback : IAudioPlayback
	{
		public bool WaitForCancellation { get; init; }
		public Exception? Failure { get; init; }
		public Exception? CancellationFailure { get; init; }
		public int CancellationCallbackCount { get; private set; }
		public int StopCount { get; private set; }
		public List<EncodedAudio> Played { get; } = [];
		public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool IsPlaying { get; private set; }
		public event Action<bool>? PlayingChanged;
		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3237", Justification = "测试替身不使用音量采样事件，显式接口事件访问器必须保持空实现。")]
		event Action<double>? IAudioPlayback.VolumeSampled
		{
			add { }
			remove { }
		}

		public async Task PlayAsync(EncodedAudio audio, CancellationToken cancellationToken)
		{
			if (CancellationFailure is { } cancellationFailure)
			{
				// 故意保留回调，模拟播放资源已释放但取消注册尚未摘除的竞态。
				_ = cancellationToken.Register(() =>
				{
					CancellationCallbackCount++;
					throw cancellationFailure;
				});
			}
			Played.Add(audio);
			IsPlaying = true;
			PlayingChanged?.Invoke(true);
			Started.TrySetResult(true);
			if (Failure is { } failure) throw failure;
			if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			IsPlaying = false;
			PlayingChanged?.Invoke(false);
		}

		public void Stop()
		{
			StopCount++;
			IsPlaying = false;
			PlayingChanged?.Invoke(false);
		}

		public void Dispose()
		{
		}
	}
}
