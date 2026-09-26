using System.Text.Json;
using Nori.Core.Mcp;

namespace Nori.Desktop.Bridge;

public sealed partial class BridgeCommands
{
	private static bool HasProperty(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out _);

	private static string ReadFiniteNumber(JsonElement element, string label, float min, float max)
	{
		if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out float value)
			|| !float.IsFinite(value) || value < min || value > max)
		{
			throw new InvalidOperationException($"{label}必须在 {min} 到 {max} 之间");
		}
		return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}

	private static string ReadInteger(JsonElement element, string label, int min, int max)
	{
		if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value)
			|| value < min || value > max)
		{
			throw new InvalidOperationException($"{label}必须是 {min} 到 {max} 之间的整数");
		}
		return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}

	// ===================================================================
	// 通用辅助
	// ===================================================================

	private static string Str(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? ""
			: throw new InvalidOperationException($"缺少参数: {name}");

	private static Guid ParseGuid(JsonElement args, string name)
	{
		string value = Str(args, name);
		return Guid.TryParse(value, out Guid result) && result != Guid.Empty
			? result
			: throw new InvalidOperationException($"参数 {name} 无效");
	}

	private static bool RequiredBool(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty(name, out JsonElement value)
		&& value.ValueKind is JsonValueKind.True or JsonValueKind.False
			? value.GetBoolean()
			: throw new InvalidOperationException($"缺少参数: {name}");

	private static bool? OptionalBool(JsonElement args, string name)
	{
		if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value))
		{
			return value.ValueKind switch
			{
				JsonValueKind.True => true,
				JsonValueKind.False => false,
				_ => null,
			};
		}
		return null;
	}

	private static string? OptionalStr(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static bool HasTtsConfigurationChange(JsonElement args)
	{
		if (args.ValueKind != JsonValueKind.Object) return false;
		string[] keys = [
			"ttsProvider", "ttsBaseUrl", "ttsApiKey", "ttsVoice", "ttsSpeed",
			"gptsovitsBaseUrl", "gptsovitsRefAudio", "gptsovitsPromptText", "gptsovitsPromptLang",
		];
		return keys.Any(key => args.TryGetProperty(key, out _));
	}

	private static double Num(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
			? value.GetDouble()
			: throw new InvalidOperationException($"缺少参数: {name}");

	private static double? OptionalDouble(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
			? value.GetDouble()
			: null;

	private static int? OptionalInt(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
			? value.GetInt32()
			: null;

	private static int ClampLimit(int? value, int fallback) => Math.Clamp(value ?? fallback, 1, 100);

	private static McpServerConfig ParseMcpConfig(JsonElement args)
	{
		McpServerConfig? config = args.Deserialize<McpServerConfig>(BridgeJson.Options);
		return config ?? throw new InvalidOperationException("无法解析 MCP 服务器配置");
	}

	private static object? Run(Func<object?> action)
	{
		action();
		return null;
	}

	private static object? Run(Action action)
	{
		action();
		return null;
	}

	private Task<T> OnUi<T>(Func<T> action) => _uiDispatcher.InvokeAsync(action);

	private Task OnUiAsync(Func<Task> action) => _uiDispatcher.InvokeTaskAsync(action);
}
