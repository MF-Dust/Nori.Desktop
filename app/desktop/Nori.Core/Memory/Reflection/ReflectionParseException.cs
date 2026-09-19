using System.Text.Json;
using Nori.Core.Security;

namespace Nori.Core.Memory;

/// <summary>不携带模型原文的 Reflection 解析失败。</summary>
public sealed class ReflectionParseException : FormatException
{
	public string Category { get; }
	public string Stage { get; }
	public long? LineNumber { get; }
	public long? BytePosition { get; }
	public int PayloadLength { get; }
	public string? Provider { get; private set; }
	public string? ModelId { get; private set; }

	internal ReflectionParseException(string category, string stage, int payloadLength, JsonException? inner = null)
		: base($"Reflection 解析失败: category={category}, stage={stage}", inner)
	{
		Category = category;
		Stage = stage;
		LineNumber = inner?.LineNumber;
		BytePosition = inner?.BytePositionInLine;
		PayloadLength = Math.Max(0, payloadLength);
	}

	internal void AttachRequestContext(string provider, string modelId)
	{
		Provider = ReflectionDiagnostics.SafeIdentifier(provider);
		ModelId = ReflectionDiagnostics.SafeIdentifier(modelId);
	}
}

/// <summary>只输出固定字段的 Reflection 安全诊断。</summary>
public static class ReflectionDiagnostics
{
	public static string Format(Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		if (exception is not ReflectionParseException parse)
		{
			return $"category={Classify(exception)} exception={exception.GetType().Name}";
		}

		List<string> fields =
		[
			$"category={parse.Category}",
			$"stage={parse.Stage}",
			$"exception={parse.InnerException?.GetType().Name ?? parse.GetType().Name}",
		];
		if (parse.LineNumber is { } line) fields.Add($"line={line}");
		if (parse.BytePosition is { } position) fields.Add($"byte={position}");
		fields.Add($"payload_length={parse.PayloadLength}");
		if (parse.Provider is {Length: > 0} provider) fields.Add($"provider={provider}");
		if (parse.ModelId is {Length: > 0} model) fields.Add($"model={model}");
		return string.Join(' ', fields);
	}

	internal static string Classify(Exception exception) => exception switch
	{
		ReflectionParseException parse => parse.Category,
		OperationCanceledException => "cancelled",
		TimeoutException => "timeout",
		Chat.ChatException => "llm",
		HttpRequestException => "network",
		_ => "exception",
	};

	internal static string SafeIdentifier(string value)
	{
		string normalized = new(value.Trim().Where(character => !char.IsControl(character)).Take(128).ToArray());
		return SensitiveDataRedactor.Redact(normalized);
	}
}
