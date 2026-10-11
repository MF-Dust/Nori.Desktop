using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;
using Nori.Core.Network;
using Nori.Core.Security;

namespace Nori.Core.Chat.Images;

/// <summary>一张生成好的图片。</summary>
/// <param name="Name">本地文件名 (位于 <see cref="GeneratedImageStore"/> 目录)。</param>
/// <param name="Markdown">在消息中引用这张图片的 Markdown。</param>
/// <param name="RevisedPrompt">服务端改写后的提示词 (部分模型提供)。</param>
public sealed record GeneratedImage(string Name, string Markdown, string? RevisedPrompt);

/// <summary>图像生成能力。</summary>
public interface IImageGenerator
{
	/// <summary>当前配置是否可用。</summary>
	bool IsConfigured { get; }

	/// <summary>按提示词生成一张图片并保存到本地。</summary>
	Task<GeneratedImage> GenerateAsync(string prompt, string? aspectRatio, CancellationToken cancellationToken = default);
}

/// <summary>
/// 图像生成服务。
///
/// 支持两种协议：
/// · openai —— <c>POST {base}/images/generations</c> (OpenAI gpt-image / dall-e 及大量兼容服务)；
/// · google —— Gemini 图像模型走 <c>models/{model}:generateContent</c>，Imagen 模型走 <c>models/{model}:predict</c>。
///
/// 地址由用户配置，与对话服务一样走模型请求的 HTTP 客户端；错误信息只保留服务端说明的脱敏摘要。
/// </summary>
public sealed class ImageGenerationService(HttpClient http, Func<AiImageSettings> settings, GeneratedImageStore store) : IImageGenerator
{
	/// <summary>图像接口响应体积上限 (base64 编码后的图片较大)。</summary>
	private const long MaxResponseBytes = 48L * 1024 * 1024;

	/// <summary>单次生成的超时：图像模型普遍比对话慢。</summary>
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

	private readonly HttpClient _http = http;
	private readonly Func<AiImageSettings> _settings = settings;
	private readonly GeneratedImageStore _store = store;

	/// <inheritdoc />
	public bool IsConfigured => _settings().IsConfigured;

	/// <inheritdoc />
	public async Task<GeneratedImage> GenerateAsync(string prompt, string? aspectRatio, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("图像描述不能为空");
		AiImageSettings config = _settings();
		if (!config.IsConfigured) throw new InvalidOperationException("尚未配置图像生成服务 (地址、密钥或模型缺失)");
		// 工具参数优先；否则用设置里的默认值。OpenAI 设置填的是像素尺寸时原样使用，不换算成比例。
		string? requested = NormalizeAspectRatio(aspectRatio);
		bool pixelSize = System.Text.RegularExpressions.Regex.IsMatch(config.Size.Trim().ToLowerInvariant(), @"^\d{2,5}x\d{2,5}$");
		string ratio = requested
			?? (config.Provider != "google" && pixelSize ? null : NormalizeAspectRatio(config.Size))
			?? "";

		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(Timeout);
		try
		{
			(byte[] bytes, string? mime, string? revised) = config.Provider == "google"
				? await GenerateGoogleAsync(config, prompt.Trim(), ratio, timeout.Token).ConfigureAwait(false)
				: await GenerateOpenAiAsync(config, prompt.Trim(), ratio, timeout.Token).ConfigureAwait(false);
			string name = await _store.SaveAsync(bytes, mime, cancellationToken).ConfigureAwait(false);
			return new GeneratedImage(name, GeneratedImageStore.ToMarkdown(name, prompt), revised);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException($"图像生成超时 ({(int)Timeout.TotalSeconds}s)");
		}
	}

	private async Task<(byte[] Bytes, string? Mime, string? Revised)> GenerateOpenAiAsync(
		AiImageSettings config, string prompt, string ratio, CancellationToken cancellationToken)
	{
		JsonObject body = new()
		{
			["model"] = config.Model,
			["prompt"] = prompt,
			["n"] = 1,
		};
		if (OpenAiSize(config, ratio) is { } size) body["size"] = size;
		// dall-e 默认返回临时 URL；gpt-image 系列不接受 response_format，且总是返回 base64。
		if (config.Model.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase)) body["response_format"] = "b64_json";

		using HttpRequestMessage request = new(HttpMethod.Post, OpenAiEndpoint(config.BaseUrl));
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
		request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		JsonNode root = await SendAsync(request, config.ApiKey, cancellationToken).ConfigureAwait(false);

		// OpenAI: data[0].b64_json / data[0].url；部分兼容服务 (如 SiliconFlow) 使用 images[0].url。
		JsonNode? item = root["data"]?.AsArray().FirstOrDefault() ?? root["images"]?.AsArray().FirstOrDefault();
		if (item is null) throw new InvalidOperationException("图像服务响应中没有图片");
		string? revised = item["revised_prompt"]?.GetValue<string>();
		if (item["b64_json"]?.GetValue<string>() is {Length: > 0} base64) return (DecodeBase64(base64), null, revised);
		if (item["url"]?.GetValue<string>() is {Length: > 0} url)
		{
			(byte[] bytes, string? mime) = await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
			return (bytes, mime, revised);
		}
		throw new InvalidOperationException("图像服务响应中没有图片数据");
	}

	private async Task<(byte[] Bytes, string? Mime, string? Revised)> GenerateGoogleAsync(
		AiImageSettings config, string prompt, string ratio, CancellationToken cancellationToken)
	{
		string model = config.Model.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? config.Model["models/".Length..] : config.Model;
		bool imagen = model.StartsWith("imagen", StringComparison.OrdinalIgnoreCase);
		JsonObject body;
		if (imagen)
		{
			JsonObject parameters = new() {["sampleCount"] = 1};
			if (ratio.Length > 0) parameters["aspectRatio"] = ratio;
			body = new JsonObject
			{
				["instances"] = new JsonArray(new JsonObject {["prompt"] = prompt}),
				["parameters"] = parameters,
			};
		}
		else
		{
			JsonObject generation = new() {["responseModalities"] = new JsonArray("TEXT", "IMAGE")};
			if (ratio.Length > 0) generation["imageConfig"] = new JsonObject {["aspectRatio"] = ratio};
			body = new JsonObject
			{
				["contents"] = new JsonArray(new JsonObject
				{
					["role"] = "user",
					["parts"] = new JsonArray(new JsonObject {["text"] = prompt}),
				}),
				["generationConfig"] = generation,
			};
		}

		string endpoint = $"{GoogleRoot(config.BaseUrl)}/models/{Uri.EscapeDataString(model)}:{(imagen ? "predict" : "generateContent")}";
		using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
		request.Headers.TryAddWithoutValidation("x-goog-api-key", config.ApiKey);
		request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		JsonNode root = await SendAsync(request, config.ApiKey, cancellationToken).ConfigureAwait(false);

		if (imagen)
		{
			JsonNode? prediction = root["predictions"]?.AsArray().FirstOrDefault();
			if (prediction?["bytesBase64Encoded"]?.GetValue<string>() is {Length: > 0} encoded)
				return (DecodeBase64(encoded), prediction["mimeType"]?.GetValue<string>(), null);
			throw new InvalidOperationException("图像服务没有返回图片 (提示词可能被安全策略拦截)");
		}

		StringBuilder text = new();
		foreach (JsonNode? part in root["candidates"]?.AsArray().FirstOrDefault()?["content"]?["parts"]?.AsArray() ?? [])
		{
			JsonNode? inline = part?["inlineData"] ?? part?["inline_data"];
			if (inline?["data"]?.GetValue<string>() is {Length: > 0} data)
				return (DecodeBase64(data), (inline["mimeType"] ?? inline["mime_type"])?.GetValue<string>(), text.Length > 0 ? text.ToString() : null);
			if (part?["text"]?.GetValue<string>() is {Length: > 0} chunk && part["thought"]?.GetValue<bool>() != true) text.Append(chunk);
		}
		string reason = root["candidates"]?.AsArray().FirstOrDefault()?["finishReason"]?.GetValue<string>() ?? "";
		throw new InvalidOperationException(reason.Length > 0
			? $"图像服务没有返回图片 ({SensitiveDataRedactor.Redact(reason)})"
			: "图像服务没有返回图片 (模型可能不支持图像输出)");
	}

	private async Task<JsonNode> SendAsync(HttpRequestMessage request, string apiKey, CancellationToken cancellationToken)
	{
		request.Headers.UserAgent.ParseAdd(ChatClientFactory.UserAgent);
		HttpResponseMessage response;
		try
		{
			response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		}
		catch (HttpRequestException exception)
		{
			throw new InvalidOperationException($"无法连接图像服务: {SensitiveDataRedactor.Redact(exception.Message.Replace(apiKey, "***", StringComparison.Ordinal))}", exception);
		}
		using (response)
		{
			if (!response.IsSuccessStatusCode)
			{
				string detail = await UrlAccessPolicy.ReadSafeProviderErrorAsync(response.Content, cancellationToken).ConfigureAwait(false);
				detail = detail.Replace(apiKey, "***", StringComparison.Ordinal);
				throw new InvalidOperationException(detail.Length > 0
					? $"图像生成失败 (HTTP {(int)response.StatusCode}): {detail}"
					: $"图像生成失败 (HTTP {(int)response.StatusCode})");
			}
			string json = await UrlAccessPolicy.ReadCappedTextAsync(response.Content, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
			try
			{
				return JsonNode.Parse(json) ?? throw new InvalidOperationException("图像服务响应为空");
			}
			catch (JsonException exception)
			{
				throw new InvalidOperationException("图像服务响应格式无法解析", exception);
			}
		}
	}

	private async Task<(byte[] Bytes, string? Mime)> DownloadAsync(string url, CancellationToken cancellationToken)
	{
		if (!ChatEndpoint.TryCreateHttpUri(url, out Uri? uri)) throw new InvalidOperationException("图像服务返回的图片地址无效");
		// 地址来自用户配置的服务，与模型请求一样允许内网 (自建服务)；只限 http/https。
		UrlAccessPolicy.EnsureAllowed(uri!, allowPrivate: true);
		using HttpResponseMessage response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"下载生成的图片失败 (HTTP {(int)response.StatusCode})");
		await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using MemoryStream buffer = new();
		byte[] chunk = new byte[81920];
		while (true)
		{
			int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
			if (read <= 0) break;
			if (buffer.Length + read > GeneratedImageStore.MaxImageBytes) throw new InvalidOperationException("生成的图片超过大小上限");
			buffer.Write(chunk, 0, read);
		}
		return (buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
	}

	private static byte[] DecodeBase64(string value)
	{
		string data = value;
		int comma = data.IndexOf(',', StringComparison.Ordinal);
		if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0) data = data[(comma + 1)..];
		try
		{
			return Convert.FromBase64String(data);
		}
		catch (FormatException exception)
		{
			throw new InvalidOperationException("图像服务返回的图片编码无效", exception);
		}
	}

	/// <summary>OpenAI 兼容服务的图像端点：在已知的协议路径前截断后拼接。</summary>
	internal static string OpenAiEndpoint(string baseUrl)
	{
		string root = ChatEndpoint.CreateHttpUri(baseUrl.Trim()).ToString().TrimEnd('/');
		foreach (string suffix in new[] {"/images/generations", "/chat/completions", "/responses"})
		{
			if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) root = root[..^suffix.Length];
		}
		return root + "/images/generations";
	}

	/// <summary>Gemini 端点根：去掉模型路径，缺少版本时补 v1beta。</summary>
	internal static string GoogleRoot(string baseUrl)
	{
		string root = ChatEndpoint.CreateHttpUri(baseUrl.Trim()).ToString().TrimEnd('/');
		int models = root.IndexOf("/models", StringComparison.OrdinalIgnoreCase);
		if (models >= 0) root = root[..models];
		if (!root.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase) && !root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root += "/v1beta";
		return root;
	}

	/// <summary>把尺寸 / 比例统一成 W:H 比例；auto 或无法识别时返回 null。</summary>
	internal static string? NormalizeAspectRatio(string? value)
	{
		string text = value?.Trim().ToLowerInvariant() ?? "";
		if (text.Length == 0 || text == "auto") return null;
		string[] parts = text.Split(':', 'x', '×', '*');
		if (parts.Length != 2 || !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height) || width <= 0 || height <= 0) return null;
		if (text.Contains(':')) return $"{width}:{height}";
		// 像素尺寸换算为最接近的常用比例。
		double target = (double)width / height;
		string[] ratios = ["1:1", "3:2", "2:3", "4:3", "3:4", "16:9", "9:16", "21:9"];
		return ratios.MinBy(ratio =>
		{
			string[] pair = ratio.Split(':');
			return Math.Abs(Math.Log(double.Parse(pair[0], System.Globalization.CultureInfo.InvariantCulture) / double.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture)) - Math.Log(target));
		});
	}

	/// <summary>OpenAI 的 size 参数：用户填了像素尺寸就原样使用，否则按比例选择支持的尺寸。</summary>
	internal static string? OpenAiSize(AiImageSettings config, string ratio)
	{
		string configured = config.Size.Trim().ToLowerInvariant();
		bool dalle3 = config.Model.StartsWith("dall-e-3", StringComparison.OrdinalIgnoreCase);
		if (ratio.Length == 0)
		{
			return System.Text.RegularExpressions.Regex.IsMatch(configured, @"^\d{2,5}x\d{2,5}$") ? configured : null;
		}
		string[] pair = ratio.Split(':');
		double value = double.Parse(pair[0], System.Globalization.CultureInfo.InvariantCulture) / double.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture);
		if (Math.Abs(value - 1) < 0.15) return "1024x1024";
		if (value > 1) return dalle3 ? "1792x1024" : "1536x1024";
		return dalle3 ? "1024x1792" : "1024x1536";
	}
}
