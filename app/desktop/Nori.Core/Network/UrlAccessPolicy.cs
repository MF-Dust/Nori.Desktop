using System.Net;
using System.Text;

namespace Nori.Core.Network;

/// <summary>
/// 公网 HTTP 请求的基础 URL 校验。
///
/// 这里只保留协议和主机格式校验，不拦截私网、保留地址或 DNS 结果；公网网络环境可能通过代理、VPN 或自定义 DNS 出口。
/// </summary>
public static class UrlAccessPolicy
{
	/// <summary>抓取响应的体积上限</summary>
	public const long MaxResponseBytes = 3 * 1024 * 1024;

	/// <summary>重定向跟随上限</summary>
	public const int MaxRedirects = 5;

	/// <summary>校验 HTTP(S) 地址。</summary>
	public static void EnsurePublicHttp(Uri uri) => EnsureAllowed(uri, allowPrivate: true);

	/// <summary>把公网请求失败翻成用户可见的中文异常。</summary>
	public static InvalidOperationException Translate(Exception exception, Uri? uri = null)
	{
		if (exception is InvalidOperationException invalidOperation) return invalidOperation;
		string host = uri?.Host ?? "目标地址";
		return new InvalidOperationException($"访问公网地址失败: {host}", exception);
	}

	/// <summary>校验 HTTP(S) 地址。</summary>
	public static void EnsureAllowed(Uri uri, bool allowPrivate)
	{
		_ = allowPrivate;
		if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
		{
			throw new InvalidOperationException($"不允许访问的地址 (仅支持 http/https): {uri}");
		}
		if (string.IsNullOrEmpty(uri.Host))
		{
			throw new InvalidOperationException($"不允许访问的地址 (缺少主机名): {uri}");
		}
	}

	/// <summary>
	/// GET 抓取: 手动跟随重定向并逐跳校验。返回的响应由调用方负责释放。
	/// </summary>
	public static async Task<HttpResponseMessage> GetWithSafeRedirectsAsync(
		HttpClient httpClient,
		Uri uri,
		bool allowPrivate,
		int maxRedirects = MaxRedirects,
		CancellationToken cancellationToken = default)
	{
		EnsureAllowed(uri, allowPrivate);
		Uri current = uri;

		for (int hop = 0; ; hop++)
		{
			HttpResponseMessage response;
			try
			{
				response = await httpClient.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (HttpRequestException exception)
			{
				throw Translate(exception, current);
			}

			if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } next)
			{
				return response;
			}
			if (hop >= maxRedirects)
			{
				response.Dispose();
				throw new InvalidOperationException($"重定向次数超过上限 ({maxRedirects}): {current}");
			}

			current = next.IsAbsoluteUri ? next : new Uri(current, next);
			try
			{
				EnsureAllowed(current, allowPrivate);
			}
			catch
			{
				response.Dispose();
				throw;
			}
			response.Dispose();
		}
	}

	/// <summary>按 UTF-8 字节上限读取响应文本，避免错误服务耗尽内存。</summary>
	public static async Task<string> ReadCappedTextAsync(
		HttpContent content,
		long cap = MaxResponseBytes,
		CancellationToken cancellationToken = default)
	{
		await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
		using MemoryStream output = new();
		byte[] buffer = new byte[64 * 1024];
		long total = 0;
		while (true)
		{
			int read = await stream.ReadAsync(buffer, cancellationToken);
			if (read <= 0) break;
			total += read;
			if (total > cap)
			{
				throw new InvalidOperationException($"远程文件超过大小上限 ({cap / 1024 / 1024} MB)");
			}
			output.Write(buffer, 0, read);
		}
		return Encoding.UTF8.GetString(output.ToArray());
	}
}
