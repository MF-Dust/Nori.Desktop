using System.Globalization;
using System.Text.Json;

namespace Nori.Desktop.Settings;

/// <summary>从运行时快照安全读取设置字段。</summary>
public static class SettingsSnapshotReader
{
	/// <summary>按路径读取 JSON 节点。</summary>
	public static JsonElement? Get(JsonElement root, params string[] path)
	{
		JsonElement current = root;
		foreach (string name in path)
		{
			if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return null;
		}
		return current;
	}

	/// <summary>读取字符串，缺失时返回空串。</summary>
	public static string String(JsonElement root, string fallback, params string[] path)
	{
		JsonElement? value = Get(root, path);
		if (value is not { } element) return fallback;
		return element.ValueKind switch
		{
			JsonValueKind.String => element.GetString() ?? fallback,
			JsonValueKind.Number => element.ToString(),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => fallback,
		};
	}

	/// <summary>读取布尔，兼容历史 0/1 字符串。</summary>
	public static bool Boolean(JsonElement root, bool fallback, params string[] path)
	{
		JsonElement? value = Get(root, path);
		if (value is not { } element) return fallback;
		if (element.ValueKind == JsonValueKind.True) return true;
		if (element.ValueKind == JsonValueKind.False) return false;
		string raw = String(root, string.Empty, path);
		return raw switch
		{
			"1" => true,
			"0" => false,
			_ when bool.TryParse(raw, out bool parsed) => parsed,
			_ => fallback,
		};
	}

	/// <summary>读取数字。</summary>
	public static double Number(JsonElement root, double fallback, params string[] path)
	{
		JsonElement? value = Get(root, path);
		if (value is not { } element) return fallback;
		if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double number)) return number;
		return double.TryParse(String(root, string.Empty, path), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : fallback;
	}

	/// <summary>读取可选密钥状态。</summary>
	public static bool SecretConfigured(JsonElement root, params string[] path)
	{
		JsonElement? value = Get(root, path);
		if (value is not { } element) return false;
		return element.ValueKind switch
		{
			JsonValueKind.True => true,
			JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
			_ => false,
		};
	}
}
