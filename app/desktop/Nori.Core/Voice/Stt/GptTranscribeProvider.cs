using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;
using Nori.Core.Network;

namespace Nori.Core.Voice.Stt;

/// <summary>
/// 新一代 OpenAI 整段文件转写适配器 (<c>/audio/transcriptions</c>)。
///
/// 默认使用 <c>gpt-transcribe</c>，支持可选 <c>language</c> 与自定义兼容端点模型覆盖；
/// 当兼容端点拒绝默认模型时自动降级到 <c>whisper-1</c>。
/// </summary>
public sealed class GptTranscribeProvider(HttpClient httpClient, ConfigStore config) : ISttProvider
{
	/// <summary>默认新一代转写模型。</summary>
	public const string DefaultModel = "gpt-transcribe";

	/// <summary>兼容降级回退模型。</summary>
	public const string FallbackModel = "whisper-1";

	private const int MaxErrorBodyBytes = 16 * 1024;

	private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
	private readonly ConfigStore _config = config ?? throw new ArgumentNullException(nameof(config));

	/// <inheritdoc />
	public string ProviderId => "gpt_transcribe";

	/// <inheritdoc />
	public SttMode Mode => SttMode.BatchCloud;

	/// <inheritdoc />
	public SttProviderCapabilities Capabilities { get; } = new()
	{
		SupportsBatch = true,
		SupportsRealtimePcm = false,
		SupportsIncrementalDelta = false,
		SupportsLanguageHint = true,
		SupportsLocalOffline = false,
		SupportsHardwareAcceleration = false,
		RequiredSampleRate = 16000,
		RequiredChannels = 1,
	};

	/// <inheritdoc />
	public Task<IStreamingSttSession> OpenStreamingSessionAsync(SttRequestOptions options, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("当前语音识别提供商仅支持整段音频转写，不支持实时流式识别");

	/// <inheritdoc />
	public async Task<string> TranscribeAsync(RecordedAudio audio, SttRequestOptions options, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(audio);
		ArgumentNullException.ThrowIfNull(options);

		RecordedAudio validated = AudioMime.ValidateRecorded(audio.Bytes, audio.Mime, audio.FileName);
		Uri endpoint = ResolveEndpointUri(options.BaseUrl);
		string apiKey = SttSanitizer.NormalizeApiKey(
			!string.IsNullOrWhiteSpace(options.ApiKey)
				? options.ApiKey
				: _config.GetStringOr("stt_api_key", ""));

		string? configuredModel = !string.IsNullOrWhiteSpace(options.Model)
			? options.Model.Trim()
			: _config.GetStringOr("stt_model", "") is { Length: > 0 } savedModel
				? savedModel.Trim()
				: null;

		string primaryModel = configuredModel ?? DefaultModel;
		bool allowFallback = configuredModel is null || string.Equals(primaryModel, DefaultModel, StringComparison.OrdinalIgnoreCase);
		string? language = string.IsNullOrWhiteSpace(options.Language) ? null : options.Language.Trim();

		AttemptResult firstAttempt = await SendTranscriptionAsync(endpoint, apiKey, validated, primaryModel, language, cancellationToken).ConfigureAwait(false);
		if (firstAttempt.Success)
		{
			return firstAttempt.Transcript!;
		}

		if (allowFallback && IsUnsupportedModelFailure(firstAttempt.StatusCode, firstAttempt.RawError))
		{
			AttemptResult fallbackAttempt = await SendTranscriptionAsync(endpoint, apiKey, validated, FallbackModel, language, cancellationToken).ConfigureAwait(false);
			if (fallbackAttempt.Success)
			{
				return fallbackAttempt.Transcript!;
			}

			throw BuildHttpException(fallbackAttempt.StatusCode, fallbackAttempt.RawError, apiKey);
		}

		throw BuildHttpException(firstAttempt.StatusCode, firstAttempt.RawError, apiKey);
	}

	private async Task<AttemptResult> SendTranscriptionAsync(
		Uri endpoint,
		string apiKey,
		RecordedAudio audio,
		string model,
		string? language,
		CancellationToken cancellationToken)
	{
		using MultipartFormDataContent form = new();
		ByteArrayContent fileContent = new(audio.Bytes);
		fileContent.Headers.TryAddWithoutValidation("Content-Type", audio.Mime);
		form.Add(fileContent, "file", audio.FileName);
		form.Add(new StringContent(model), "model");
		if (language is not null)
		{
			form.Add(new StringContent(language), "language");
		}

		using HttpRequestMessage request = new(HttpMethod.Post, endpoint) { Content = form };
		if (!string.IsNullOrEmpty(apiKey))
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
		}

		using HttpResponseMessage response = await VoiceHttp.SendAsync(_httpClient, request, ProviderId, cancellationToken, apiKey).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			string rawError = await ReadCappedTextSafeAsync(response.Content, cancellationToken).ConfigureAwait(false);
			return new AttemptResult(false, null, response.StatusCode, rawError);
		}

		byte[] bytes = await VoiceHttpContent.ReadBytesAsync(response.Content, cancellationToken, allowEmpty: true).ConfigureAwait(false);
		string json = System.Text.Encoding.UTF8.GetString(bytes);

		JsonNode? root;
		try
		{
			root = JsonNode.Parse(json);
		}
		catch (System.Text.Json.JsonException)
		{
			throw new VoiceProviderException(ProviderId, VoiceFailureKind.InvalidResponse, "语音识别响应不是有效的 JSON");
		}

		if (root?["text"] is not JsonValue textValue || !textValue.TryGetValue(out string? transcript))
		{
			throw new VoiceProviderException(ProviderId, VoiceFailureKind.InvalidResponse, "语音识别响应缺少 text 字段");
		}

		return new AttemptResult(true, transcript ?? "", response.StatusCode, "");
	}

	private Uri ResolveEndpointUri(string? optionBaseUrl)
	{
		string rawBase = !string.IsNullOrWhiteSpace(optionBaseUrl)
			? optionBaseUrl
			: _config.GetStringOr("stt_base_url", "") is { Length: > 0 } saved
				? saved
				: OpenAiRealtimeSttUriBuilder.DefaultBaseUrl;

		return OpenAiRealtimeSttUriBuilder.BuildBatchUri(rawBase);
	}

	private static bool IsUnsupportedModelFailure(HttpStatusCode statusCode, string rawError)
	{
		if (statusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity))
		{
			return false;
		}

		if (string.IsNullOrWhiteSpace(rawError)) return false;

		if (TryExtractErrorFields(rawError, out string? code, out string? message))
		{
			if (string.Equals(code, "model_not_found", StringComparison.OrdinalIgnoreCase)) return true;
			if (!string.IsNullOrWhiteSpace(message) && ContainsModelMismatchHint(message)) return true;
		}

		return ContainsModelMismatchHint(rawError);
	}

	private static bool ContainsModelMismatchHint(string text) =>
		text.Contains("model_not_found", StringComparison.OrdinalIgnoreCase)
		|| text.Contains("unknown model", StringComparison.OrdinalIgnoreCase)
		|| text.Contains("invalid model", StringComparison.OrdinalIgnoreCase)
		|| text.Contains("unsupported model", StringComparison.OrdinalIgnoreCase)
		|| (text.Contains("model", StringComparison.OrdinalIgnoreCase)
			&& (text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
				|| text.Contains("not found", StringComparison.OrdinalIgnoreCase)
				|| text.Contains("not supported", StringComparison.OrdinalIgnoreCase)
				|| text.Contains("unsupported", StringComparison.OrdinalIgnoreCase)));

	private VoiceProviderException BuildHttpException(HttpStatusCode statusCode, string rawError, string apiKey)
	{
		string summary = TryExtractErrorFields(rawError, out _, out string? message) && !string.IsNullOrWhiteSpace(message)
			? message
			: rawError;
		string safeError = SttSanitizer.RedactError(summary, apiKey);
		string text = safeError.Length > 0
			? $"语音识别请求失败: HTTP {(int)statusCode} {safeError}"
			: $"语音识别请求失败: HTTP {(int)statusCode}";

		return new VoiceProviderException(
			ProviderId,
			VoiceFailureKind.HttpRejected,
			text,
			httpStatusCode: (int)statusCode);
	}

	private static bool TryExtractErrorFields(string rawError, out string? code, out string? message)
	{
		code = null;
		message = null;
		if (string.IsNullOrWhiteSpace(rawError)) return false;

		try
		{
			JsonNode? node = JsonNode.Parse(rawError);
			if (node is null) return false;
			code = node["error"]?["code"]?.GetValue<string>();
			message = node["error"]?["message"]?.GetValue<string>() ?? node["message"]?.GetValue<string>();
			return code is not null || message is not null;
		}
		catch
		{
			return false;
		}
	}

	private static async Task<string> ReadCappedTextSafeAsync(HttpContent content, CancellationToken cancellationToken)
	{
		try
		{
			return await UrlAccessPolicy.ReadCappedTextAsync(content, MaxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return "";
		}
	}

	private sealed record AttemptResult(bool Success, string? Transcript, HttpStatusCode StatusCode, string RawError);
}
