using System.Net;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Voice;

namespace Nori.Core.Tests;

/// <summary>HTTP 语音提供商的格式与降级测试。</summary>
public class VoiceProviderTests : IDisposable
{
	private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nori-voice-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public VoiceProviderTests()
	{
		_database = NoriDatabase.Open(_dbPath);
		_config = new ConfigStore(_database);
		_config.InitDefaults("0.1.0");
		_config.Set("gptsovits_base_url", new ConfigValue.Text("http://127.0.0.1:9880"));
	}

	[Theory]
	[InlineData("https://api.openai.com/v1")]
	[InlineData("https://api.openai.com/v1/audio/speech")]
	public async Task OpenAI显式请求WAV且保留合成参数(string baseUrl)
	{
		_config.Set("tts_base_url", new ConfigValue.Text(baseUrl));
		_config.Set("tts_model", new ConfigValue.Text("tts-1-hd"));
		RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = AudioContent([1, 2], "audio/wav"),
		});
		using HttpClient client = new(handler);
		OpenAiTtsProvider provider = new(client, _config);

		EncodedAudio audio = await provider.SynthesizeAsync(
			"你好", new TtsSynthesizeOptions {Voice = "alloy", Speed = 1.2}, CancellationToken.None);

		Assert.Equal("audio/wav", audio.Mime);
		Assert.Equal(new Uri("https://api.openai.com/v1/audio/speech"), Assert.Single(handler.Uris));
		JsonNode body = JsonNode.Parse(Assert.Single(handler.Bodies)!)!;
		Assert.Equal("wav", body["response_format"]?.GetValue<string>());
		Assert.Equal("tts-1-hd", body["model"]?.GetValue<string>());
		Assert.Equal("你好", body["input"]?.GetValue<string>());
		Assert.Equal("alloy", body["voice"]?.GetValue<string>());
		Assert.Equal(1.2, body["speed"]?.GetValue<double>());
	}

	[Fact]
	public async Task GPTSoVITS_POST显式请求非流式WAV()
	{
		RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = AudioContent([1, 2], "audio/wav"),
		});
		using HttpClient client = new(handler);
		GptSoVitsTtsProvider provider = new(client, _config);

		EncodedAudio audio = await provider.SynthesizeAsync(
			"你好", new TtsSynthesizeOptions {Speed = 1.2}, CancellationToken.None);

		Assert.Equal("audio/wav", audio.Mime);
		Assert.Equal(HttpMethod.Post, Assert.Single(handler.Methods));
		JsonNode body = JsonNode.Parse(Assert.Single(handler.Bodies)!)!;
		Assert.Equal("wav", body["media_type"]?.GetValue<string>());
		Assert.False(body["streaming_mode"]!.GetValue<bool>());
		Assert.Equal("你好", body["text"]?.GetValue<string>());
		Assert.Equal(1.2, body["speed_factor"]?.GetValue<double>());
	}

	[Fact]
	public async Task GPTSoVITS_POST非成功时降级GET()
	{
		RecordingHandler handler = new(request => request.Method == HttpMethod.Post
			? new HttpResponseMessage(HttpStatusCode.InternalServerError)
			: new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = AudioContent([9, 8, 7], "audio/wav"),
			});
		using HttpClient client = new(handler);
		GptSoVitsTtsProvider provider = new(client, _config);

		EncodedAudio audio = await provider.SynthesizeAsync("你好", new TtsSynthesizeOptions(), CancellationToken.None);

		Assert.Equal(new byte[] {9, 8, 7}, audio.Bytes);
		Assert.Equal("audio/wav", audio.Mime);
		Assert.Equal([HttpMethod.Post, HttpMethod.Get], handler.Methods);
		Assert.Contains("media_type=wav", handler.Uris[1]!.Query, StringComparison.Ordinal);
		Assert.Contains("streaming_mode=false", handler.Uris[1]!.Query, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GPTSoVITS_POST空响应时降级GET()
	{
		RecordingHandler handler = new(request =>
		{
			if (request.Method == HttpMethod.Post)
			{
				HttpResponseMessage response = new(HttpStatusCode.OK) {Content = new ByteArrayContent([])};
				response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
				return response;
			}
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = AudioContent([1, 2], "audio/wav"),
			};
		});
		using HttpClient client = new(handler);
		GptSoVitsTtsProvider provider = new(client, _config);

		EncodedAudio audio = await provider.SynthesizeAsync("你好", new TtsSynthesizeOptions(), CancellationToken.None);

		Assert.Equal(new byte[] {1, 2}, audio.Bytes);
		Assert.Equal([HttpMethod.Post, HttpMethod.Get], handler.Methods);
	}

	[Fact]
	public async Task TTS缺少或错误MIME时失败()
	{
		RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new ByteArrayContent([1]),
		});
		using HttpClient client = new(handler);
		GptSoVitsTtsProvider provider = new(client, _config);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			provider.SynthesizeAsync("你好", new TtsSynthesizeOptions(), CancellationToken.None));
	}

	[Theory]
	[InlineData("audio/mpeg")]
	[InlineData("audio/ogg")]
	public async Task Custom保留非WAV响应供显式兼容后端使用(string mime)
	{
		_config.Set("tts_base_url", new ConfigValue.Text("http://127.0.0.1:9000/tts"));
		byte[] bytes = [1, 2, 3];
		RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = AudioContent(bytes, mime),
		});
		using HttpClient client = new(handler);
		CustomHttpTtsProvider provider = new(client, _config);

		EncodedAudio audio = await provider.SynthesizeAsync(
			"你好", new TtsSynthesizeOptions {Voice = "custom-voice", Speed = 1.2}, CancellationToken.None);

		Assert.Equal(bytes, audio.Bytes);
		Assert.Equal(mime, audio.Mime);
		JsonObject body = JsonNode.Parse(Assert.Single(handler.Bodies)!)!.AsObject();
		Assert.Equal("你好", body["text"]?.GetValue<string>());
		Assert.Equal("custom-voice", body["voice"]?.GetValue<string>());
		Assert.Equal(1.2, body["speed"]?.GetValue<double>());
		Assert.Equal(3, body.Count);
	}

	private static ByteArrayContent AudioContent(byte[] bytes, string mime)
	{
		ByteArrayContent content = new(bytes);
		content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
		return content;
	}

	public void Dispose()
	{
		_database.Dispose();
		try { File.Delete(_dbPath); } catch (IOException) { }
	}

	private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		public List<HttpMethod> Methods { get; } = [];
		public List<Uri?> Uris { get; } = [];
		public List<string?> Bodies { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Methods.Add(request.Method);
			Uris.Add(request.RequestUri);
			Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
			HttpResponseMessage response = responder(request);
			if (request.Method == HttpMethod.Get && response.Content is null)
			{
				response.Content = AudioContent([9, 8, 7], "audio/wav");
			}
			return response;
		}
	}
}
