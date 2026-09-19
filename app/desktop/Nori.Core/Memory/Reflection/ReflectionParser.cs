using System.Globalization;
using System.Text.Json;

namespace Nori.Core.Memory;

/// <summary>Reflection JSON 的安全解析器。</summary>
public static class ReflectionParser
{
	private const int MaxFacts = 20;
	private const int MaxEvidence = 20;
	private const int MaxTopics = 20;

	public static ReflectionResult Parse(string raw)
	{
		ArgumentNullException.ThrowIfNull(raw);
		using JsonDocument document = ExtractDocument(raw);
		JsonElement root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object)
		{
			throw Failure("validation", "root", raw.Length);
		}

		bool shouldStore = ParseBool(root, "shouldStore", raw.Length);
		string summary = ParseString(root, "summary", 2000, raw.Length);
		string persona = ParseString(root, "personaSummary", 1200, raw.Length);
		double importance = ParseScore(root, "importance", 0.5, raw.Length);
		IReadOnlyList<string> topics = ParseTopics(root, raw.Length);
		IReadOnlyList<ReflectionFact> facts = ParseFacts(root, raw.Length);

		return new ReflectionResult
		{
			ShouldStore = shouldStore,
			Summary = summary,
			PersonaSummary = persona,
			Topics = topics,
			Importance = importance,
			KeyFacts = facts,
		};
	}

	private static JsonDocument ExtractDocument(string raw)
	{
		string value = raw.Trim();
		if (value.Length == 0) throw Failure("json_extract", "root", raw.Length);

		if (value.StartsWith("```", StringComparison.Ordinal))
		{
			int firstLine = value.IndexOf('\n');
			int endFence = value.LastIndexOf("```", StringComparison.Ordinal);
			if (firstLine < 0 || endFence <= firstLine)
			{
				throw Failure("json_extract", "fence", raw.Length);
			}
			value = value[(firstLine + 1)..endFence].Trim();
		}

		value = StripLeadingThinkBlocks(value, raw.Length);
		JsonException? firstJsonError;
		try
		{
			JsonDocument complete = JsonDocument.Parse(value);
			if (complete.RootElement.ValueKind == JsonValueKind.Object) return complete;
			complete.Dispose();
			throw Failure("validation", "root", raw.Length);
		}
		catch (JsonException exception)
		{
			firstJsonError = exception;
		}

		JsonException? candidateError = null;
		int searchFrom = 0;
		bool foundOpeningBrace = false;
		while (searchFrom < value.Length)
		{
			int start = value.IndexOf('{', searchFrom);
			if (start < 0) break;
			foundOpeningBrace = true;
			if (!TryFindObjectEnd(value, start, out int end)) break;

			string candidate = value[start..(end + 1)];
			try
			{
				JsonDocument document = JsonDocument.Parse(candidate);
				if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
				document.Dispose();
			}
			catch (JsonException exception)
			{
				candidateError = exception;
			}
			searchFrom = start + 1;
		}

		if (foundOpeningBrace)
		{
			throw Failure("json_parse", "root", raw.Length, candidateError ?? firstJsonError);
		}
		throw Failure("json_extract", "root", raw.Length);
	}

	private static string StripLeadingThinkBlocks(string value, int payloadLength)
	{
		string remaining = value.TrimStart();
		while (remaining.StartsWith("<think", StringComparison.OrdinalIgnoreCase))
		{
			int openEnd = remaining.IndexOf('>');
			int close = openEnd < 0
				? -1
				: remaining.IndexOf("</think>", openEnd + 1, StringComparison.OrdinalIgnoreCase);
			if (openEnd < 0 || close < 0)
			{
				throw Failure("json_extract", "reasoning", payloadLength);
			}
			remaining = remaining[(close + "</think>".Length)..].TrimStart();
		}
		return remaining;
	}

	private static bool TryFindObjectEnd(string value, int start, out int end)
	{
		int depth = 0;
		bool inString = false;
		bool escaped = false;
		for (int index = start; index < value.Length; index++)
		{
			char character = value[index];
			if (inString)
			{
				if (escaped)
				{
					escaped = false;
					continue;
				}
				if (character == '\\')
				{
					escaped = true;
					continue;
				}
				if (character == '"') inString = false;
				continue;
			}

			if (character == '"')
			{
				inString = true;
				continue;
			}
			if (character == '{') depth++;
			if (character != '}') continue;
			depth--;
			if (depth == 0)
			{
				end = index;
				return true;
			}
		}
		end = -1;
		return false;
	}

	private static bool ParseBool(JsonElement root, string name, int payloadLength)
	{
		if (!TryGetProperty(root, name, out JsonElement value)) return false;
		if (value.ValueKind == JsonValueKind.True) return true;
		if (value.ValueKind == JsonValueKind.False) return false;
		if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed)) return parsed;
		throw Failure("validation", name, payloadLength);
	}

	private static string ParseString(JsonElement root, string name, int maxLength, int payloadLength)
	{
		if (!TryGetProperty(root, name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return "";
		if (value.ValueKind != JsonValueKind.String) throw Failure("validation", name, payloadLength);
		string result = value.GetString()?.Trim() ?? "";
		if (result.Length > maxLength) throw Failure("validation", name, payloadLength);
		return result;
	}

	private static double ParseScore(JsonElement root, string name, double fallback, int payloadLength)
	{
		if (!TryGetProperty(root, name, out JsonElement value)) return fallback;
		if (!TryParseScore(value, out double score)) throw Failure("validation", name, payloadLength);
		return score;
	}

	private static bool TryParseScore(JsonElement value, out double score)
	{
		bool parsed = value.ValueKind switch
		{
			JsonValueKind.Number => value.TryGetDouble(out score),
			JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float,
				CultureInfo.InvariantCulture, out score),
			_ => AssignInvalid(out score),
		};
		return parsed && double.IsFinite(score) && score is >= 0 and <= 1;
	}

	private static IReadOnlyList<string> ParseTopics(JsonElement root, int payloadLength)
	{
		if (!TryGetProperty(root, "topics", out JsonElement value) || value.ValueKind == JsonValueKind.Null) return [];
		if (value.ValueKind != JsonValueKind.Array) throw Failure("validation", "topics", payloadLength);
		List<string> topics = [];
		HashSet<string> seen = new(StringComparer.Ordinal);
		foreach (JsonElement item in value.EnumerateArray())
		{
			if (topics.Count == MaxTopics) break;
			if (item.ValueKind != JsonValueKind.String) continue;
			string topic = item.GetString()?.Trim() ?? "";
			if (topic.Length is 0 or > 120 || !seen.Add(topic)) continue;
			topics.Add(topic);
		}
		return topics;
	}

	private static IReadOnlyList<ReflectionFact> ParseFacts(JsonElement root, int payloadLength)
	{
		if (!TryGetProperty(root, "keyFacts", out JsonElement value) || value.ValueKind == JsonValueKind.Null) return [];
		if (value.ValueKind != JsonValueKind.Array) throw Failure("validation", "keyFacts", payloadLength);
		List<ReflectionFact> facts = [];
		foreach (JsonElement item in value.EnumerateArray().Take(MaxFacts))
		{
			if (TryParseFact(item) is { } fact) facts.Add(fact);
		}
		return facts;
	}

	private static ReflectionFact? TryParseFact(JsonElement item)
	{
		if (item.ValueKind != JsonValueKind.Object) return null;
		if (!TryReadString(item, "type", out string type) || !TryParseMemoryKind(type, out MemoryKind kind)) return null;
		if (!TryReadString(item, "content", out string content) || content.Length is 0 or > 600) return null;
		if (!TryGetProperty(item, "importance", out JsonElement importanceValue)
			|| !TryParseScore(importanceValue, out double importance)) return null;
		if (!TryGetProperty(item, "confidence", out JsonElement confidenceValue)
			|| !TryParseScore(confidenceValue, out double confidence) || confidence < 0.6) return null;
		if (!TryGetProperty(item, "evidence", out JsonElement evidenceValue)
			|| evidenceValue.ValueKind != JsonValueKind.Array) return null;

		string? expiresAt = null;
		if (TryGetProperty(item, "expiresAt", out JsonElement expiresValue))
		{
			if (expiresValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) return null;
			expiresAt = expiresValue.ValueKind == JsonValueKind.String ? expiresValue.GetString()?.Trim() : null;
			if (expiresAt?.Length > 120) return null;
		}

		return new ReflectionFact
		{
			Kind = kind,
			Content = content,
			Importance = importance,
			Confidence = confidence,
			Evidence = ParseEvidence(evidenceValue),
			ExpiresAt = expiresAt,
		};
	}

	private static IReadOnlyList<int> ParseEvidence(JsonElement value)
	{
		List<int> evidence = [];
		foreach (JsonElement item in value.EnumerateArray())
		{
			if (evidence.Count == MaxEvidence) break;
			int parsed;
			bool valid = item.ValueKind switch
			{
				JsonValueKind.Number => item.TryGetInt32(out parsed),
				JsonValueKind.String => int.TryParse(item.GetString(), NumberStyles.None,
					CultureInfo.InvariantCulture, out parsed),
				_ => AssignInvalid(out parsed),
			};
			if (valid && parsed >= 0) evidence.Add(parsed);
		}
		return evidence;
	}

	private static bool TryReadString(JsonElement root, string name, out string value)
	{
		if (TryGetProperty(root, name, out JsonElement element) && element.ValueKind == JsonValueKind.String)
		{
			value = element.GetString()?.Trim() ?? "";
			return true;
		}
		value = "";
		return false;
	}

	private static bool TryParseMemoryKind(string value, out MemoryKind kind)
	{
		kind = MemoryKindExtensions.Parse(value);
		return kind != MemoryKind.General || value.Equals("general", StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
	{
		if (root.TryGetProperty(name, out value)) return true;
		foreach (JsonProperty property in root.EnumerateObject())
		{
			if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
			value = property.Value;
			return true;
		}
		value = default;
		return false;
	}

	private static bool AssignInvalid(out double value)
	{
		value = double.NaN;
		return false;
	}

	private static bool AssignInvalid(out int value)
	{
		value = -1;
		return false;
	}

	private static ReflectionParseException Failure(string category, string stage, int payloadLength,
		JsonException? inner = null) => new(category, stage, payloadLength, inner);
}
