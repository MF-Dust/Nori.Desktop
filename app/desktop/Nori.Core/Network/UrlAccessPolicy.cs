using System.Net;
using System.Net.Sockets;
using Microsoft.Security.AntiSSRF;
using System.Text;

namespace Nori.Core.Network;

internal sealed class NetworkAddressPolicyException(string message) : InvalidOperationException(message);

/// <summary>
/// 受限的后端 URL 访问策略。
///
/// 公网请求先做 scheme/主机/IP 校验，再由默认直连客户端在真实连接时复核 DNS 地址；
/// 重定向由此处逐跳跟随，避免自动重定向绕过策略。
/// </summary>
public static class UrlAccessPolicy
{
	// 快照 AntiSSRF 的推荐黑名单，避免手写掩码偏离原策略。
	private static readonly IPNetwork[] _restrictedNetworks =
		IPAddressRanges.recommendedLatest.Select(IPNetwork.Parse).ToArray();

	/// <summary>抓取响应的体积上限</summary>
	public const long MaxResponseBytes = 3 * 1024 * 1024;

	/// <summary>重定向跟随上限</summary>
	public const int MaxRedirects = 5;

	/// <summary>校验公网 HTTP(S) 地址。</summary>
	public static void EnsurePublicHttp(Uri uri) => EnsureAllowed(uri, allowPrivate: false);

	/// <summary>把公网客户端的安全拦截与传输失败翻成用户可见的中文异常。</summary>
	public static InvalidOperationException Translate(Exception exception, Uri? uri = null)
	{
		if (exception is InvalidOperationException invalidOperation) return invalidOperation;
		string host = uri?.Host ?? "目标地址";
		if (ContainsNetworkPolicyRejection(exception))
		{
			return new InvalidOperationException($"公网地址被安全策略拒绝: {host}", exception);
		}
		if (exception is HttpRequestException)
		{
			return new InvalidOperationException($"访问公网地址失败: {host}", exception);
		}
		return new InvalidOperationException($"访问公网地址失败: {host}", exception);
	}

	/// <summary>校验 HTTP(S) 地址; allowPrivate 仅用于显式配置的本地端点。</summary>
	public static void EnsureAllowed(Uri uri, bool allowPrivate)
	{
		if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
		{
			throw new InvalidOperationException($"不允许访问的地址 (仅支持 http/https): {uri}");
		}
		if (string.IsNullOrEmpty(uri.Host))
		{
			throw new InvalidOperationException($"不允许访问的地址 (缺少主机名): {uri}");
		}
		if (allowPrivate) return;

		// 域名的最终 IP 由默认直连客户端的 ConnectCallback 校验；这里仅拦截
		// 可直接识别的字面量与 localhost，避免 DNS 预解析造成 TOCTOU。
		if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
			uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException($"不允许访问私网或保留地址: {uri.Host}");
		}
		if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? literal) && IsRestricted(literal))
		{
			throw new InvalidOperationException($"不允许访问私网或保留地址: {uri.Host}");
		}
	}

	/// <summary>
	/// 校验 DNS 解析结果。必须拒绝结果中包含的任一私网或保留地址，避免连接时选择到危险地址。
	/// </summary>
	internal static void EnsureResolvedPublicAddresses(string host, IPAddress[] addresses)
	{
		if (addresses.Length == 0 || addresses.Any(IsRestricted))
			throw new NetworkAddressPolicyException($"公网地址被安全策略拒绝: {host}");
	}

	/// <summary>内部判定, 测试可见: 地址是否回环/私网/链路本地/保留</summary>
	public static bool IsRestricted(IPAddress address)
	{
		if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
		return address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
			|| _restrictedNetworks.Any(network => network.Contains(address));
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
			catch (AntiSSRFException exception)
			{
				throw Translate(exception, current);
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

	private static bool ContainsNetworkPolicyRejection(Exception exception)
	{
		for (Exception? current = exception; current is not null; current = current.InnerException)
		{
			if (current is AntiSSRFException or NetworkAddressPolicyException) return true;
		}
		return false;
	}
}
