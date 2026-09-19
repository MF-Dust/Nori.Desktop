using System.Net;
using System.Text.Json;
using Nori.Core.Tools;

namespace Nori.Core.Network;

/// <summary>只携带安全固定字段的 AnySearch 失败。</summary>
public sealed class AnySearchException : HttpRequestException, IToolFailureException
{
	public string Category { get; }
	public string? Code { get; }
	public string? RequestId { get; }

	internal AnySearchException(string message, string category, HttpStatusCode? statusCode = null,
		string? code = null, string? requestId = null, Exception? inner = null)
		: base(message, inner, statusCode)
	{
		Category = category;
		Code = code;
		RequestId = requestId;
	}

	int? IToolFailureException.StatusCode => StatusCode is { } status ? (int)status : null;
}

/// <summary>把 AnySearch 响应转换为不回显正文的稳定错误。</summary>
public static class AnySearchError
{
	public static AnySearchException Parse(HttpStatusCode statusCode, string body)
	{
		(string? code, string? requestId) = ReadMetadata(body);
		(string category, string message) = statusCode switch
		{
			HttpStatusCode.BadRequest => ("invalid_request", "AnySearch 请求参数无效。"),
			HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ("authentication", "AnySearch API Key 无效。"),
			HttpStatusCode.PaymentRequired => ("quota", "AnySearch 匿名额度已用尽，请配置 API Key。"),
			(HttpStatusCode)429 => ("rate_limit", "AnySearch 请求过于频繁，请稍后重试。"),
			_ when (int)statusCode >= 500 => ("upstream", "AnySearch 服务暂时不可用。"),
			_ => ("upstream", "AnySearch 请求失败。"),
		};
		return new AnySearchException(message, category, statusCode, code, requestId);
	}

	public static AnySearchException Configuration(Exception inner) =>
		new("AnySearch 配置无效。", "configuration", inner: inner);

	public static AnySearchException Network(Exception inner) =>
		new("AnySearch 网络连接失败。", "network", inner: inner);

	public static AnySearchException ResponseTooLarge(Exception inner) =>
		new("AnySearch 响应超过安全大小上限。", "upstream", inner: inner);

	private static (string? Code, string? RequestId) ReadMetadata(string body)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(body);
			if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
			JsonElement root = document.RootElement;
			string? code = ReadToken(root, "code");
			string? requestId = ReadToken(root, "request_id") ?? ReadToken(root, "requestId");
			return (code, requestId);
		}
		catch (JsonException)
		{
			return (null, null);
		}
	}

	private static string? ReadToken(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out JsonElement value)) return null;
		string? candidate = value.ValueKind switch
		{
			JsonValueKind.String => value.GetString(),
			JsonValueKind.Number => value.GetRawText(),
			_ => null,
		};
		if (string.IsNullOrWhiteSpace(candidate)) return null;
		string trimmed = candidate.Trim();
		if (trimmed.Length > 128) return null;
		return trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')
			? trimmed
			: null;
	}
}
