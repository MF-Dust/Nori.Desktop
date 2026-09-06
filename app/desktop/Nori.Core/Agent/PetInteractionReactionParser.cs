using System.Text.Json;

namespace Nori.Core.Agent;

/// <summary>伴侣互动 AI 响应。</summary>
public sealed record PetInteractionReaction
{
	public string? Text { get; init; }
	public string? Emotion { get; init; }
	public string? Expression { get; init; }
	public string? Motion { get; init; }
}

/// <summary>
/// 解析伴侣互动的严格 JSON 响应。
/// 互动响应不允许携带工具调用或协议外的执行指令。
/// </summary>
public static class PetInteractionReactionParser
{
	public const int MaxTextLength = 120;
	public const int MaxEmotionLength = 32;
	public const int MaxActionLength = 128;

	public static PetInteractionReaction Parse(string raw)
	{
		string json = UnwrapCodeFence(raw);
		if (json.Length == 0) throw new InvalidOperationException("AI 互动响应为空");

		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("AI 互动响应必须是 JSON 对象");
			if (HasToolField(root)) throw new InvalidOperationException("AI 互动响应不允许调用工具");

			return new PetInteractionReaction
			{
				Text = ReadOptionalText(root, "text", MaxTextLength),
				Emotion = ReadOptionalText(root, "emotion", MaxEmotionLength),
				Expression = ReadOptionalText(root, "expression", MaxActionLength),
				Motion = ReadOptionalText(root, "action", MaxActionLength),
			};
		}
		catch (JsonException exception)
		{
			throw new InvalidOperationException("AI 互动响应不是有效 JSON", exception);
		}
	}

	private static string UnwrapCodeFence(string raw)
	{
		string text = raw?.Trim() ?? "";
		if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
		int firstLineEnd = text.IndexOf('\n');
		if (firstLineEnd < 0) throw new InvalidOperationException("AI 互动代码块格式无效");
		int closing = text.LastIndexOf("```", StringComparison.Ordinal);
		if (closing <= firstLineEnd) throw new InvalidOperationException("AI 互动代码块未闭合");
		return text[(firstLineEnd + 1)..closing].Trim();
	}

	private static bool HasToolField(JsonElement root) =>
		root.TryGetProperty("tool_call", out _)
		|| root.TryGetProperty("tool_calls", out _)
		|| root.TryGetProperty("function_call", out _)
		|| root.TryGetProperty("recipient", out _);

	private static string? ReadOptionalText(JsonElement root, string name, int maxLength)
	{
		if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null) return null;
		if (element.ValueKind != JsonValueKind.String) throw new InvalidOperationException($"AI 互动字段不是字符串: {name}");
		string value = element.GetString()?.Trim() ?? "";
		if (value.Length == 0) return null;
		if (value.Length > maxLength) throw new InvalidOperationException($"AI 互动字段过长: {name}");
		if (value.Contains("```", StringComparison.Ordinal)) throw new InvalidOperationException($"AI 互动文本不允许 Markdown: {name}");
		return value;
	}
}
