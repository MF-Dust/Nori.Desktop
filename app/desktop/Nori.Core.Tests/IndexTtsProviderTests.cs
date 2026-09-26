using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;
using Nori.Core.Voice;
using Nori.Core.Voice.Audio;

namespace Nori.Core.Tests;

/// <summary>IndexTTS-2 (优云智算) OpenAI 兼容请求映射与错误处理测试。</summary>
public class IndexTtsProviderTests : IDisposable
{
	private readonly TempDatabase _tempDatabase = new("nori-indextts");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public IndexTtsProviderTests()
	{
		_database = NoriDatabase.Open(_tempDatabase.Path);
		_config = new ConfigStore(_database);
		_config.InitDefaults("0.1.0");
		_config.Set("tts_provider", new ConfigValue.Text("indextts"));
		_config.Set("tts_api_key", new ConfigValue.Text("test-secret"));
	}

	[Fact]
	public async Task 默认配置正确映射请求到Modelverse音频端点()
	{
		HttpTestHandler handler = new(_ => WavResponse());
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
		HttpTestHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = "uspeech:abc"}, CancellationToken.None);

		Assert.Equal(new Uri("https://api.modelverse.cn/v1/audio/speech"), handler.LastUri);
	}

	[Fact]
	public async Task 配置模型名优先于默认值()
	{
		_config.Set("tts_model", new ConfigValue.Text("IndexTeam/IndexTTS-2-chinese"));
		HttpTestHandler handler = new(_ => WavResponse());
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
		HttpTestHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			provider.SynthesizeAsync("测试", new TtsSynthesizeOptions(), CancellationToken.None));

		Assert.Contains("IndexTTS-2", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 未配置音色ID时本地报错()
	{
		HttpTestHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = ""}, CancellationToken.None));

		Assert.Contains("音色", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task HTTP错误携带错误消息()
	{
		HttpTestHandler handler = new(_ => JsonResponse(HttpStatusCode.Unauthorized, """
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

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task 扩展字段按配置决定是否进入请求体(bool configured)
	{
		if (configured)
		{
			_config.Set("indextts_emo_text", new ConfigValue.Text("开心又期待"));
			_config.Set("indextts_sample_rate", new ConfigValue.Text("44100"));
			_config.Set("indextts_gain", new ConfigValue.Text("1.5"));
		}
		HttpTestHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = "uspeech:abc"}, CancellationToken.None);

		JsonNode body = JsonNode.Parse(handler.LastBody!)!;
		if (configured)
		{
			Assert.Equal("开心又期待", body["emo_text"]?.GetValue<string>());
			Assert.Equal(44100, body["sample_rate"]?.GetValue<int>());
			Assert.Equal(1.5, body["gain"]?.GetValue<double>());
		}
		else
		{
			Assert.Null(body["emo_text"]);
			Assert.Null(body["sample_rate"]);
			Assert.Null(body["gain"]);
			Assert.Null(body["interval_silence"]);
		}
	}

	[Theory]
	[MemberData(nameof(EmotionMappingCases))]
	public async Task 情绪参数按选项与配置映射(
		string emotion,
		string? configAlpha,
		string? configEmoText,
		int? expectedMethod,
		string? expectedText,
		double? expectedWeight)
	{
		if (configAlpha is not null) _config.Set("indextts_emo_alpha", new ConfigValue.Text(configAlpha));
		if (configEmoText is not null) _config.Set("indextts_emo_text", new ConfigValue.Text(configEmoText));
		HttpTestHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions {Voice = "uspeech:abc", EmotionText = emotion}, CancellationToken.None);

		JsonNode body = JsonNode.Parse(handler.LastBody!)!;
		if (expectedMethod is null)
		{
			Assert.Null(body["emo_control_method"]);
			Assert.Null(body["emo_text"]);
			Assert.Null(body["emo_weight"]);
		}
		else
		{
			Assert.Equal(expectedMethod.Value, body["emo_control_method"]?.GetValue<int>());
			Assert.Equal(expectedText, body["emo_text"]?.GetValue<string>());
			Assert.Equal(expectedWeight!.Value, body["emo_weight"]?.GetValue<double>());
		}
	}

	public static TheoryData<string, string?, string?, int?, string?, double?> EmotionMappingCases => new()
	{
		{ "happy", null, null, 3, "happy", 0.3 },
		{ "neutral", null, null, null, null, null },
		{ "sad", "0.5", null, 3, "sad", 0.5 },
		{ "happy", null, "angry", 3, "happy", 0.3 },
	};

	[Fact]
	public async Task 克隆音色上传并返回VoiceId()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-indextts-upload-{Guid.NewGuid():N}");
		string template = Path.Combine(tempDir, "voice.wav");
		Directory.CreateDirectory(tempDir);
		File.WriteAllBytes(template, MinimalWav());
		AppStoragePaths paths = new(tempDir);

		HttpTestHandler handler = new(_ => JsonResponse(HttpStatusCode.OK, """{"id": "uspeech:uploaded-123"}"""));
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

		HttpTestHandler handler = new(request => RouteByRequest(request, "uspeech:from-template"));
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
		HttpTestHandler handler = new(request =>
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
		HttpTestHandler handler = new(request =>
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
		HttpTestHandler handler = new(request =>
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
		HttpTestHandler handler = new(request =>
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
	public async Task 完整Speech端点克隆时会归一化到VoiceUpload且使用配置模型()
	{
		string tempDir = CreateTempDir();
		string template = Path.Combine(tempDir, "voice.wav");
		File.WriteAllBytes(template, MinimalWav());
		_config.Set("tts_base_url", new ConfigValue.Text("https://api.modelverse.cn/v1/audio/speech"));
		_config.Set("tts_model", new ConfigValue.Text("custom/index-tts-model"));

		HttpTestHandler handler = new(_ => JsonResponse(HttpStatusCode.OK, """{"id":"uspeech:clone"}"""));
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config, new AppStoragePaths(tempDir));

		await provider.CloneVoiceAsync(template, CancellationToken.None);

		Assert.Equal(new Uri("https://api.modelverse.cn/v1/audio/voice/upload"), handler.LastUri);
		Assert.Contains("custom/index-tts-model", handler.LastBody!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 原模板删除后过期音色仍从本地存档续期()
	{
		string tempDir = CreateTempDir();
		string template = Path.Combine(tempDir, "voice.wav");
		File.WriteAllBytes(template, MinimalWav());
		AppStoragePaths paths = new(tempDir);
		_config.Set("indextts_template_audio", new ConfigValue.Text(template));

		int uploadCount = 0;
		HttpTestHandler handler = new(request =>
		{
			bool isUpload = request.RequestUri?.AbsolutePath.EndsWith("/audio/voice/upload", StringComparison.Ordinal) == true;
			if (isUpload)
			{
				uploadCount++;
				return JsonResponse(HttpStatusCode.OK, $$"""{"id":"uspeech:renew-{{uploadCount}}"}""");
			}
			return WavResponse();
		});
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config, paths);

		Assert.Equal("uspeech:renew-1", await provider.CloneVoiceAsync(template, CancellationToken.None));

		IndexTtsProvider.IndexTtsVoiceCache cache = System.Text.Json.JsonSerializer.Deserialize<IndexTtsProvider.IndexTtsVoiceCache>(
			File.ReadAllText(paths.IndexTtsCachePath))!;
		IndexTtsProvider.IndexTtsVoiceEntry entry = Assert.Single(cache.Voices.Values);
		string archivedFile = entry.ArchiveFile;
		entry.UploadUnixSeconds = DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeSeconds();
		File.WriteAllText(paths.IndexTtsCachePath, System.Text.Json.JsonSerializer.Serialize(cache));
		File.Delete(template);

		string renewed = await provider.ResolveTemplateVoiceAsync(CancellationToken.None);

		Assert.Equal("uspeech:renew-2", renewed);
		Assert.Equal(2, uploadCount);
		Assert.True(File.Exists(archivedFile));
	}

	[Fact]
	public async Task 情绪强度零值按零发送()
	{
		_config.Set("indextts_emo_alpha", new ConfigValue.Text("0"));
		HttpTestHandler handler = new(_ => WavResponse());
		using HttpClient client = new(handler);
		IndexTtsProvider provider = new(client, _config);

		await provider.SynthesizeAsync(
			"测试",
			new TtsSynthesizeOptions {Voice = "uspeech:test", EmotionText = "happy"},
			CancellationToken.None);

		JsonNode body = JsonNode.Parse(handler.LastBody!)!;
		Assert.Equal(0d, body["emo_weight"]?.GetValue<double>());
	}

	[Fact]
	public async Task 情绪强度变化会生成新的合成缓存身份()
	{
		int synthCount = 0;
		HttpTestHandler handler = new(request =>
		{
			if (request.RequestUri?.AbsolutePath.EndsWith("/audio/speech", StringComparison.Ordinal) == true) synthCount++;
			return WavResponse();
		});
		using HttpClient client = new(handler);
		using VoiceService service = new(client, _config, null, () => null);
		TtsSynthesizeOptions options = new() {Voice = "uspeech:test", EmotionText = "happy"};

		_config.Set("indextts_emo_alpha", new ConfigValue.Text("0.2"));
		await service.SynthesizeAsync("同一句话", options, CancellationToken.None);
		_config.Set("indextts_emo_alpha", new ConfigValue.Text("0.8"));
		await service.SynthesizeAsync("同一句话", options, CancellationToken.None);

		Assert.Equal(2, synthCount);
	}

	[Fact]
	public void VoiceService能够创建IndexTtsProvider()
	{
		using HttpClient client = new(new HttpTestHandler(_ => WavResponse()));
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
		HttpTestHandler? handler = null;
		handler = new HttpTestHandler(async (request, _) =>
		{
			bool isUpload = request.RequestUri?.AbsolutePath.EndsWith("/audio/voice/upload", StringComparison.Ordinal) ?? false;
			if (isUpload)
			{
				voiceCounter++;
				return JsonResponse(HttpStatusCode.OK, $$"""{"id": "uspeech:svc-{{voiceCounter}}"}""");
			}
			// 记录合成请求里实际使用的 voice
			var body = JsonNode.Parse(handler!.LastBody!) as JsonObject;
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
		_tempDatabase.Dispose();
	}

	private static string CreateTempDir()
	{
		string path = Path.Combine(Path.GetTempPath(), $"nori-indextts-files-{Guid.NewGuid():N}");
		Directory.CreateDirectory(path);
		return path;
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
}
