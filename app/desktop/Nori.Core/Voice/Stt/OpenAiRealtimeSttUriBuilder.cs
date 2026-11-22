using System.Net;

namespace Nori.Core.Voice.Stt;

/// <summary>
/// 构造并校验 OpenAI Realtime Transcription WebSocket 与批量转写 HTTP 地址。
///
/// Realtime 仅允许 <c>wss://</c> 及本地环回明文地址 (<c>127.0.0.1</c> / <c>localhost</c> / <c>[::1]</c>)，
/// 查询参数仅允许规范化后的 <c>intent=transcription</c>；批量转写仅允许 <c>https://</c> 及本地环回 <c>http://</c>，
/// 并严格禁止在 URL 用户信息或敏感查询参数中携带凭据。
/// </summary>
public static class OpenAiRealtimeSttUriBuilder
{
	/// <summary>默认 OpenAI API 基地址。</summary>
	public const string DefaultBaseUrl = "https://api.openai.com/v1";

	/// <summary>将 HTTP(S) 或 WS(S) 地址规范化为 Realtime Transcription WebSocket 地址。</summary>
	public static Uri Build(string? baseUrl)
	{
		string candidate = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
		if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed))
		{
			throw new InvalidOperationException("实时语音识别服务地址格式无效");
		}

		if (!string.IsNullOrEmpty(parsed.UserInfo))
		{
			throw new InvalidOperationException("实时语音识别服务地址不允许包含凭据信息");
		}

		string scheme = parsed.Scheme.ToLowerInvariant();
		if (scheme is not ("http" or "https" or "ws" or "wss"))
		{
			throw new InvalidOperationException("实时语音识别服务地址仅支持 https/wss 或本地环回 http/ws");
		}

		if (string.IsNullOrEmpty(parsed.Host))
		{
			throw new InvalidOperationException("实时语音识别服务地址缺少主机名");
		}

		bool isEncrypted = scheme is "https" or "wss";
		if (!isEncrypted && !IsLoopbackHost(parsed.DnsSafeHost))
		{
			throw new InvalidOperationException("实时语音识别服务禁止向非本地环回地址使用明文 ws/http 连接");
		}

		ValidateRealtimeQuery(parsed.Query);

		string targetScheme = isEncrypted ? "wss" : "ws";
		string normalizedPath = NormalizeRealtimePath(parsed.AbsolutePath);

		UriBuilder builder = new(parsed)
		{
			Scheme = targetScheme,
			Port = parsed.IsDefaultPort ? -1 : parsed.Port,
			Path = normalizedPath,
			Query = "intent=transcription",
			Fragment = string.Empty,
		};
		return builder.Uri;
	}

	/// <summary>将 HTTP(S) 地址解析并规范化为批量转写端点 (<c>/audio/transcriptions</c>)。</summary>
	public static Uri BuildBatchUri(string? baseUrl)
	{
		string candidate = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
		if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed))
		{
			throw new InvalidOperationException("语音识别服务地址格式无效");
		}

		if (!string.IsNullOrEmpty(parsed.UserInfo))
		{
			throw new InvalidOperationException("语音识别服务地址不允许包含凭据信息");
		}

		string scheme = parsed.Scheme.ToLowerInvariant();
		if (scheme is not ("http" or "https"))
		{
			throw new InvalidOperationException("语音识别服务地址仅支持 https 或本地环回 http");
		}

		if (string.IsNullOrEmpty(parsed.Host))
		{
			throw new InvalidOperationException("语音识别服务地址缺少主机名");
		}

		if (scheme == "http" && !IsLoopbackHost(parsed.DnsSafeHost))
		{
			throw new InvalidOperationException("语音识别服务禁止向非本地环回地址使用明文 http 连接");
		}

		if (!string.IsNullOrEmpty(parsed.Query) && ContainsSecretQuery(parsed.Query))
		{
			throw new InvalidOperationException("语音识别服务地址不允许在查询参数中携带凭据");
		}

		string path = (parsed.AbsolutePath ?? "").TrimEnd('/');
		if (!path.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase))
		{
			path += "/audio/transcriptions";
		}

		UriBuilder builder = new(parsed)
		{
			Scheme = scheme,
			Port = parsed.IsDefaultPort ? -1 : parsed.Port,
			Path = path,
			Fragment = string.Empty,
		};
		return builder.Uri;
	}

	internal static bool IsLoopbackHost(string host)
	{
		if (string.IsNullOrWhiteSpace(host)) return false;
		string normalized = host.Trim().Trim('[', ']').TrimEnd('.');
		if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
		return IPAddress.TryParse(normalized, out IPAddress? address) && IPAddress.IsLoopback(address);
	}

	private static string NormalizeRealtimePath(string rawPath)
	{
		string path = (rawPath ?? "").TrimEnd('/');

		if (path.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase))
		{
			path = path[..^"/audio/transcriptions".Length].TrimEnd('/');
		}
		else if (path.EndsWith("/realtime", StringComparison.OrdinalIgnoreCase))
		{
			path = path[..^"/realtime".Length].TrimEnd('/');
		}

		while (path.EndsWith("/v1/v1", StringComparison.OrdinalIgnoreCase))
		{
			path = path[..^3];
		}

		if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
		{
			path += "/v1";
		}

		return path + "/realtime";
	}

	private static void ValidateRealtimeQuery(string rawQuery)
	{
		string trimmed = rawQuery.TrimStart('?');
		if (trimmed.Length == 0) return;

		foreach (string segment in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			int equals = segment.IndexOf('=');
			string key = (equals >= 0 ? segment[..equals] : segment).Trim();
			string value = (equals >= 0 ? segment[(equals + 1)..] : string.Empty).Trim();

			if (!string.Equals(key, "intent", StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(value, "transcription", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("实时语音识别服务地址不允许携带除 intent=transcription 以外的查询参数");
			}
		}
	}

	private static bool ContainsSecretQuery(string rawQuery)
	{
		string trimmed = rawQuery.TrimStart('?');
		foreach (string segment in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			int equals = segment.IndexOf('=');
			string key = (equals >= 0 ? segment[..equals] : segment).Trim();
			if (IsForbiddenSecretKey(key)) return true;
		}
		return false;
	}

	private static bool IsForbiddenSecretKey(string key) =>
		key.Equals("api_key", StringComparison.OrdinalIgnoreCase)
		|| key.Equals("apikey", StringComparison.OrdinalIgnoreCase)
		|| key.Equals("key", StringComparison.OrdinalIgnoreCase)
		|| key.Equals("token", StringComparison.OrdinalIgnoreCase)
		|| key.Equals("access_token", StringComparison.OrdinalIgnoreCase)
		|| key.Equals("secret", StringComparison.OrdinalIgnoreCase)
		|| key.Equals("password", StringComparison.OrdinalIgnoreCase)
		|| key.Equals("authorization", StringComparison.OrdinalIgnoreCase);
}
