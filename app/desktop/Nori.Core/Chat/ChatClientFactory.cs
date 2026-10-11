using LlmTornado;
using LlmTornado.Code;
using LlmTornado.Code.Vendor;
using Microsoft.Extensions.AI;
using Nori.Core.Chat.Tornado;

namespace Nori.Core.Chat;

/// <summary>基于 LlmTornado 的 IChatClient 工厂；四种协议共用一套转换与传输。</summary>
public static class ChatClientFactory
{
	/// <summary>
	/// 发给模型服务的客户端标识。
	///
	/// 客户端库默认写的是它自己（例如 OpenAI SDK 的 <c>OpenAI/2.5.0 (.NET 10.0; Windows …)</c>）——
	/// 那句话声称发请求的是别的客户端，而不是 Nori。两个后果：
	///
	/// · **网关按 User-Agent 拦。** 实测：同一把密钥、同一条地址，curl 打是 200，
	///   桌宠打是 403，正文只有一句 "Your request was blocked."。拦的是 Cloudflare 的
	///   托管规则（它把这个串当成 AI 爬虫），而异常里只有一个状态码，看不出是谁拦的 ——
	///   这种错查起来很贵，因为所有"配置对不对"的排查方向都是错的。
	/// · 用户翻自建网关的日志时，认不出哪些请求是桌宠发的。
	///
	/// 覆盖它不改变任何行为，只是如实说明自己是谁。
	/// </summary>
	internal static string UserAgent { get; } = $"Nori.Desktop/{SanitizeVersion(ProductVersion.Current)}";

	/// <summary>按 provider 创建保持 ILlmAdapter 契约的适配器。</summary>
	public static ILlmAdapter Create(LlmProvider provider, HttpClient httpClient) =>
		new ChatClientLlmAdapter(provider, httpClient);

	/// <summary>
	/// 版本号裁成 User-Agent 的 token 能收的字符。
	///
	/// <see cref="ProductVersion.Current"/> 是完整的 informational version，带得动
	/// <c>+</c> 和构建元数据，而那些字符在 token 里非法 —— 直接 ParseAdd 会抛，
	/// 且抛在每一次请求上。全裁光时退回 Dev，不能产出一个尾巴是斜杠的空标识。
	/// </summary>
	internal static string SanitizeVersion(string version)
	{
		System.Text.StringBuilder builder = new(version.Length);
		foreach (char character in version)
		{
			if (char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_') builder.Append(character);
		}

		return builder.Length == 0 ? "Dev" : builder.ToString();
	}

	internal static IChatClient? TryCreateChatClient(
		LlmProvider provider,
		HttpClient httpClient,
		string baseUrl,
		string apiKey,
		string model)
	{
		string key = apiKey ?? "";
		(LLmProviders vendor, string urlFormat, int? defaultMaxTokens) = provider switch
		{
			LlmProvider.OpenAi => (LLmProviders.OpenAi, FormatRoot(NormalizeEndpoint(baseUrl, "/chat/completions")), (int?)null),
			LlmProvider.OpenAiResponses => (LLmProviders.OpenAi, FormatRoot(NormalizeEndpoint(baseUrl, "/responses")), null),
			// Anthropic 要求显式 max_tokens；沿用旧 SDK 接线时的 4096 默认值。
			LlmProvider.Anthropic => (LLmProviders.Anthropic, FormatRoot(NormalizeAnthropicBaseUrl(baseUrl) + "/v1"), 4096),
			LlmProvider.Google => (LLmProviders.Google, GoogleFormat(baseUrl), null),
			_ => (LLmProviders.Unknown, "", null),
		};
		if (vendor == LLmProviders.Unknown) return null;

		IEndpointProvider endpointProvider = vendor switch
		{
			LLmProviders.Anthropic => new AnthropicEndpointProvider(),
			LLmProviders.Google => new GoogleEndpointProvider(),
			_ => new OpenAiEndpointProvider(LLmProviders.OpenAi),
		};
		endpointProvider.Auth = new ProviderAuthentication(vendor, key);
		// 返回值还会被 LlmTornado 以 (端点片段, 子路径, 模型名) 再格式化一次。
		endpointProvider.UrlResolver = (_, _, _) => urlFormat;
		TornadoCallScope scope = new(httpClient, key, provider);
		TornadoApi api = TornadoTransport.CreateApi(endpointProvider, scope);
		return new TornadoChatClient(api, scope, model, vendor, defaultMaxTokens);
	}

	internal static Adapters.IModelCatalogAdapter CreateModelCatalogAdapter(LlmProvider provider, HttpClient httpClient) => provider switch
	{
		LlmProvider.OpenAi => new Adapters.OpenAiChatAdapter(httpClient),
		LlmProvider.OpenAiResponses => new Adapters.OpenAiResponsesAdapter(httpClient),
		LlmProvider.Anthropic => new Adapters.AnthropicAdapter(httpClient),
		LlmProvider.Google => new Adapters.GoogleGenAiAdapter(httpClient),
		_ => new Adapters.OpenAiChatAdapter(httpClient),
	};

	/// <summary>把根地址转成 LlmTornado 的 URL 模板，并转义地址本身的花括号。</summary>
	private static string FormatRoot(string root) =>
		root.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal) + "/{0}{1}";

	private static string GoogleFormat(string baseUrl)
	{
		(string root, string version) = NormalizeGoogleBaseUrl(baseUrl);
		return FormatRoot($"{root}/{(version.Length == 0 ? "v1beta" : version)}");
	}

	private static string NormalizeAnthropicBaseUrl(string baseUrl)
	{
		string normalized = NormalizeEndpoint(baseUrl, "/messages");
		if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[..^"/v1".Length].TrimEnd('/');
		}
		return normalized;
	}

	private static (string BaseUrl, string ApiVersion) NormalizeGoogleBaseUrl(string baseUrl)
	{
		string normalized = baseUrl.Trim().TrimEnd('/');
		if (normalized.Length == 0) throw new ChatException("Base URL 不能为空");
		// 协议头严格校验 (NORI-14): 非法地址提前转领域错误。
		_ = ChatEndpoint.CreateHttpUri(normalized);
		if (normalized.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[..^"/models".Length].TrimEnd('/');
		}
		int modelPath = normalized.IndexOf("/models/", StringComparison.OrdinalIgnoreCase);
		if (modelPath >= 0)
		{
			normalized = normalized[..modelPath].TrimEnd('/');
		}
		foreach (string version in new[] {"/v1beta", "/v1"})
		{
			if (normalized.EndsWith(version, StringComparison.OrdinalIgnoreCase))
			{
				return (normalized[..^version.Length].TrimEnd('/'), version[1..]);
			}
		}
		return (normalized, "");
	}

	private static string NormalizeEndpoint(string baseUrl, string protocolPath)
	{
		string endpoint = baseUrl.Trim().TrimEnd('/');
		if (endpoint.Length == 0) throw new ChatException("Base URL 不能为空");
		// 协议头严格校验 (NORI-14): 非法地址在这里转成领域错误, 不让 UriFormatException 泄漏。
		_ = ChatEndpoint.CreateHttpUri(endpoint);
		if (endpoint.EndsWith(protocolPath, StringComparison.OrdinalIgnoreCase))
		{
			endpoint = endpoint[..^protocolPath.Length].TrimEnd('/');
		}
		return endpoint;
	}
}
