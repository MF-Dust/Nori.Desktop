using System.Globalization;
using System.Text.Json;

namespace Nori.Desktop.Settings.Pages;

/// <summary>原生设置页使用的宽容 JSON 读取器，兼容 Bridge 的 camelCase 与旧快照字段。</summary>
internal static class SettingsJson
{
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		PropertyNameCaseInsensitive = true,
	};

	public static bool IsObject(JsonElement value) => value.ValueKind == JsonValueKind.Object;

	public static JsonElement Property(JsonElement value, string name)
	{
		if (value.ValueKind != JsonValueKind.Object) return default;
		if (value.TryGetProperty(name, out JsonElement exact)) return exact;
		foreach (JsonProperty property in value.EnumerateObject())
		{
			if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
		}
		return default;
	}

	public static bool Has(JsonElement value, string name) => Property(value, name).ValueKind != JsonValueKind.Undefined;

	public static string String(JsonElement value, string name, string fallback = "")
	{
		JsonElement item = Property(value, name);
		return item.ValueKind == JsonValueKind.String ? item.GetString() ?? fallback : fallback;
	}

	public static string? NullableString(JsonElement value, string name)
	{
		JsonElement item = Property(value, name);
		return item.ValueKind == JsonValueKind.String ? item.GetString() : null;
	}

	public static bool Bool(JsonElement value, string name, bool fallback = false)
	{
		JsonElement item = Property(value, name);
		if (item.ValueKind is JsonValueKind.True or JsonValueKind.False) return item.GetBoolean();
		if (item.ValueKind == JsonValueKind.String && bool.TryParse(item.GetString(), out bool parsed)) return parsed;
		if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number)) return number != 0;
		return fallback;
	}

	public static int Int(JsonElement value, string name, int fallback = 0)
	{
		JsonElement item = Property(value, name);
		if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number)) return number;
		if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
		return fallback;
	}

	public static long Long(JsonElement value, string name, long fallback = 0)
	{
		JsonElement item = Property(value, name);
		if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out long number)) return number;
		if (item.ValueKind == JsonValueKind.String && long.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
		return fallback;
	}

	public static double Double(JsonElement value, string name, double fallback = 0)
	{
		JsonElement item = Property(value, name);
		if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out double number)) return number;
		if (item.ValueKind == JsonValueKind.String && double.TryParse(item.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
		return fallback;
	}

	public static IEnumerable<JsonElement> Array(JsonElement value, string name)
	{
		JsonElement item = Property(value, name);
		return item.ValueKind == JsonValueKind.Array ? item.EnumerateArray() : [];
	}

	public static IEnumerable<JsonElement> RootArray(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray();
		if (value.ValueKind == JsonValueKind.Object)
		{
			foreach (string name in new[] {"items", "servers", "tools", "plugins", "skills"})
			{
				JsonElement nested = Property(value, name);
				if (nested.ValueKind == JsonValueKind.Array) return nested.EnumerateArray();
			}
		}
		return [];
	}

	public static JsonElement Object(JsonElement value, string name)
	{
		JsonElement item = Property(value, name);
		return item.ValueKind == JsonValueKind.Object ? item : default;
	}

	public static string RawOrString(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
		return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : value.GetRawText();
	}

	public static T? Deserialize<T>(JsonElement value)
	{
		if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return default;
		return JsonSerializer.Deserialize<T>(value.GetRawText(), Options);
	}

	public static JsonElement Parse(string text)
	{
		using JsonDocument document = JsonDocument.Parse(text);
		return document.RootElement.Clone();
	}

	public static JsonElement? TryParse(string text)
	{
		try { return Parse(text); }
		catch (JsonException) { return null; }
	}
}
