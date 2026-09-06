using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nori.Core.Configuration;

/// <summary>
/// 配置值类型: 支持基础类型和 JSON
///
/// 与 Rust 版 config.rs 的 ConfigValue 逐字等价, 包括"读取时重新推断类型"这一行为.
/// 前端 services/live2d/config.ts 的 parseNumber / parseExpressionList 就是为这个行为存在的,
/// 改动推断规则会让伴侣的缩放/表情配置静默失效.
/// </summary>
[JsonConverter(typeof(ConfigValueJsonConverter))]
public abstract record ConfigValue
{
	/// <summary>字符串值</summary>
	[JsonConverter(typeof(ConfigValueJsonConverter))]
	public sealed record Text(string Value) : ConfigValue;

	/// <summary>整数值</summary>
	[JsonConverter(typeof(ConfigValueJsonConverter))]
	public sealed record Integer(long Value) : ConfigValue;

	/// <summary>布尔值</summary>
	[JsonConverter(typeof(ConfigValueJsonConverter))]
	public sealed record Boolean(bool Value) : ConfigValue;

	/// <summary>JSON 对象或数组</summary>
	[JsonConverter(typeof(ConfigValueJsonConverter))]
	public sealed record Json(JsonNode Value) : ConfigValue;

	/// <summary>
	/// 转换成 SQLite 中保存的字符串
	/// </summary>
	public string ToStorage() => this switch
	{
		Text text => text.Value,
		Integer integer => integer.Value.ToString(CultureInfo.InvariantCulture),
		Boolean boolean => boolean.Value ? "1" : "0",
		Json json => json.Value.ToJsonString(),
		_ => string.Empty,
	};

	/// <summary>
	/// 从 SQLite 保存的字符串恢复 ConfigValue
	///
	/// 推断顺序与 Rust 版一致: 布尔 → 整数 → JSON 对象/数组 → 字符串
	/// </summary>
	public static ConfigValue FromStorage(string value)
	{
		// Boolean
		if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return new Boolean(true);
		if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return new Boolean(false);
		// Integer
		if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number)) return new Integer(number);
		// JSON: 只有对象与数组算 Json, 其余 (裸数字/裸字符串/null) 落到字符串
		if (LooksLikeJsonContainer(value))
		{
			try
			{
				JsonNode? node = JsonNode.Parse(value);
				if (node is JsonObject or JsonArray) return new Json(node);
			}
			catch (JsonException)
			{
				// 不是合法 JSON, 落到字符串
			}
		}
		// 默认字符串
		return new Text(value);
	}

	/// <summary>
	/// 读取字符串配置, 缺失/类型不符时返回 fallback (对应 Rust 的 get_str_or)
	/// </summary>
	public static string AsStringOr(ConfigValue? value, string fallback) => value switch
	{
		Text text when text.Value.Length > 0 => text.Value,
		Integer integer => integer.Value.ToString(CultureInfo.InvariantCulture),
		// Rust 用的是 bool 的 Display, 即 "true" / "false"
		Boolean boolean => boolean.Value ? "true" : "false",
		_ => fallback,
	};

	/// <summary>
	/// 快速判断是否可能是 JSON 对象或数组, 避免对普通字符串做一次昂贵的解析
	/// </summary>
	private static bool LooksLikeJsonContainer(string value)
	{
		ReadOnlySpan<char> span = value.AsSpan().Trim();
		return span.Length >= 2 && (span[0] == '{' || span[0] == '[');
	}
}

/// <summary>
/// ConfigValue 的 JSON 编解码
///
/// 对应 Rust 的 #[serde(untagged)]: 序列化成裸值, 反序列化按 JSON 实际类型还原.
/// 前端 invoke("set_config", {key, value}) 可能传字符串 / 数字 / 布尔 / 数组, 都要接得住.
/// </summary>
public sealed class ConfigValueJsonConverter : JsonConverter<ConfigValue>
{
	public override bool CanConvert(Type typeToConvert) => typeof(ConfigValue).IsAssignableFrom(typeToConvert);

	public override ConfigValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
	{
		JsonTokenType.String => new ConfigValue.Text(reader.GetString() ?? string.Empty),
		JsonTokenType.True => new ConfigValue.Boolean(true),
		JsonTokenType.False => new ConfigValue.Boolean(false),
		JsonTokenType.Number => ReadNumber(ref reader),
		JsonTokenType.Null => null,
		_ => new ConfigValue.Json(JsonNode.Parse(ref reader) ?? throw new JsonException("无法解析配置值")),
	};

	public override void Write(Utf8JsonWriter writer, ConfigValue value, JsonSerializerOptions options)
	{
		switch (value)
		{
			case ConfigValue.Text text:
				writer.WriteStringValue(text.Value);
				break;
			case ConfigValue.Integer integer:
				writer.WriteNumberValue(integer.Value);
				break;
			case ConfigValue.Boolean boolean:
				writer.WriteBooleanValue(boolean.Value);
				break;
			case ConfigValue.Json json:
				json.Value.WriteTo(writer, options);
				break;
			default:
				writer.WriteNullValue();
				break;
		}
	}

	/// <summary>
	/// 数字: 能放进 i64 的走 Integer, 小数按 Rust 的 untagged 顺序会落到 Json
	/// </summary>
	private static ConfigValue ReadNumber(ref Utf8JsonReader reader)
	{
		if (reader.TryGetInt64(out long number)) return new ConfigValue.Integer(number);
		return new ConfigValue.Json(JsonValue.Create(reader.GetDouble()));
	}
}
