using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Core;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace Nori.Core.Chat;

/// <summary>官方 provider SDK 与 IChatClient 的工厂。</summary>
public static class ChatClientFactory
{
	/// <summary>
	/// 发给模型服务的客户端标识。
	///
	/// provider SDK 默认写的是它自己（形如 <c>OpenAI/2.5.0 (.NET 10.0; Windows …)</c>）——
	/// 那句话声称发请求的是 OpenAI 的客户端，而不是 Nori。两个后果：
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
		return provider switch
		{
			LlmProvider.OpenAi => CreateOpenAiChatClient(httpClient, baseUrl, key, model),
			LlmProvider.OpenAiResponses => CreateOpenAiResponsesChatClient(httpClient, baseUrl, key, model),
			LlmProvider.Anthropic => CreateAnthropicChatClient(httpClient, baseUrl, key, model),
			LlmProvider.Google => CreateGoogleChatClient(httpClient, baseUrl, key, model),
			_ => null,
		};
	}

	internal static Adapters.IModelCatalogAdapter CreateModelCatalogAdapter(LlmProvider provider, HttpClient httpClient) => provider switch
	{
		LlmProvider.OpenAi => new Adapters.OpenAiChatAdapter(httpClient),
		LlmProvider.OpenAiResponses => new Adapters.OpenAiResponsesAdapter(httpClient),
		LlmProvider.Anthropic => new Adapters.AnthropicAdapter(httpClient),
		LlmProvider.Google => new Adapters.GoogleGenAiAdapter(httpClient),
		_ => new Adapters.OpenAiChatAdapter(httpClient),
	};

	private static IChatClient CreateOpenAiChatClient(HttpClient sharedHttpClient, string baseUrl, string apiKey, string model)
	{
		string endpoint = NormalizeEndpoint(baseUrl, "/chat/completions");
		ApiKeyCredential credential = new(apiKey);
		HttpClient providerHttpClient = CreateProviderHttpClient(sharedHttpClient);
		OpenAIClientOptions options = new()
		{
			Endpoint = ChatEndpoint.CreateHttpUri(endpoint),
			Transport = new HttpClientPipelineTransport(providerHttpClient),
		};
		ChatClient client = new(model, credential, options);
		return new OwnedChatClient(client.AsIChatClient(), clientOwner: null, providerHttpClient);
	}

	private static IChatClient CreateOpenAiResponsesChatClient(HttpClient sharedHttpClient, string baseUrl, string apiKey, string model)
	{
		string endpoint = NormalizeEndpoint(baseUrl, "/responses");
		ApiKeyCredential credential = new(apiKey);
		HttpClient providerHttpClient = CreateProviderHttpClient(sharedHttpClient);
		ResponsesClientOptions options = new()
		{
			Endpoint = ChatEndpoint.CreateHttpUri(endpoint),
			Transport = new HttpClientPipelineTransport(providerHttpClient),
		};
		ResponsesClient client = new(credential, options);
		return new OwnedChatClient(client.AsIChatClient(model), clientOwner: null, providerHttpClient);
	}

	private static IChatClient CreateAnthropicChatClient(HttpClient sharedHttpClient, string baseUrl, string apiKey, string model)
	{
		Anthropic.Core.ClientOptions options = new()
		{
			BaseUrl = NormalizeAnthropicBaseUrl(baseUrl),
			ApiKey = apiKey,
			HttpClient = CreateProviderHttpClient(sharedHttpClient),
			MaxRetries = 0,
		};
		AnthropicClient client = new(options);
		return new OwnedChatClient(client.AsIChatClient(model, defaultMaxOutputTokens: 4096), client, client.HttpClient);
	}

	private static IChatClient CreateGoogleChatClient(HttpClient sharedHttpClient, string baseUrl, string apiKey, string model)
	{
		HttpClient providerHttpClient = CreateProviderHttpClient(sharedHttpClient);
		(string googleBaseUrl, string googleApiVersion) = NormalizeGoogleBaseUrl(baseUrl);
		Google.GenAI.Types.HttpOptions httpOptions = new()
		{
			BaseUrl = googleBaseUrl,
			ApiVersion = googleApiVersion,
		};
		Google.GenAI.Types.ClientOptions clientOptions = new()
		{
			HttpClientFactory = () => providerHttpClient,
		};
		Google.GenAI.Client client = new(
			enterprise: false,
			vertexAI: false,
			apiKey: apiKey,
			httpOptions: httpOptions,
			clientOptions: clientOptions);
		return new OwnedChatClient(client.AsIChatClient(model), client, providerHttpClient);
	}

	/// <summary>转发用的外层客户端。internal 是为了让用例能验到 User-Agent 真的被换掉。</summary>
	internal static HttpClient CreateProviderHttpClient(HttpClient sharedHttpClient) =>
		new(new SharedHttpMessageHandler(sharedHttpClient), disposeHandler: true)
		{
			Timeout = sharedHttpClient.Timeout,
		};

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
		// 协议头严格校验 (NORI-14): Google SDK 只收字符串 BaseUrl, 非法地址提前转领域错误。
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

	private sealed class SharedHttpMessageHandler(HttpClient sharedHttpClient) : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			// 外层 provider HttpClient 已经标记 request 为已发送，转发给共享 HttpClient 前必须复制消息。
			using HttpRequestMessage forwarded = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
			return await sharedHttpClient.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		}

		private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			HttpRequestMessage clone = new(request.Method, request.RequestUri)
			{
				Version = request.Version,
				VersionPolicy = request.VersionPolicy,
			};
			foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
			{
				clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}

			// 覆盖 SDK 写的那一条，理由见 ChatClientFactory.UserAgent。
			clone.Headers.UserAgent.Clear();
			clone.Headers.UserAgent.ParseAdd(UserAgent);
			if (request.Content is not null)
			{
				byte[] content = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
				ByteArrayContent clonedContent = new(content);
				foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
				{
					clonedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
				}
				clone.Content = clonedContent;
			}
			return clone;
		}

		protected override void Dispose(bool disposing)
		{
			// provider SDK 只拥有这个包装器，不能释放应用级共享客户端。
		}
	}

	private sealed class OwnedChatClient(IChatClient inner, IDisposable? clientOwner, HttpClient providerHttpClient) : IChatClient
	{
		private int _disposed;

		public Task<ChatResponse> GetResponseAsync(
			IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
			ChatOptions? options = null,
			CancellationToken cancellationToken = default) =>
			inner.GetResponseAsync(messages, options, cancellationToken);

		public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
			IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
			ChatOptions? options = null,
			CancellationToken cancellationToken = default) =>
			inner.GetStreamingResponseAsync(messages, options, cancellationToken);

		public object? GetService(System.Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
			try
			{
				inner.Dispose();
			}
			finally
			{
				try
				{
					clientOwner?.Dispose();
				}
				finally
				{
					providerHttpClient.Dispose();
				}
			}
		}
	}
}
