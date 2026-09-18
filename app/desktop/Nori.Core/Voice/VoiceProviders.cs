using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Network;

namespace Nori.Core.Voice;

/// <summary>
/// HttpClient 发送边界: 把传输层失败 (TCP/DNS/超时) 归一化为 VoiceProviderException。
///
/// 用户主动取消原样上抛; HTTP 状态码失败由各 Provider 在拿到响应后按 HttpRejected 分类。
/// </summary>
internal static class VoiceHttp
{
	public static async Task<HttpResponseMessage> SendAsync(
		HttpClient client,
		HttpRequestMessage request,
		string provider,
		CancellationToken cancellationToken)
	{
		try
		{
			return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException exception)
		{
			throw new VoiceProviderException(provider, VoiceFailureKind.Timeout, "语音服务请求超时", exception);
		}
		catch (HttpRequestException exception)
		{
			throw new VoiceProviderException(provider, VoiceFailureKind.Network, "语音服务网络请求失败", exception);
		}
	}
}

/// <summary>OpenAI 兼容 TTS 适配器 (/v1/audio/speech)。</summary>
public sealed class OpenAiTtsProvider(HttpClient httpClient, ConfigStore config) : ITtsProvider
{
	public string Name => "openai";

	public async Task<EncodedAudio> SynthesizeAsync(string text, TtsSynthesizeOptions options, CancellationToken cancellationToken)
	{
		string baseUrl = (config.GetStringOr("tts_base_url", "") is {Length: > 0} saved ? saved : "https://api.openai.com/v1").Trim().TrimEnd('/');
		string apiKey = config.GetStringOr("tts_api_key", "");
		string model = config.GetStringOr("tts_model", "tts-1").Trim();
		if (model.Length == 0) model = "tts-1";
		if (!baseUrl.EndsWith("/audio/speech", StringComparison.OrdinalIgnoreCase)) baseUrl += "/audio/speech";

		JsonObject payload = new()
		{
			["model"] = model,
			["input"] = text,
			["voice"] = options.Voice is {Length: > 0} ? options.Voice : "nova",
			["speed"] = options.Speed,
			["response_format"] = "wav",
		};

		using HttpRequestMessage request = new(HttpMethod.Post, baseUrl)
		{
			Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

		using HttpResponseMessage response = await VoiceHttp.SendAsync(httpClient, request, Name, cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			string error = await ReadErrorAsync(response, cancellationToken);
			throw new VoiceProviderException(
				Name, VoiceFailureKind.HttpRejected,
				$"OpenAI TTS 请求失败: HTTP {(int)response.StatusCode} {error}",
				httpStatusCode: (int)response.StatusCode);
		}
		return await VoiceHttpContent.ReadAudioAsync(response.Content, cancellationToken);
	}

	private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		try
		{
			byte[] bytes = await VoiceHttpContent.ReadBytesAsync(response.Content, cancellationToken, allowEmpty: true);
			return Encoding.UTF8.GetString(bytes);
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException)
		{
			return response.ReasonPhrase ?? "";
		}
	}
}

/// <summary>
/// 自定义 HTTP TTS 适配器。
///
/// POST tts_base_url, 请求体 {text, voice, speed}, 响应为带 Content-Type 的音频字节。
/// 原生后端仅支持 WAV，自定义服务须在服务端配置为输出 PCM WAV；
/// 此处仍保留真实编码与 MIME，让显式选择 WebView 的兼容路径继续支持其他音频格式。
/// </summary>
public sealed class CustomHttpTtsProvider(HttpClient httpClient, ConfigStore config) : ITtsProvider
{
	public string Name => "custom";

	public async Task<EncodedAudio> SynthesizeAsync(string text, TtsSynthesizeOptions options, CancellationToken cancellationToken)
	{
		string endpoint = config.GetStringOr("tts_base_url", "").Trim();
		if (endpoint.Length == 0) throw new InvalidOperationException("未配置自定义 TTS 请求端点 URL");
		if (!ChatEndpoint.TryCreateHttpUri(endpoint, out Uri? endpointUri) || endpointUri is null)
			throw new InvalidOperationException("自定义 TTS 请求端点 URL 格式无效");

		UrlAccessPolicy.EnsureAllowed(endpointUri, allowPrivate: true);
		JsonObject payload = new()
		{
			["text"] = text,
			["voice"] = options.Voice,
			["speed"] = options.Speed,
		};

		using HttpRequestMessage request = new(HttpMethod.Post, endpointUri)
		{
			Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		using HttpResponseMessage response = await VoiceHttp.SendAsync(httpClient, request, Name, cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new VoiceProviderException(
				Name, VoiceFailureKind.HttpRejected,
				$"自定义 TTS 请求失败: HTTP {(int)response.StatusCode}",
				httpStatusCode: (int)response.StatusCode);
		}
		return await VoiceHttpContent.ReadAudioAsync(response.Content, cancellationToken);
	}
}

/// <summary>
/// GPT-SoVITS API 适配器 (官方 FastAPI /tts 端点; 本地端点显式允许私网)。
/// </summary>
public sealed class GptSoVitsTtsProvider(HttpClient httpClient, ConfigStore config) : ITtsProvider
{
	public string Name => "gpt_sovits";

	public async Task<EncodedAudio> SynthesizeAsync(string text, TtsSynthesizeOptions options, CancellationToken cancellationToken)
	{
		string baseUrl = (config.GetStringOr("gptsovits_base_url", "") is {Length: > 0} saved ? saved : "http://127.0.0.1:9880").Trim().TrimEnd('/');
		string refAudio = config.GetStringOr("gptsovits_ref_audio", "");
		string promptText = config.GetStringOr("gptsovits_prompt_text", "");
		string promptLang = config.GetStringOr("gptsovits_prompt_lang", "zh");
		string url = baseUrl.EndsWith("/tts", StringComparison.OrdinalIgnoreCase) ? baseUrl : $"{baseUrl}/tts";

		if (!ChatEndpoint.TryCreateHttpUri(baseUrl, out Uri? baseUri) || baseUri is null)
			throw new InvalidOperationException("GPT-SoVITS Base URL 格式无效");
		UrlAccessPolicy.EnsureAllowed(baseUri, allowPrivate: true);
		JsonObject payload = new()
		{
			["text"] = text,
			["text_lang"] = "zh",
			["ref_audio_path"] = refAudio,
			["prompt_text"] = promptText,
			["prompt_lang"] = promptLang,
			["speed_factor"] = options.Speed,
			["media_type"] = "wav",
			["streaming_mode"] = false,
		};

		// 有些 GPT-SoVITS 版本只实现 GET；POST 非成功或返回空内容时必须继续降级。
		EncodedAudio? post = null;
		try
		{
			post = await PostAsync(url, payload, cancellationToken);
		}
		catch (VoiceProviderException exception) when (exception.FailureKind == VoiceFailureKind.Network)
		{
			// 网络错误也尝试旧版 GET 路径；取消/超时仍由上层直接处理。
		}
		if (post is not null) return post;

		return await GetAsync(url, text, refAudio, promptText, promptLang, options.Speed, cancellationToken);
	}

	private async Task<EncodedAudio?> PostAsync(string url, JsonObject payload, CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new(HttpMethod.Post, url)
		{
			Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		using HttpResponseMessage response = await VoiceHttp.SendAsync(httpClient, request, Name, cancellationToken);
		if (!response.IsSuccessStatusCode) return null;

		string mime = AudioMime.Validate(response.Content.Headers.ContentType?.ToString());
		byte[] bytes = await VoiceHttpContent.ReadBytesAsync(response.Content, cancellationToken, allowEmpty: true);
		return bytes.Length == 0 ? null : AudioMime.ValidateEncoded(bytes, mime);
	}

	private async Task<EncodedAudio> GetAsync(
		string url, string text, string refAudio, string promptText, string promptLang, double speed, CancellationToken cancellationToken)
	{
		List<string> query =
		[
			$"text={Uri.EscapeDataString(text)}",
			"text_lang=zh",
			$"ref_audio_path={Uri.EscapeDataString(refAudio)}",
			$"prompt_text={Uri.EscapeDataString(promptText)}",
			$"prompt_lang={Uri.EscapeDataString(promptLang)}",
			$"speed_factor={speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
			"media_type=wav",
			"streaming_mode=false",
		];
		using HttpRequestMessage request = new(HttpMethod.Get, $"{url}?{string.Join("&", query)}");
		using HttpResponseMessage response = await VoiceHttp.SendAsync(httpClient, request, Name, cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new VoiceProviderException(
				Name, VoiceFailureKind.HttpRejected,
				$"GPT-SoVITS API 合成失败: HTTP {(int)response.StatusCode}",
				httpStatusCode: (int)response.StatusCode);
		}
		return await VoiceHttpContent.ReadAudioAsync(response.Content, cancellationToken);
	}
}

/// <summary>OpenAI Whisper 录音识别 (/v1/audio/transcriptions)。</summary>
public sealed class WhisperSttProvider(HttpClient httpClient, ConfigStore config)
{
	/// <summary>识别一段录音，保留 MediaRecorder 的 MIME 与文件名。</summary>
	public async Task<string> TranscribeAsync(RecordedAudio audio, CancellationToken cancellationToken)
	{
		RecordedAudio validated = AudioMime.ValidateRecorded(audio.Bytes, audio.Mime, audio.FileName);
		string baseUrl = (config.GetStringOr("stt_base_url", "") is {Length: > 0} saved ? saved : "https://api.openai.com/v1").Trim().TrimEnd('/');
		if (!baseUrl.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase)) baseUrl += "/audio/transcriptions";
		string apiKey = config.GetStringOr("stt_api_key", "");

		using MultipartFormDataContent form = new();
		ByteArrayContent file = new(validated.Bytes);
		file.Headers.TryAddWithoutValidation("Content-Type", validated.Mime);
		form.Add(file, "file", validated.FileName);
		form.Add(new StringContent("whisper-1"), "model");
		form.Add(new StringContent("zh"), "language");

		using HttpRequestMessage request = new(HttpMethod.Post, baseUrl) {Content = form};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
		using HttpResponseMessage response = await VoiceHttp.SendAsync(httpClient, request, "whisper", cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			string error = await ReadTextAsync(response.Content, cancellationToken);
			throw new VoiceProviderException(
				"whisper", VoiceFailureKind.HttpRejected,
				$"Whisper 识别失败: HTTP {(int)response.StatusCode} {error}",
				httpStatusCode: (int)response.StatusCode);
		}

		string json = await ReadTextAsync(response.Content, cancellationToken);
		JsonNode? data = JsonNode.Parse(json);
		return data?["text"]?.GetValue<string>() ?? "";
	}

	private static async Task<string> ReadTextAsync(HttpContent content, CancellationToken cancellationToken)
	{
		byte[] bytes = await VoiceHttpContent.ReadBytesAsync(content, cancellationToken, allowEmpty: true);
		return Encoding.UTF8.GetString(bytes);
	}
}
