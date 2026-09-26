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

	public void Dispose()
	{
		_database.Dispose();
		_tempDatabase.Dispose();
	}

	private sealed class AudioHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = AudioContent([1, 2, 3]),
			});

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
			Played.Add(audio);
			IsPlaying = true;
			PlayingChanged?.Invoke(true);
			Started.TrySetResult(true);
			if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			IsPlaying = false;
			PlayingChanged?.Invoke(false);
		}

		public void Stop()
		{
			IsPlaying = false;
			PlayingChanged?.Invoke(false);
		}

		public void Dispose()
		{
		}
	}
}
