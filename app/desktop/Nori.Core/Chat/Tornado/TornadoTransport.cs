using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Web;
using LlmTornado;
using LlmTornado.Code;
using Nori.Core.Network;

namespace Nori.Core.Chat.Tornado;

/// <summary>
/// 一次模型客户端生命周期内的传输上下文。
///
/// LlmTornado 的错误异常会把请求地址、请求头与请求正文拼进消息 (Google 的密钥还在查询串里)，
/// 也丢掉了 HTTP 状态码。转发处理器把原始失败记在这里，适配层据此生成脱敏且带状态码的异常。
/// </summary>
internal sealed class TornadoCallScope(HttpClient sharedHttpClient, string apiKey, LlmProvider protocol)
{
	private readonly Lock _gate = new();
	private TornadoHttpFailure? _failure;
	private Exception? _transportException;

	public HttpClient SharedHttpClient { get; } = sharedHttpClient;

	public string ApiKey { get; } = apiKey;

	public LlmProvider Protocol { get; } = protocol;

	/// <summary>开始新的一次请求前清掉上一轮记录 (工具调用会在同一客户端上连发多轮)。</summary>
	public void Reset()
	{
		lock (_gate)
		{
			_failure = null;
			_transportException = null;
		}
	}

	public void RecordFailure(HttpStatusCode status, string body)
	{
		lock (_gate) _failure = new TornadoHttpFailure(status, body);
	}

	public void RecordTransportException(Exception exception)
	{
		lock (_gate) _transportException = exception;
	}

	/// <summary>把 LlmTornado 抛出的异常换成脱敏结果；没有记录时返回 null。</summary>
	public Exception? TakeRecordedException()
	{
		lock (_gate)
		{
			if (_transportException is { } transport) return transport;
			if (_failure is { } failure)
			{
				return new HttpRequestException(
					TornadoErrorText.Describe(failure.Status, failure.Body, ApiKey),
					inner: null,
					statusCode: failure.Status);
			}
			return null;
		}
	}

	private sealed record TornadoHttpFailure(HttpStatusCode Status, string Body);
}

/// <summary>
/// LlmTornado 与 Nori 网络栈之间的唯一接缝。
///
/// LlmTornado 按 provider 在进程级缓存 HttpClient，无法逐个客户端注入；这里登记一个路由
/// HttpClient，并在每个请求发出前根据 TornadoApi 实例找到对应的上下文，把请求复制后交给
/// 调用方传入的共享 HttpClient —— 公网地址校验、代理策略、响应大小上限与测试替身因此保持不变。
/// </summary>
internal static class TornadoTransport
{
	/// <summary>错误正文只需要足够提取服务端错误说明。</summary>
	private const long MaxErrorBodyBytes = 64 * 1024;

	private static readonly ConditionalWeakTable<TornadoApi, TornadoCallScope> Scopes = new();
	private static readonly HttpRequestOptionsKey<TornadoCallScope> ScopeKey = new("Nori.Chat.TornadoScope");
	private static readonly HttpClient Router = new(new RoutingHandler(), disposeHandler: true)
	{
		Timeout = Timeout.InfiniteTimeSpan,
	};

	static TornadoTransport()
	{
		EndpointBase.OnHttpClientRequested = _ => Router;
		EndpointBase.OnHttpRequestOutbound = static (provider, _, _, _, request) =>
		{
			if (provider.Api is { } api && Scopes.TryGetValue(api, out TornadoCallScope? scope))
			{
				request.Options.Set(ScopeKey, scope);
			}
			return ValueTask.CompletedTask;
		};
	}

	/// <summary>创建只服务一个上下文的 TornadoApi。</summary>
	public static TornadoApi CreateApi(IEndpointProvider provider, TornadoCallScope scope)
	{
		TornadoApi api = new(provider);
		Scopes.AddOrUpdate(api, scope);
		return api;
	}

	/// <summary>
	/// 把请求转发给上下文的共享 HttpClient。
	/// 未登记上下文的请求一律拒绝，避免任何请求绕过公网地址校验直连。
	/// </summary>
	private sealed class RoutingHandler : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (!request.Options.TryGetValue(ScopeKey, out TornadoCallScope? scope) || scope is null)
			{
				throw new InvalidOperationException("模型请求缺少 Nori 传输上下文，已拒绝发送");
			}

			using HttpRequestMessage forwarded = await CloneRequestAsync(request, scope, cancellationToken).ConfigureAwait(false);
			HttpResponseMessage response;
			try
			{
				response = await scope.SharedHttpClient
					.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				// 原始异常 (如公网地址校验拒绝) 保留给适配层；LlmTornado 会把它包成带地址的新异常。
				scope.RecordTransportException(exception);
				throw;
			}

			response.Content = UrlAccessPolicy.WrapResponseContent(response.Content);
			if (response.IsSuccessStatusCode) return response;

			string body;
			try
			{
				body = await UrlAccessPolicy.ReadCappedTextAsync(response.Content, MaxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is InvalidOperationException or IOException or HttpRequestException)
			{
				body = string.Empty;
			}
			scope.RecordFailure(response.StatusCode, body);
			response.Content.Dispose();
			response.Content = new StringContent(body);
			return response;
		}

		private static async Task<HttpRequestMessage> CloneRequestAsync(
			HttpRequestMessage request,
			TornadoCallScope scope,
			CancellationToken cancellationToken)
		{
			Uri? uri = request.RequestUri;
			string? googleKey = null;
			if (scope.Protocol == LlmProvider.Google && uri is not null)
			{
				// Google 端点把密钥放在查询串里；改用请求头，免得密钥进入任何带地址的诊断。
				(uri, googleKey) = MoveQueryKey(uri);
			}

			HttpRequestMessage clone = new(request.Method, uri)
			{
				Version = request.Version,
				VersionPolicy = request.VersionPolicy,
			};
			foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
			{
				// OpenAI 兼容 provider 会把密钥再抄一份到 Azure 风格的 api-key 头，第三方网关不需要。
				if (header.Key.Equals("api-key", StringComparison.OrdinalIgnoreCase)) continue;
				clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
			if (googleKey is not null) clone.Headers.TryAddWithoutValidation("x-goog-api-key", googleKey);

			// 如实标明请求来自 Nori，理由见 ChatClientFactory.UserAgent。
			clone.Headers.UserAgent.Clear();
			clone.Headers.UserAgent.ParseAdd(ChatClientFactory.UserAgent);
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

		private static (Uri Uri, string? Key) MoveQueryKey(Uri uri)
		{
			if (string.IsNullOrEmpty(uri.Query)) return (uri, null);
			System.Collections.Specialized.NameValueCollection query = HttpUtility.ParseQueryString(uri.Query);
			string? key = query["key"];
			if (key is null) return (uri, null);
			query.Remove("key");
			UriBuilder builder = new(uri) { Query = query.ToString() ?? string.Empty };
			return (builder.Uri, key);
		}
	}
}

/// <summary>把服务端错误正文压成一句可展示的说明。</summary>
internal static class TornadoErrorText
{
	private const int MaxDetailCharacters = 300;

	public static string Describe(HttpStatusCode status, string body, string apiKey)
	{
		string detail = ExtractMessage(body);
		if (!string.IsNullOrEmpty(apiKey)) detail = detail.Replace(apiKey, "***", StringComparison.Ordinal);
		detail = string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		if (detail.Length > MaxDetailCharacters) detail = detail[..MaxDetailCharacters] + "…";
		string head = $"HTTP {(int)status} ({status})";
		return detail.Length == 0 ? head : $"{head}: {detail}";
	}

	private static string ExtractMessage(string body)
	{
		if (string.IsNullOrWhiteSpace(body)) return string.Empty;
		try
		{
			using JsonDocument document = JsonDocument.Parse(body);
			JsonElement root = document.RootElement;
			if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
			if (root.ValueKind == JsonValueKind.Object)
			{
				if (root.TryGetProperty("error", out JsonElement error))
				{
					if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? string.Empty;
					if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement nested)
						&& nested.ValueKind == JsonValueKind.String)
						return nested.GetString() ?? string.Empty;
				}
				foreach (string name in new[] {"message", "detail", "msg"})
				{
					if (root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
						return value.GetString() ?? string.Empty;
				}
			}
		}
		catch (JsonException)
		{
			// 非 JSON 正文 (如网关 HTML) 原样截断。
		}
		return body;
	}
}
