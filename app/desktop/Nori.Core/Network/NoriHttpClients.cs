using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Nori.Core.Network;

/// <summary>
/// 应用出站 HTTP 客户端集合。
///
/// Local 用于用户显式配置的 LLM、Embedding、TTS 与 MCP 端点，保留系统代理；
/// Public 用于网页/天气/搜索/技能等公网请求，默认明确直连并在连接时校验 DNS 地址，禁止自动重定向。
/// </summary>
public sealed class NoriHttpClients : IDisposable
{
	/// <summary>本地/模型请求超时</summary>
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(130);

	public HttpClient Local { get; }

	public HttpClient Public { get; }

	private NoriHttpClients(HttpClient local, HttpClient @public)
	{
		Local = local;
		Public = @public;
	}

	/// <summary>创建一组带统一 TLS 与超时策略的客户端。</summary>
	/// <param name="allowInsecureTls">跳过公网/本地请求的证书校验 (自签名端点)。</param>
	/// <param name="publicUseSystemProxy">
	/// 公网客户端跟随系统代理。开启后不执行连接时的目标 DNS 地址校验；
	/// 调用方的 UrlAccessPolicy 仅预校验主机名/IP 字面量并逐跳检查重定向，
	/// 无法防止代理解析到私网或 DNS 重绑定，必须信任代理及其访问控制。
	/// </param>
	/// <param name="timeout">请求超时</param>
	public static NoriHttpClients Create(bool allowInsecureTls, TimeSpan? timeout = null, bool publicUseSystemProxy = false)
	{
		TimeSpan requestTimeout = timeout ?? DefaultTimeout;
		HttpClientHandler localHandler = new()
		{
			ServerCertificateCustomValidationCallback = allowInsecureTls
				? static (_, _, _, _) => true
				: null,
		};
		HttpClient local = new(localHandler) {Timeout = requestTimeout};

		HttpClient @public;
		if (publicUseSystemProxy)
		{
			SocketsHttpHandler proxiedHandler = new()
			{
				AllowAutoRedirect = false,
				PooledConnectionLifetime = TimeSpan.FromMinutes(5),
				UseProxy = true,
				Proxy = HttpClient.DefaultProxy,
				SslOptions = new SslClientAuthenticationOptions
				{
					RemoteCertificateValidationCallback = allowInsecureTls
						? static (_, _, _, _) => true
						: null,
				},
			};
			@public = new HttpClient(proxiedHandler) {Timeout = requestTimeout};
		}
		else
		{
			// AntiSSRF 1.0.0 未暴露 UseProxy=false；这里显式直连，并复用其 recommendedLatest 黑名单做连接时校验，
			// 避免 HttpClient.DefaultProxy/PAC 的误判阻断公网请求，也不让代理绕过 SSRF 校验。
			SocketsHttpHandler directHandler = new()
			{
				AllowAutoRedirect = false,
				PooledConnectionLifetime = TimeSpan.FromMinutes(5),
				UseProxy = false,
				ConnectCallback = static async (context, cancellationToken) =>
				{
					IPAddress[] addresses = await Dns.GetHostAddressesAsync(
						context.DnsEndPoint.Host, cancellationToken);
					UrlAccessPolicy.EnsureResolvedPublicAddresses(
						context.DnsEndPoint.Host, addresses);

					Socket socket = new(SocketType.Stream, ProtocolType.Tcp)
					{
						NoDelay = true,
					};
					try
					{
						await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
						return new NetworkStream(socket, ownsSocket: true);
					}
					catch
					{
						socket.Dispose();
						throw;
					}
				},
				SslOptions = new SslClientAuthenticationOptions
				{
					EnabledSslProtocols = SslProtocols.None,
					RemoteCertificateValidationCallback = allowInsecureTls
						? static (_, _, _, _) => true
						: null,
				},
			};
			@public = new HttpClient(new DirectPublicHandler(directHandler)) {Timeout = requestTimeout};
		}

		return new NoriHttpClients(local, @public);
	}

	private sealed class DirectPublicHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.RequestUri is not { } uri)
				throw new InvalidOperationException("公网请求缺少目标地址");
			UrlAccessPolicy.EnsurePublicHttp(uri);
			return base.SendAsync(request, cancellationToken);
		}
	}

	public void Dispose()
	{
		Public.Dispose();
		Local.Dispose();
	}
}
