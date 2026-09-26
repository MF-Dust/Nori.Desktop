using System.Text.Json;

namespace Nori.Desktop;

/// <summary>快照 JSON 的严格读取。缺失、null 和类型不符走缺省，数字字符串与 "true" 不另作解析。</summary>
internal static class SnapshotJson
{
	internal static JsonElement P(JsonElement value, string key) =>
		value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out JsonElement result) ? result : default;

	internal static string S(JsonElement value, string key, string fallback = "")
	{
		JsonElement item = P(value, key);
		return item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? fallback : item.ToString();
	}

	internal static bool B(JsonElement value, string key) => P(value, key).ValueKind == JsonValueKind.True;

	internal static double N(JsonElement value, string key, double fallback = 0)
	{
		JsonElement item = P(value, key);
		return item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out double number) ? number : fallback;
	}

	internal static long Id(JsonElement value, string key)
	{
		JsonElement item = P(value, key);
		return item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out long number) ? number : 0;
	}

	internal static IEnumerable<JsonElement> Items(JsonElement value) =>
		value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}
