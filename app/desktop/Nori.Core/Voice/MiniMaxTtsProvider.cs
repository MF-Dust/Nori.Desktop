using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;

namespace Nori.Core.Voice;

/// <summary>MiniMax 同步 T2A HTTP 适配器 (/v1/t2a_v2)。</summary>
public sealed class MiniMaxTtsProvider(HttpClient httpClient, ConfigStore config) : ITtsProvider
{
	private const string DefaultBaseUrl = "https://api.minimaxi.com/v1";
	private const string DefaultModel = "speech-2.8-turbo";
	private const string DefaultVoice = "male-qn-qingse";

	public string Name => "minimax";

	public async Task<EncodedAudio> SynthesizeAsync(
		string text,
		TtsSynthesizeOptions options,
		CancellationToken cancellationToken)
	{
		string baseUrl = (config.GetStringOr("tts_base_url", "") is {Length: > 0} saved ? saved : DefaultBaseUrl)
			.Trim().TrimEnd('/');
		string endpoint = FormatEndpoint(baseUrl);
		string apiKey = config.GetStringOr("tts_api_key", "");
		if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("未配置 MiniMax TTS API Key");

		JsonObject payload = new()
		{
			["model"] = DefaultModel,
			["text"] = text,
			["stream"] = false,
			["voice_setting"] = new JsonObject
			{
				["voice_id"] = options.Voice is {Length: > 0} ? options.Voice : DefaultVoice,
				["speed"] = options.Speed,
			},
			["audio_setting"] = new JsonObject
			{
				// 非流式接口支持 WAV，直接满足原生播放器的 PCM 契约。
				["format"] = "wav",
			},
			["output_format"] = "hex",
		};

		using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
		{
			Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

		using HttpResponseMessage response = await VoiceHttp.SendAsync(httpClient, request, Name, cancellationToken);

		byte[] responseBytes = await VoiceHttpContent.ReadBytesAsync(response.Content, cancellationToken, allowEmpty: true);
		string raw = Encoding.UTF8.GetString(responseBytes);
		JsonNode? body = TryParse(raw);

		if (!response.IsSuccessStatusCode)
		{
			throw new VoiceProviderException(
				Name, VoiceFailureKind.HttpRejected,
				$"MiniMax TTS 请求失败: HTTP {(int)response.StatusCode}{DiagnosticSuffix(body, raw)}",
				httpStatusCode: (int)response.StatusCode);
		}

		int statusCode = ReadStatusCode(body);
		if (statusCode != 0)
		{
			throw new VoiceProviderException(
				Name, VoiceFailureKind.ProviderRejected,
				$"MiniMax TTS 返回错误: status_code={statusCode}{DiagnosticSuffix(body, raw)}",
				providerStatusCode: statusCode);
		}

		string? audioHex = body?["data"]?["audio"]?.GetValue<string>();
		if (string.IsNullOrWhiteSpace(audioHex))
		{
			throw new VoiceProviderException(
				Name, VoiceFailureKind.InvalidResponse,
				$"MiniMax TTS 响应缺少 data.audio{DiagnosticSuffix(body, raw)}");
		}
		if (audioHex.Length > VoiceAudioLimits.MaxBytes * 2)
		{
			throw new VoiceProviderException(Name, VoiceFailureKind.InvalidResponse, "MiniMax TTS 音频超过 32MiB 限制");
		}

		byte[] audioBytes;
		try
		{
			audioBytes = Convert.FromHexString(audioHex);
		}
		catch (FormatException exception)
		{
			throw new VoiceProviderException(
				Name, VoiceFailureKind.InvalidResponse,
				$"MiniMax TTS data.audio 不是有效的 hex 数据{DiagnosticSuffix(body, raw)}", exception);
		}

		string format = body?["extra_info"]?["audio_format"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "wav";
		string mime = format switch
		{
			"mp3" => "audio/mpeg",
			"wav" => "audio/wav",
			"flac" => "audio/flac",
			_ => throw new VoiceProviderException(
				Name, VoiceFailureKind.InvalidResponse, $"MiniMax TTS 返回了不支持的音频格式: {format}"),
		};
		return AudioMime.ValidateEncoded(audioBytes, mime);
	}

	private static string FormatEndpoint(string baseUrl)
	{
		if (baseUrl.Length == 0) throw new InvalidOperationException("MiniMax TTS Base URL 不能为空");
		if (baseUrl.EndsWith("/t2a_v2", StringComparison.OrdinalIgnoreCase)) return baseUrl;
		if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return $"{baseUrl}/t2a_v2";
		return $"{baseUrl}/v1/t2a_v2";
	}

	private static JsonNode? TryParse(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return null;
		try { return JsonNode.Parse(raw); }
		catch (JsonException) { return null; }
	}

	private static int ReadStatusCode(JsonNode? body)
	{
		if (body?["base_resp"]?["status_code"] is not JsonValue value) return 0;
		if (value.TryGetValue(out int intValue)) return intValue;
		if (value.TryGetValue(out long longValue) && longValue is >= int.MinValue and <= int.MaxValue) return (int)longValue;
		return -1;
	}

	private static string DiagnosticSuffix(JsonNode? body, string raw)
	{
		string? statusMsg = body?["base_resp"]?["status_msg"]?.GetValue<string>();
		string? traceId = body?["trace_id"]?.GetValue<string>();
		List<string> details = [];
		if (!string.IsNullOrWhiteSpace(statusMsg)) details.Add($"message={statusMsg}");
		if (!string.IsNullOrWhiteSpace(traceId)) details.Add($"trace_id={traceId}");
		if (details.Count == 0 && !string.IsNullOrWhiteSpace(raw))
		{
			string compact = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
			details.Add(compact.Length > 200 ? compact[..200] : compact);
		}
		return details.Count == 0 ? "" : $", {string.Join(", ", details)}";
	}
}
