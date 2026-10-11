using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Nori.Core.Security;

namespace Nori.Core.Network;

/// <summary>
/// 公网 HTTP 请求的基础 URL 校验。
///
/// <c>allowPrivate</c> 为 false 时拒绝回环、链路本地、私网、元数据地址和保留主机名。
/// 主机名不是字面 IP 时，抓取路径会解析 DNS，任一结果不是公网单播就拒绝。
/// DNS 解析本身失败时不单独拦截，交给后续 HTTP 调用报告。
/// <c>allowPrivate</c> 为 true 时只校验协议和主机名，供本机 LLM/TTS 端点使用。
/// </summary>
public static class UrlAccessPolicy
{
	/// <summary>抓取响应的体积上限</summary>
	public const long MaxResponseBytes = 3 * 1024 * 1024;

	/// <summary>重定向跟随上限</summary>
	public const int MaxRedirects = 5;

	/// <summary>
	/// 测试替换主机名解析。生产路径保持 null，走系统 DNS。
	/// </summary>
	internal static Func<string, CancellationToken, Task<IPAddress[]>>? ResolveHostForTests { get; set; }

	/// <summary>校验公网 HTTP(S) 地址，拒绝私网和保留地址。</summary>
	public static void EnsurePublicHttp(Uri uri) => EnsureAllowed(uri, allowPrivate: false);

	/// <summary>把公网请求失败翻成用户可见的中文异常。</summary>
	public static InvalidOperationException Translate(Exception exception, Uri? uri = null)
	{
		if (exception is InvalidOperationException invalidOperation) return invalidOperation;
		string host = uri?.Host ?? "目标地址";
		return new InvalidOperationException($"访问公网地址失败: {host}", exception);
	}

	/// <summary>校验 HTTP(S) 地址。公网请求拒绝私网、链路本地、回环和元数据地址。</summary>
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
		if (IsReservedHost(uri.IdnHost) || (TryParseHost(uri.IdnHost, out IPAddress? address) && address is not null && !IsPublicUnicast(address)))
		{
			throw new InvalidOperationException($"不允许访问的地址 (私网或保留地址): {uri}");
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
		await EnsurePublicResolutionAsync(uri, allowPrivate, cancellationToken);
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
				await EnsurePublicResolutionAsync(current, allowPrivate, cancellationToken);
			}
			catch
			{
				response.Dispose();
				throw;
			}
			response.Dispose();
		}
	}

	/// <summary>
	/// 非字面 IP 的主机名在公网模式下解析 DNS。任一地址不是公网单播则拒绝。
	/// 解析失败不在这里拦截，让后续 HTTP 调用自己失败。
	/// </summary>
	private static async Task EnsurePublicResolutionAsync(Uri uri, bool allowPrivate, CancellationToken cancellationToken)
	{
		if (allowPrivate || TryParseHost(uri.IdnHost, out _)) return;
		IPAddress[] addresses;
		try
		{
			addresses = ResolveHostForTests is null
				? await Dns.GetHostAddressesAsync(uri.IdnHost, cancellationToken)
				: await ResolveHostForTests(uri.IdnHost, cancellationToken);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			return;
		}
		if (addresses is not null && addresses.Any(address => !IsPublicUnicast(address)))
		{
			throw new InvalidOperationException($"不允许访问的地址 (解析到私网或保留地址): {uri}");
		}
	}

	private static bool IsReservedHost(string host)
	{
		string normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
		if (normalized.Length == 0) return true;
		if (normalized is "localhost" or "metadata" or "metadata.google.internal") return true;
		return normalized.EndsWith(".localhost", StringComparison.Ordinal)
			|| normalized.EndsWith(".metadata.google.internal", StringComparison.Ordinal);
	}

	private static bool TryParseHost(string host, out IPAddress? address)
	{
		string candidate = host.Trim();
		int zone = candidate.IndexOf('%');
		if (zone >= 0) candidate = candidate[..zone];
		return IPAddress.TryParse(candidate, out address);
	}

	/// <summary>公网单播。回环、未指定、私网、链路本地、共享地址、文档和组播地址都不是。</summary>
	private static bool IsPublicUnicast(IPAddress address)
	{
		if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
		if (address.AddressFamily == AddressFamily.InterNetworkV6) return IsPublicIpv6(address);
		if (address.AddressFamily != AddressFamily.InterNetwork) return false;

		byte[] bytes = address.GetAddressBytes();
		if (bytes[0] == 0) return false;
		if (bytes[0] == 10) return false;
		if (bytes[0] == 127) return false;
		if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) return false;
		if (bytes[0] == 169 && bytes[1] == 254) return false;
		if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return false;
		if (bytes[0] == 192 && bytes[1] == 168) return false;
		if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) return false;
		if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) return false;
		if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) return false;
		if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) return false;
		if (bytes[0] == 198 && bytes[1] is 18 or 19) return false;
		return bytes[0] < 224;
	}

	private static bool IsPublicIpv6(IPAddress address)
	{
		if (IPAddress.IPv6Any.Equals(address) || IPAddress.IPv6Loopback.Equals(address)) return false;
		if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
		byte[] bytes = address.GetAddressBytes();
		if ((bytes[0] & 0xFE) == 0xFC) return false;
		return !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8);
	}

	/// <summary>读取受限的 Provider 错误摘要，不把完整响应正文带到 UI 或日志。</summary>
	public static async Task<string> ReadSafeProviderErrorAsync(
		HttpContent content,
		CancellationToken cancellationToken = default)
	{
		try
		{
			string raw = await ReadCappedTextAsync(content, 4096, cancellationToken);
			if (JsonNode.Parse(raw) is not { } node) return "";
			string? message = node["error"]?["message"]?.GetValue<string>() ?? node["message"]?.GetValue<string>();
			if (string.IsNullOrWhiteSpace(message)) return "";
			string normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
			if (normalized.Length > 200) normalized = normalized[..200];
			return SensitiveDataRedactor.Redact(normalized);
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

	/// <summary>用受限内容包装响应，防止客户端库直接读取无界响应体。</summary>
	internal static HttpContent WrapResponseContent(HttpContent content, long cap = MaxResponseBytes) =>
		new CappedHttpContent(content, cap);

	private sealed class CappedHttpContent(HttpContent inner, long cap) : HttpContent
	{
		protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
			CopyCappedToAsync(stream, CancellationToken.None);

		protected override Task SerializeToStreamAsync(
			Stream stream,
			System.Net.TransportContext? context,
			CancellationToken cancellationToken) =>
			CopyCappedToAsync(stream, cancellationToken);

		protected override bool TryComputeLength(out long length)
		{
			length = 0;
			return false;
		}

		protected override Task<Stream> CreateContentReadStreamAsync() => CreateCappedStreamAsync();

		private async Task CopyCappedToAsync(Stream destination, CancellationToken cancellationToken)
		{
			await using Stream source = await inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
			await CopyCappedAsync(source, destination, cancellationToken).ConfigureAwait(false);
		}

		private async Task<Stream> CreateCappedStreamAsync()
		{
			Stream source = await inner.ReadAsStreamAsync().ConfigureAwait(false);
			return new CappedReadStream(source, cap);
		}

		private async Task CopyCappedAsync(Stream source, Stream destination, CancellationToken cancellationToken)
		{
			if (inner.Headers.ContentLength is long declaredLength && declaredLength > cap)
				throw new InvalidOperationException($"远程响应超过大小上限 ({cap / 1024 / 1024} MB)");

			byte[] buffer = new byte[64 * 1024];
			long total = 0;
			while (true)
			{
				int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
				if (read == 0) return;
				total += read;
				if (total > cap) throw new InvalidOperationException($"远程响应超过大小上限 ({cap / 1024 / 1024} MB)");
				await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing) inner.Dispose();
			base.Dispose(disposing);
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

/// <summary>为流式响应提供严格的字节上限，不预读或缓冲整个响应。</summary>
internal sealed class CappedReadStream(Stream inner, long cap) : Stream
{
	private readonly byte[] _probe = new byte[1];
	private long _total;

	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position
	{
		get => _total;
		set => throw new NotSupportedException();
	}

	public override void Flush() { }
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	public override int Read(byte[] buffer, int offset, int count)
	{
		if (count == 0) return 0;
		if (_total >= cap)
		{
			int extra = inner.Read(_probe, 0, 1);
			if (extra != 0) throw LimitExceeded();
			return 0;
		}
		int allowed = (int)Math.Min(count, cap - _total);
		int read = inner.Read(buffer, offset, allowed);
		_total += read;
		return read;
	}

	public override int Read(Span<byte> buffer)
	{
		if (buffer.Length == 0) return 0;
		if (_total >= cap)
		{
			int extra = inner.Read(_probe);
			if (extra != 0) throw LimitExceeded();
			return 0;
		}
		int allowed = (int)Math.Min(buffer.Length, cap - _total);
		int read = inner.Read(buffer[..allowed]);
		_total += read;
		return read;
	}

	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		if (buffer.Length == 0) return 0;
		if (_total >= cap)
		{
			int extra = await inner.ReadAsync(_probe.AsMemory(), cancellationToken).ConfigureAwait(false);
			if (extra != 0) throw LimitExceeded();
			return 0;
		}
		int allowed = (int)Math.Min(buffer.Length, cap - _total);
		int read = await inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
		_total += read;
		return read;
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing) inner.Dispose();
		base.Dispose(disposing);
	}

	private static InvalidOperationException LimitExceeded() =>
		new("远程响应超过大小上限");
}
