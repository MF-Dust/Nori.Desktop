using System.Net;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;
using Nori.Core.Voice;

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
