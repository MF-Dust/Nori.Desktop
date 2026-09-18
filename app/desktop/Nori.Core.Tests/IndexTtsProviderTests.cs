using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Voice;
using Nori.Core.Voice.Audio;

namespace Nori.Core.Tests;

/// <summary>IndexTTS-2 (优云智算) OpenAI 兼容请求映射与错误处理测试。</summary>
public class IndexTtsProviderTests : IDisposable
{
	private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nori-indextts-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public IndexTtsProviderTests()
	{
		_database = NoriDatabase.Open(_dbPath);
		_config = new ConfigStore(_database);
		_config.InitDefaults("0.1.0");
		_config.Set("tts_provider", new ConfigValue.Text("indextts"));
		_config.Set("tts_api_key", new ConfigValue.Text("test-secret"));
	}

	[Fact]
	public async Task 默认配置正确映射请求到Modelverse音频端点()
	{
		CaptureHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		EncodedAudio audio = await provider.SynthesizeAsync(
			"你好，Nori",
			new TtsSynthesizeOptions {Voice = "uspeech:abc123", Speed = 1.2},
			CancellationToken.None);

		Assert.Equal(new Uri("https://api.modelverse.cn/v1/audio/speech"), handler.LastUri);
		Assert.Equal("Bearer", handler.AuthorizationScheme);
		Assert.Equal("test-secret", handler.AuthorizationParameter);
		Assert.Equal("audio/wav", audio.Mime);
		Assert.True(WaveDecoder.IsWave(audio.Bytes));

		JsonNode body = JsonNode.Parse(handler.LastBody!)!;
		Assert.Equal("IndexTeam/IndexTTS-2", body["model"]?.GetValue<string>());
		Assert.Equal("你好，Nori", body["input"]?.GetValue<string>());
		Assert.Equal("uspeech:abc123", body["voice"]?.GetValue<string>());
		Assert.Equal(1.2, body["speed"]?.GetValue<double>());
		// Modelverse 固定返回 WAV，不向该接口加入未声明支持的格式参数。
		Assert.Null(body["response_format"]);
	}

	[Fact]
	public async Task 完整端点不会重复追加音频路径()
	{
		_config.Set("tts_base_url", new ConfigValue.Text("https://api.modelverse.cn/v1/audio/speech"));
		CaptureHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = "uspeech:abc"}, CancellationToken.None);

		Assert.Equal(new Uri("https://api.modelverse.cn/v1/audio/speech"), handler.LastUri);
	}

	[Fact]
	public async Task 配置模型名优先于默认值()
	{
		_config.Set("tts_model", new ConfigValue.Text("IndexTeam/IndexTTS-2-chinese"));
		CaptureHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = "uspeech:abc"}, CancellationToken.None);

		JsonNode body = JsonNode.Parse(handler.LastBody!)!;
		Assert.Equal("IndexTeam/IndexTTS-2-chinese", body["model"]?.GetValue<string>());
	}

	[Fact]
	public async Task 未配置APIKey时本地报错()
	{
		_config.Set("tts_api_key", new ConfigValue.Text(""));
		CaptureHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			provider.SynthesizeAsync("测试", new TtsSynthesizeOptions(), CancellationToken.None));

		Assert.Contains("IndexTTS-2", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 未配置音色ID时本地报错()
	{
		CaptureHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = ""}, CancellationToken.None));

		Assert.Contains("音色", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task HTTP错误携带错误消息()
	{
		CaptureHandler handler = new(_ => JsonResponse(HttpStatusCode.Unauthorized, """
			{"error": {"message": "invalid api key"}}
			"""));
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		VoiceProviderException error = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = "uspeech:abc"}, CancellationToken.None));

		Assert.Equal(VoiceFailureKind.HttpRejected, error.FailureKind);
		Assert.Equal(401, error.HttpStatusCode);
		Assert.Contains("invalid api key", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 扩展字段按配置透传()
	{
		_config.Set("indextts_emo_text", new ConfigValue.Text("开心又期待"));
		_config.Set("indextts_sample_rate", new ConfigValue.Text("44100"));
		_config.Set("indextts_gain", new ConfigValue.Text("1.5"));
		CaptureHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = "uspeech:abc"}, CancellationToken.None);

		JsonNode body = JsonNode.Parse(handler.LastBody!)!;
		Assert.Equal("开心又期待", body["emo_text"]?.GetValue<string>());
		Assert.Equal(44100, body["sample_rate"]?.GetValue<int>());
		Assert.Equal(1.5, body["gain"]?.GetValue<double>());
	}

	[Fact]
	public async Task 未配置的扩展字段不进入请求体()
	{
		CaptureHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = "uspeech:abc"}, CancellationToken.None);

		JsonNode body = JsonNode.Parse(handler.LastBody!)!;
		Assert.Null(body["emo_text"]);
		Assert.Null(body["sample_rate"]);
		Assert.Null(body["gain"]);
		Assert.Null(body["interval_silence"]);
	}

	[Fact]
	public async Task 英文情绪值经选项映射为情感参数()
	{
		CaptureHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = "uspeech:abc", EmotionText = "happy"}, CancellationToken.None);

		JsonNode body = JsonNode.Parse(handler.LastBody!)!;
		Assert.Equal(3, body["emo_control_method"]?.GetValue<int>());
		Assert.Equal("happy", body["emo_text"]?.GetValue<string>());
		Assert.Equal(0.3, body["emo_weight"]?.GetValue<double>());
	}

	[Fact]
	public async Task Neutral情绪不注入情感参数()
	{
		CaptureHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = "uspeech:abc", EmotionText = "neutral"}, CancellationToken.None);

		JsonNode body = JsonNode.Parse(handler.LastBody!)!;
		Assert.Null(body["emo_control_method"]);
		Assert.Null(body["emo_text"]);
		Assert.Null(body["emo_weight"]);
	}

	[Fact]
	public async Task 情绪强度可配置覆盖默认值()
	{
		_config.Set("indextts_emo_alpha", new ConfigValue.Text("0.5"));
		CaptureHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = "uspeech:abc", EmotionText = "sad"}, CancellationToken.None);

		JsonNode body = JsonNode.Parse(handler.LastBody!)!;
		Assert.Equal(0.5, body["emo_weight"]?.GetValue<double>());
	}

	[Fact]
	public async Task 选项情绪优先于配置情绪()
	{
		_config.Set("indextts_emo_text", new ConfigValue.Text("angry"));
		CaptureHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = "uspeech:abc", EmotionText = "happy"}, CancellationToken.None);

		JsonNode body = JsonNode.Parse(handler.LastBody!)!;
		Assert.Equal("happy", body["emo_text"]?.GetValue<string>());
	}

	[Fact]
	public async Task 克隆音色上传并返回VoiceId()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-indextts-upload-{Guid.NewGuid():N}");
		string template = Path.Combine(tempDir, "voice.wav");
		Directory.CreateDirectory(tempDir);
		File.WriteAllBytes(template, MinimalWav());
		AppStoragePaths paths = new(tempDir);

		CaptureHandler handler = new(_ => JsonResponse(HttpStatusCode.OK, """{"id": "uspeech:uploaded-123"}"""));
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config, paths);

		string voiceId = await provider.CloneVoiceAsync(template, CancellationToken.None);

		Assert.Equal("uspeech:uploaded-123", voiceId);
		Assert.Equal(new Uri("https://api.modelverse.cn/v1/audio/voice/upload"), handler.LastUri);
		// 存档已写入 data/resources/indextts/voices/ 目录
		Assert.True(Directory.Exists(Path.Combine(tempDir, "data", "resources", "indextts", "voices")));
		Assert.True(File.Exists(Path.Combine(tempDir, "data", "resources", "indextts", "voice_cache.json")));
	}

	[Fact]
	public async Task 合成时从模板音频自动解析音色()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-indextts-resolve-{Guid.NewGuid():N}");
		string template = Path.Combine(tempDir, "voice.wav");
		Directory.CreateDirectory(tempDir);
		File.WriteAllBytes(template, MinimalWav());
		AppStoragePaths paths = new(tempDir);
		_config.Set("indextts_template_audio", new ConfigValue.Text(template));

		CaptureHandler handler = new(request => RouteByRequest(request, "uspeech:from-template"));
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config, paths);

		// 第一次：上传克隆，随后合成
		EncodedAudio audio = await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions(), CancellationToken.None);

		Assert.NotEmpty(audio.Bytes);
		Assert.Equal(new Uri("https://api.modelverse.cn/v1/audio/speech"), handler.LastUri);
	}

	[Fact]
	public async Task 缓存音色未过期时不重复上传()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-indextts-cache-{Guid.NewGuid():N}");
		string template = Path.Combine(tempDir, "voice.wav");
		Directory.CreateDirectory(tempDir);
		File.WriteAllBytes(template, MinimalWav());
		AppStoragePaths paths = new(tempDir);
		_config.Set("indextts_template_audio", new ConfigValue.Text(template));

		int uploadCount = 0;
		CaptureHandler handler = new(request =>
		{
			bool isUpload = request.RequestUri?.AbsolutePath.EndsWith("/audio/voice/upload", StringComparison.Ordinal) ?? false;
			if (isUpload) uploadCount++;
			return RouteByRequest(request, "uspeech:cached-456");
		});
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config, paths);

		// 第一次合成触发上传；第二次同模板应命中缓存不重复上传
		await provider.SynthesizeAsync("第一次", new TtsSynthesizeOptions(), CancellationToken.None);
		await provider.SynthesizeAsync("第二次", new TtsSynthesizeOptions(), CancellationToken.None);

		Assert.Equal(1, uploadCount);
	}

	[Fact]
	public async Task 音色过期后自动续期且缓存key不漂移()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-indextts-renew-{Guid.NewGuid():N}");
		string template = Path.Combine(tempDir, "voice.wav");
		Directory.CreateDirectory(tempDir);
		File.WriteAllBytes(template, MinimalWav());
		AppStoragePaths paths = new(tempDir);
		_config.Set("indextts_template_audio", new ConfigValue.Text(template));

		int uploadCount = 0;
		CaptureHandler handler = new(request =>
		{
			bool isUpload = request.RequestUri?.AbsolutePath.EndsWith("/audio/voice/upload", StringComparison.Ordinal) ?? false;
			if (isUpload) uploadCount++;
			return RouteByRequest(request, $$"""uspeech:renew-{{uploadCount}}""");
		});
		using HttpClient client = new(handler);

		// 第一次正常克隆
		IndexTtsProvider first = new(client, _config, paths);
		await first.CloneVoiceAsync(template, CancellationToken.None);
		Assert.Equal(1, uploadCount);

		// 把缓存条目时间改成已过期，再用新 provider 合成 → 应自动续期（第二次上传）
		string cacheFile = Path.Combine(tempDir, "data", "resources", "indextts", "voice_cache.json");
		IndexTtsProvider.IndexTtsVoiceCache agedCache =
			System.Text.Json.JsonSerializer.Deserialize<IndexTtsProvider.IndexTtsVoiceCache>(File.ReadAllText(cacheFile))!;
		foreach (IndexTtsProvider.IndexTtsVoiceEntry entry in agedCache.Voices.Values)
		{
			entry.UploadUnixSeconds = DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeSeconds();
		}
		File.WriteAllText(cacheFile, System.Text.Json.JsonSerializer.Serialize(agedCache));

		IndexTtsProvider second = new(client, _config, paths);
		await second.SynthesizeAsync("过期后", new TtsSynthesizeOptions(), CancellationToken.None);

		Assert.Equal(2, uploadCount);
		// 缓存仍是同一把 key（源路径），没有新增漂移条目
		string after = File.ReadAllText(cacheFile);
		IndexTtsProvider.IndexTtsVoiceCache parsed = System.Text.Json.JsonSerializer.Deserialize<IndexTtsProvider.IndexTtsVoiceCache>(after)!;
		Assert.Single(parsed.Voices);
	}

	[Fact]
	public async Task 换模板后试听使用新音色()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-indextts-swap-{Guid.NewGuid():N}");
		string templateA = Path.Combine(tempDir, "voice_a.wav");
		string templateB = Path.Combine(tempDir, "voice_b.wav");
		Directory.CreateDirectory(tempDir);
		File.WriteAllBytes(templateA, MinimalWav());
		File.WriteAllBytes(templateB, MinimalWav());
		AppStoragePaths paths = new(tempDir);

		var uploadedVoices = new List<string>();
		int voiceCounter = 0;
		CaptureHandler handler = new(request =>
		{
			bool isUpload = request.RequestUri?.AbsolutePath.EndsWith("/audio/voice/upload", StringComparison.Ordinal) ?? false;
			if (isUpload)
			{
				voiceCounter++;
				return JsonResponse(HttpStatusCode.OK, $$"""{"id": "uspeech:swapped-{{voiceCounter}}"}""");
			}
			return WavResponse();
		});
		using HttpClient client = new(handler);

		// 模板 A → 合成
		_config.Set("indextts_template_audio", new ConfigValue.Text(templateA));
		IndexTtsProvider providerA = new(client, _config, paths);
		await providerA.SynthesizeAsync("第一次试听", new TtsSynthesizeOptions(), CancellationToken.None);
		Assert.Equal(1, voiceCounter);

		// 换模板 B → 合成（即使 tts_voice 残留旧值也必须用新模板克隆的音色）
		_config.Set("indextts_template_audio", new ConfigValue.Text(templateB));
		_config.Set("tts_voice", new ConfigValue.Text("uspeech:stale-old"));
		IndexTtsProvider providerB = new(client, _config, paths);
		await providerB.SynthesizeAsync("第二次试听", new TtsSynthesizeOptions(), CancellationToken.None);

		Assert.Equal(2, voiceCounter);
		// 缓存里两条不同源路径的条目（A 与 B 各自独立）
		IndexTtsProvider.IndexTtsVoiceCache cache =
			System.Text.Json.JsonSerializer.Deserialize<IndexTtsProvider.IndexTtsVoiceCache>(
				File.ReadAllText(Path.Combine(tempDir, "data", "resources", "indextts", "voice_cache.json")))!;
		Assert.Equal(2, cache.Voices.Count);
	}

	[Fact]
	public async Task 同路径不同大小写在Windows语义下命中同一缓存()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-indextts-case-{Guid.NewGuid():N}");
		string template = Path.Combine(tempDir, "voice.wav");
		Directory.CreateDirectory(tempDir);
		File.WriteAllBytes(template, MinimalWav());
		// 构造一个大写路径的副本：Windows 下与 template 是同一文件，Linux 下是另一个文件
		string upperCaseTemplate = Path.Combine(tempDir, "VOICE.WAV");
		File.WriteAllBytes(upperCaseTemplate, MinimalWav());
		AppStoragePaths paths = new(tempDir);

		int uploadCount = 0;
		CaptureHandler handler = new(request =>
		{
			bool isUpload = request.RequestUri?.AbsolutePath.EndsWith("/audio/voice/upload", StringComparison.Ordinal) ?? false;
			if (isUpload) uploadCount++;
			return RouteByRequest(request, "uspeech:case-1");
		});
		using HttpClient client = new(handler);

		// 第一次用原大小写模板路径上传克隆
		_config.Set("indextts_template_audio", new ConfigValue.Text(template));
		IndexTtsProvider providerA = new(client, _config, paths);
		await providerA.SynthesizeAsync("测试A", new TtsSynthesizeOptions(), CancellationToken.None);
		Assert.Equal(1, uploadCount);

		// 换不同大小写的同路径
		_config.Set("indextts_template_audio", new ConfigValue.Text(upperCaseTemplate));
		IndexTtsProvider providerB = new(client, _config, paths);
		await providerB.SynthesizeAsync("测试B", new TtsSynthesizeOptions(), CancellationToken.None);

		if (OperatingSystem.IsWindows())
		{
			Assert.Equal(1, uploadCount); // Windows: 大小写不敏感，命中缓存
		}
		else
		{
			Assert.Equal(2, uploadCount); // 其他平台: 大小写敏感，视为不同模板
		}
	}

	[Fact]
	public void VoiceService能够创建IndexTtsProvider()
	{
		using HttpClient client = new(new CaptureHandler(_ => WavResponse()));
		using VoiceService service = new(client, _config, null, () => null);
		Assert.IsType<IndexTtsProvider>(service.CreateProvider("indextts"));
	}

	[Fact]
	public async Task VoiceService换模板后合成缓存使用新音色()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-indextts-service-swap-{Guid.NewGuid():N}");
		string templateA = Path.Combine(tempDir, "voice_a.wav");
		string templateB = Path.Combine(tempDir, "voice_b.wav");
		Directory.CreateDirectory(tempDir);
		File.WriteAllBytes(templateA, MinimalWav());
		File.WriteAllBytes(templateB, MinimalWav());
		AppStoragePaths paths = new(tempDir);

		int voiceCounter = 0;
		var synthVoices = new List<string>();
		AsyncCaptureHandler handler = new(async request =>
		{
			bool isUpload = request.RequestUri?.AbsolutePath.EndsWith("/audio/voice/upload", StringComparison.Ordinal) ?? false;
			if (isUpload)
			{
				voiceCounter++;
				return JsonResponse(HttpStatusCode.OK, $$"""{"id": "uspeech:svc-{{voiceCounter}}"}""");
			}
			// 记录合成请求里实际使用的 voice
			string bodyText = request.Content is null ? "" : await request.Content.ReadAsStringAsync(CancellationToken.None);
			var body = System.Text.Json.Nodes.JsonNode.Parse(bodyText) as System.Text.Json.Nodes.JsonObject;
			synthVoices.Add(body?["voice"]?.GetValue<string>() ?? "");
			return WavResponse();
		});
		using HttpClient client = new(handler);
		using VoiceService service = new(client, _config, null, () => null, paths);
		_config.Set("tts_provider", new ConfigValue.Text("indextts"));
		_config.Set("tts_base_url", new ConfigValue.Text("https://api.modelverse.cn/v1"));

		// 模板 A 试听
		_config.Set("indextts_template_audio", new ConfigValue.Text(templateA));
		_config.Set("tts_voice", new ConfigValue.Text("uspeech:stale"));
		await service.SynthesizeAsync("试听", null, CancellationToken.None);
		Assert.Equal(1, voiceCounter);

		// 换模板 B 再试听：必须用新 voice 合成（tts_voice 残留旧值不影响）
		_config.Set("indextts_template_audio", new ConfigValue.Text(templateB));
		await service.SynthesizeAsync("试听", null, CancellationToken.None);
		Assert.Equal(2, voiceCounter);
		Assert.Equal("uspeech:svc-2", synthVoices[^1]);
	}

	public void Dispose()
	{
		_database.Dispose();
		try { File.Delete(_dbPath); } catch (IOException) { }
	}

	private static HttpResponseMessage WavResponse() => new(HttpStatusCode.OK)
	{
		Content = new ByteArrayContent(MinimalWav())
		{
			Headers = {ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav")},
		},
	};

	private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
	{
		Content = new StringContent(json, Encoding.UTF8, "application/json"),
	};

	/// <summary>44 字节最小合法 WAV (静音)。</summary>
	private static byte[] MinimalWav()
	{
		const int dataLength = 0;
		using MemoryStream stream = new(44 + dataLength);
		using (BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true))
		{
			writer.Write(Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(36 + dataLength);
			writer.Write(Encoding.ASCII.GetBytes("WAVE"));
			writer.Write(Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16);
			writer.Write((short)1);
			writer.Write((short)1);
			writer.Write(24000);
			writer.Write(48000);
			writer.Write((short)2);
			writer.Write((short)16);
			writer.Write(Encoding.ASCII.GetBytes("data"));
			writer.Write(dataLength);
		}
		return stream.ToArray();
	}

	/// <summary>按请求 URL 分流：上传接口返回 JSON id，合成接口返回 WAV。</summary>
	private static HttpResponseMessage RouteByRequest(HttpRequestMessage request, string voiceId)
	{
		bool isUpload = request.RequestUri?.AbsolutePath.EndsWith("/audio/voice/upload", StringComparison.Ordinal) ?? false;
		return isUpload ? JsonResponse(HttpStatusCode.OK, $$"""{"id": "{{voiceId}}"}""") : WavResponse();
	}

	private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		public Uri? LastUri { get; private set; }
		public string? AuthorizationScheme { get; private set; }
		public string? AuthorizationParameter { get; private set; }
		public string? LastBody { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			LastUri = request.RequestUri;
			AuthorizationScheme = request.Headers.Authorization?.Scheme;
			AuthorizationParameter = request.Headers.Authorization?.Parameter;
			LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
			return responder(request);
		}
	}

	private sealed class AsyncCaptureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			responder(request);
	}
}
