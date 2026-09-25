using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nori.Core.Security;

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

	/// <summary>用受限内容包装响应，防止 SDK 直接读取无界响应体。</summary>
	internal static HttpContent WrapResponseContent(HttpContent content, long cap = MaxResponseBytes) =>
		new CappedHttpContent(content, cap);

	/// <summary>为不能注入 HttpContent 的官方 SDK 创建受限客户端。</summary>
	internal static HttpClient CreateCappedHttpClient(HttpClient httpClient, long cap = MaxResponseBytes) =>
		new(new CappedResponseHandler(httpClient, cap)) {Timeout = httpClient.Timeout};

	private sealed class CappedResponseHandler(HttpClient inner, long cap) : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			using HttpRequestMessage forwarded = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
			HttpResponseMessage response = await inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			response.Content = new CappedHttpContent(response.Content, cap);
			return response;
		}

		private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			HttpRequestMessage clone = new(request.Method, request.RequestUri)
			{
				Version = request.Version,
				VersionPolicy = request.VersionPolicy,
			};
			foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
				clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
			if (request.Content is not null)
			{
				byte[] body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
				ByteArrayContent content = new(body);
				foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
					content.Headers.TryAddWithoutValidation(header.Key, header.Value);
				clone.Content = content;
			}
			return clone;
		}
	}

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
