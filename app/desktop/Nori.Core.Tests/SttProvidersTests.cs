using System.Net;
using System.Text;
using System.Text.Json;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Voice;
using Nori.Core.Voice.Stt;

namespace Nori.Core.Tests;

/// <summary>GptTranscribeProvider、WhisperSttProvider 与 ISttProvider 契约测试。</summary>
public sealed class SttProvidersTests : IDisposable
{
	private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nori-stt-providers-{Guid.NewGuid():N}.db");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public SttProvidersTests()
	{
		_database = NoriDatabase.Open(_dbPath);
		_config = new ConfigStore(_database);
		_config.InitDefaults("0.1.0");
	}

	[Fact]
	public async Task 各Provider能力声明与不支持模式抛中文NotSupportedException()
	{
		OpenAiRealtimeSttProvider realtime = new(_config);
		Assert.Equal(SttMode.RealtimeCloud, realtime.Mode);
		Assert.True(realtime.Capabilities.SupportsRealtimePcm);
		Assert.True(realtime.Capabilities.SupportsIncrementalDelta);
		Assert.False(realtime.Capabilities.SupportsBatch);
		Assert.Equal(24000, realtime.Capabilities.RequiredSampleRate);
		Assert.Equal(1, realtime.Capabilities.RequiredChannels);

		RecordedAudio sample = new([1, 2, 3, 4], "audio/wav", "test.wav");
		NotSupportedException rtEx = await Assert.ThrowsAsync<NotSupportedException>(() =>
			realtime.TranscribeAsync(sample, new SttRequestOptions()));
		Assert.Contains("实时流式", rtEx.Message, StringComparison.Ordinal);

		using HttpClient client = new(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
		GptTranscribeProvider gpt = new(client, _config);
		Assert.Equal(SttMode.BatchCloud, gpt.Mode);
		Assert.True(gpt.Capabilities.SupportsBatch);
		Assert.False(gpt.Capabilities.SupportsRealtimePcm);
		await Assert.ThrowsAsync<NotSupportedException>(() => gpt.OpenStreamingSessionAsync(new SttRequestOptions()));

		WhisperSttProvider whisper = new(client, _config);
		Assert.Equal("whisper", whisper.ProviderId);
		Assert.Equal(SttMode.BatchCloud, whisper.Mode);
		Assert.True(whisper.Capabilities.SupportsBatch);
		Assert.False(whisper.Capabilities.SupportsRealtimePcm);
		await Assert.ThrowsAsync<NotSupportedException>(() => whisper.OpenStreamingSessionAsync(new SttRequestOptions()));
	}

	[Fact]
	public async Task GptTranscribeProvider默认使用GptTranscribe且支持可选Language与自定义端点覆盖()
	{
		List<CapturedRequest> requests = [];
		StubHandler handler = new(req =>
		{
			requests.Add(req);
			return JsonResponse(HttpStatusCode.OK, "{\"text\":\"转写成功\"}");
		});
		using HttpClient client = new(handler);
		GptTranscribeProvider provider = new(client, _config);

		RecordedAudio audio = new([0x52, 0x49, 0x46, 0x46], "audio/wav", "clip.wav");
		string text1 = await provider.TranscribeAsync(audio, new SttRequestOptions
		{
			ApiKey = "sk-gpt-secret",
		});
		Assert.Equal("转写成功", text1);

		CapturedRequest first = Assert.Single(requests);
		Assert.Equal(new Uri("https://api.openai.com/v1/audio/transcriptions"), first.Uri);
		Assert.Equal("Bearer sk-gpt-secret", first.Authorization);
		Assert.Equal("gpt-transcribe", first.FormFields["model"]);
		Assert.False(first.FormFields.ContainsKey("language"));
		Assert.Equal("clip.wav", first.FileName);

		requests.Clear();
		_config.Set("stt_base_url", new ConfigValue.Text("https://compat.example.com/custom/v1/"));
		string text2 = await provider.TranscribeAsync(audio, new SttRequestOptions
		{
			Model = "custom-transcribe-v2",
			Language = "ja",
			ApiKey = "sk-gpt-secret",
		});
		Assert.Equal("转写成功", text2);

		CapturedRequest second = Assert.Single(requests);
		Assert.Equal(new Uri("https://compat.example.com/custom/v1/audio/transcriptions"), second.Uri);
		Assert.Equal("custom-transcribe-v2", second.FormFields["model"]);
		Assert.Equal("ja", second.FormFields["language"]);
	}

	[Theory]
	[InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"model_not_found\",\"message\":\"The model `gpt-transcribe` does not exist\"}}")]
	[InlineData(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"Unsupported model: gpt-transcribe\"}}")]
	[InlineData(HttpStatusCode.UnprocessableEntity, "{\"message\":\"unknown model gpt-transcribe\"}")]
	public async Task GptTranscribeProvider遇兼容端点不支持默认模型时自动降级Whisper1(
		HttpStatusCode statusCode,
		string errorBody)
	{
		List<string?> requestedModels = [];
		StubHandler handler = new(req =>
		{
			string? model = req.FormFields.GetValueOrDefault("model");
			requestedModels.Add(model);
			return model == "gpt-transcribe"
				? JsonResponse(statusCode, errorBody)
				: JsonResponse(HttpStatusCode.OK, "{\"text\":\"降级识别成功\"}");
		});
		using HttpClient client = new(handler);
		GptTranscribeProvider provider = new(client, _config);

		RecordedAudio audio = new([1, 2, 3, 4], "audio/wav", "voice.wav");
		string text = await provider.TranscribeAsync(audio, new SttRequestOptions
		{
			BaseUrl = "http://127.0.0.1:8080/v1",
			ApiKey = "sk-local",
			Language = "zh",
		});

		Assert.Equal("降级识别成功", text);
		Assert.Equal(["gpt-transcribe", "whisper-1"], requestedModels);
	}

	[Fact]
	public async Task GptTranscribeProvider显式指定自定义模型时不盲目降级且错误脱敏()
	{
		const string secret = "sk-never-leak-key-777";
		List<string?> requestedModels = [];
		StubHandler handler = new(req =>
		{
			requestedModels.Add(req.FormFields.GetValueOrDefault("model"));
			return JsonResponse(
				HttpStatusCode.BadRequest,
				JsonSerializer.Serialize(new
				{
					error = new
					{
						code = "model_not_found",
						message = $"invalid model with key {secret}",
					},
				}));
		});
		using HttpClient client = new(handler);
		GptTranscribeProvider provider = new(client, _config);

		RecordedAudio audio = new([1, 2, 3, 4], "audio/wav", "voice.wav");
		VoiceProviderException ex = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			provider.TranscribeAsync(audio, new SttRequestOptions
			{
				Model = "third-party-custom-model",
				ApiKey = secret,
			}));

		Assert.Equal(["third-party-custom-model"], requestedModels);
		Assert.Equal(VoiceFailureKind.HttpRejected, ex.FailureKind);
		Assert.Equal(400, ex.HttpStatusCode);
		Assert.DoesNotContain(secret, ex.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GptTranscribeProvider与WhisperSttProvider在HTTP与传输异常下全链脱敏不泄露ApiKey()
	{
		const string secret = "sk-batch-secret-654321";
		RecordedAudio audio = new([1, 2, 3, 4], "audio/wav", "voice.wav");

		// 传输层 HttpRequestException（含嵌套 InnerException）不得把 secret 留到 Exception.ToString()
		StubHandler transportFailHandler = new(_ =>
			throw new HttpRequestException(
				$"connect failed with Bearer {secret}",
				new IOException($"socket error with {secret}")));
		using HttpClient transportFailClient = new(transportFailHandler);
		GptTranscribeProvider gptTransportFail = new(transportFailClient, _config);
		WhisperSttProvider whisperTransportFail = new(transportFailClient, _config);

		VoiceProviderException gptEx = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			gptTransportFail.TranscribeAsync(audio, new SttRequestOptions { ApiKey = secret }));
		Assert.Equal(VoiceFailureKind.Network, gptEx.FailureKind);
		Assert.DoesNotContain(secret, gptEx.ToString(), StringComparison.Ordinal);

		VoiceProviderException whisperEx = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			whisperTransportFail.TranscribeAsync(audio, new SttRequestOptions { ApiKey = secret }));
		Assert.Equal(VoiceFailureKind.Network, whisperEx.FailureKind);
		Assert.DoesNotContain(secret, whisperEx.ToString(), StringComparison.Ordinal);

		// 内部超时 OperationCanceledException 同样不得泄露 secret
		StubHandler timeoutHandler = new(_ =>
			throw new OperationCanceledException($"timed out for {secret}"));
		using HttpClient timeoutClient = new(timeoutHandler);
		GptTranscribeProvider gptTimeout = new(timeoutClient, _config);
		VoiceProviderException timeoutEx = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			gptTimeout.TranscribeAsync(audio, new SttRequestOptions { ApiKey = secret }));
		Assert.Equal(VoiceFailureKind.Timeout, timeoutEx.FailureKind);
		Assert.DoesNotContain(secret, timeoutEx.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task 批量STT端点校验先解析Uri再拼路径且拒绝非环回明文与凭据URL()
	{
		RecordedAudio audio = new([1, 2, 3, 4], "audio/wav", "voice.wav");
		List<Uri?> uris = [];
		StubHandler handler = new(req =>
		{
			uris.Add(req.Uri);
			return JsonResponse(HttpStatusCode.OK, "{\"text\":\"ok\"}");
		});
		using HttpClient client = new(handler);
		GptTranscribeProvider provider = new(client, _config);

		await provider.TranscribeAsync(audio, new SttRequestOptions
		{
			BaseUrl = "https://compat.example.com/v1/?api-version=2024-06-01",
			ApiKey = "sk-test",
		});
		Assert.Equal(
			new Uri("https://compat.example.com/v1/audio/transcriptions?api-version=2024-06-01"),
			Assert.Single(uris));

		uris.Clear();
		await provider.TranscribeAsync(audio, new SttRequestOptions
		{
			BaseUrl = "http://localhost:8080/v1",
			ApiKey = "sk-test",
		});
		Assert.Equal(new Uri("http://localhost:8080/v1/audio/transcriptions"), Assert.Single(uris));

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			provider.TranscribeAsync(audio, new SttRequestOptions { BaseUrl = "http://example.com/v1" }));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			provider.TranscribeAsync(audio, new SttRequestOptions { BaseUrl = "http://192.168.1.50:8080/v1" }));

		const string urlSecret = "sk-url-embedded-secret";
		InvalidOperationException userInfoEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			provider.TranscribeAsync(audio, new SttRequestOptions { BaseUrl = $"https://{urlSecret}@api.openai.com/v1" }));
		Assert.DoesNotContain(urlSecret, userInfoEx.ToString(), StringComparison.Ordinal);

		InvalidOperationException querySecretEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			provider.TranscribeAsync(audio, new SttRequestOptions { BaseUrl = $"https://api.openai.com/v1?api_key={urlSecret}" }));
		Assert.DoesNotContain(urlSecret, querySecretEx.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GptTranscribeProvider鉴权失败直接拒绝且响应格式错误报告InvalidResponse()
	{
		const string secret = "sk-auth-secret-888";
		StubHandler authFailHandler = new(_ =>
			JsonResponse(
				HttpStatusCode.Unauthorized,
				JsonSerializer.Serialize(new
				{
					error = new
					{
						message = $"bad key {secret}",
					},
				})));
		using HttpClient authClient = new(authFailHandler);
		GptTranscribeProvider authProvider = new(authClient, _config);

		RecordedAudio audio = new([1, 2, 3, 4], "audio/wav", "voice.wav");
		VoiceProviderException authEx = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			authProvider.TranscribeAsync(audio, new SttRequestOptions { ApiKey = secret }));
		Assert.Equal(VoiceFailureKind.HttpRejected, authEx.FailureKind);
		Assert.Equal(401, authEx.HttpStatusCode);
		Assert.DoesNotContain(secret, authEx.Message, StringComparison.Ordinal);

		StubHandler invalidJsonHandler = new(_ => JsonResponse(HttpStatusCode.OK, "{\"unexpected\":123}"));
		using HttpClient invalidClient = new(invalidJsonHandler);
		GptTranscribeProvider invalidProvider = new(invalidClient, _config);

		VoiceProviderException invalidEx = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			invalidProvider.TranscribeAsync(audio, new SttRequestOptions { ApiKey = secret }));
		Assert.Equal(VoiceFailureKind.InvalidResponse, invalidEx.FailureKind);
	}

	[Fact]
	public async Task WhisperSttProvider保持原有Whisper1行为且实现ISttProvider并对错误脱敏()
	{
		const string secret = "sk-whisper-secret-321";
		_config.Set("stt_api_key", new ConfigValue.Text(secret));

		List<CapturedRequest> requests = [];
		StubHandler handler = new(req =>
		{
			requests.Add(req);
			return req.FormFields.GetValueOrDefault("language") == "en"
				? JsonResponse(HttpStatusCode.InternalServerError, $"upstream error with {secret}")
				: JsonResponse(HttpStatusCode.OK, "{\"text\":\"你好世界\"}");
		});
		using HttpClient client = new(handler);
		WhisperSttProvider whisper = new(client, _config);

		RecordedAudio audio = new([1, 2, 3, 4], "audio/wav", "legacy.wav");
		string legacyResult = await whisper.TranscribeAsync(audio, CancellationToken.None);
		Assert.Equal("你好世界", legacyResult);

		CapturedRequest legacyReq = Assert.Single(requests);
		Assert.Equal("whisper-1", legacyReq.FormFields["model"]);
		Assert.Equal("zh", legacyReq.FormFields["language"]);
		Assert.Equal($"Bearer {secret}", legacyReq.Authorization);

		ISttProvider asInterface = whisper;
		VoiceProviderException err = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			asInterface.TranscribeAsync(audio, new SttRequestOptions { Language = "en" }));
		Assert.Equal("whisper", err.Provider);
		Assert.Equal(VoiceFailureKind.HttpRejected, err.FailureKind);
		Assert.DoesNotContain(secret, err.ToString(), StringComparison.Ordinal);
	}

	public void Dispose()
	{
		_database.Dispose();
		try { File.Delete(_dbPath); } catch (IOException) { }
	}

	private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json) =>
		new(code)
		{
			Content = new StringContent(json, Encoding.UTF8, "application/json"),
		};

	private sealed record CapturedRequest(
		Uri? Uri,
		string? Authorization,
		Dictionary<string, string> FormFields,
		string? FileName);

	private sealed class StubHandler(Func<CapturedRequest, HttpResponseMessage> responder) : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Dictionary<string, string> fields = new(StringComparer.Ordinal);
			string? fileName = null;

			if (request.Content is MultipartFormDataContent multipart)
			{
				foreach (HttpContent part in multipart)
				{
					string? name = part.Headers.ContentDisposition?.Name?.Trim('"');
					if (string.IsNullOrEmpty(name)) continue;

					if (name == "file")
					{
						fileName = part.Headers.ContentDisposition?.FileName?.Trim('"');
					}
					else
					{
						fields[name] = await part.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
					}
				}
			}

			string? auth = request.Headers.Authorization is null
				? null
				: $"{request.Headers.Authorization.Scheme} {request.Headers.Authorization.Parameter}";

			return responder(new CapturedRequest(request.RequestUri, auth, fields, fileName));
		}
	}
}
