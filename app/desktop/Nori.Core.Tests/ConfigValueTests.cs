using System.Text.Json;
using Nori.Core.Configuration;

namespace Nori.Core.Tests;

/// <summary>
/// ConfigValue 的读时类型推断必须与 Rust 版 config.rs 逐字等价.
/// 这是整个移植里最容易静默出错的地方: 推断规则一变, 伴侣的缩放/表情配置会悄悄失效.
/// </summary>
public class ConfigValueTests
{
	[Theory]
	// 布尔: "1"/"true" 与 "0"/"false", 大小写不敏感
	[InlineData("1", typeof(ConfigValue.Boolean))]
	[InlineData("0", typeof(ConfigValue.Boolean))]
	[InlineData("true", typeof(ConfigValue.Boolean))]
	[InlineData("TRUE", typeof(ConfigValue.Boolean))]
	[InlineData("False", typeof(ConfigValue.Boolean))]
	// 整数
	[InlineData("42", typeof(ConfigValue.Integer))]
	[InlineData("-7", typeof(ConfigValue.Integer))]
	// JSON 容器
	[InlineData("{\"a\":1}", typeof(ConfigValue.Json))]
	[InlineData("[{\"group\":\"Idle\",\"names\":[\"a\"]}]", typeof(ConfigValue.Json))]
	// 其余一律字符串
	[InlineData("arg-nori", typeof(ConfigValue.Text))]
	[InlineData("zh-CN", typeof(ConfigValue.Text))]
	[InlineData("", typeof(ConfigValue.Text))]
	[InlineData("null", typeof(ConfigValue.Text))]
	[InlineData("\"quoted\"", typeof(ConfigValue.Text))]
	public void FromStorage_推断类型(string stored, Type expected) =>
		Assert.IsType(expected, ConfigValue.FromStorage(stored));

	[Fact]
	public void FromStorage_小数落到字符串而不是整数()
	{
		// l2d_scale 存的是 "1.25" 这类值: Rust 里 i64 解析失败, 又不是 JSON 容器, 最终是字符串.
		// 前端 parseNumber 依赖这一点.
		ConfigValue value = ConfigValue.FromStorage("1.25");
		ConfigValue.Text text = Assert.IsType<ConfigValue.Text>(value);
		Assert.Equal("1.25", text.Value);
	}

	[Theory]
	[InlineData("live2d")]
	[InlineData("1.25")]
	[InlineData("{\"a\":[1,2]}")]
	[InlineData("42")]
	[InlineData("1")]
	public void ToStorage_与FromStorage互为逆运算(string stored) =>
		Assert.Equal(stored, ConfigValue.FromStorage(stored).ToStorage());

	[Fact]
	public void ToStorage_布尔写成一和零()
	{
		Assert.Equal("1", new ConfigValue.Boolean(true).ToStorage());
		Assert.Equal("0", new ConfigValue.Boolean(false).ToStorage());
	}

	[Theory]
	// 前端 set_config 可能传字符串 / 数字 / 布尔 / 数组
	[InlineData("\"arg-nori\"", "arg-nori")]
	[InlineData("42", "42")]
	[InlineData("true", "1")]
	[InlineData("[\"a\",\"b\"]", "[\"a\",\"b\"]")]
	[InlineData("{\"k\":1}", "{\"k\":1}")]
	public void 反序列化前端传值后能正确入库(string json, string expectedStorage)
	{
		ConfigValue? value = JsonSerializer.Deserialize<ConfigValue>(json);
		Assert.NotNull(value);
		Assert.Equal(expectedStorage, value.ToStorage());
	}

	[Theory]
	// 序列化回前端时是裸值 (对应 serde untagged), 不是 {"Text": "..."} 这种包装
	[InlineData("arg-nori", "\"arg-nori\"")]
	public void 序列化成裸值(string stored, string expectedJson) =>
		Assert.Equal(expectedJson, JsonSerializer.Serialize(ConfigValue.FromStorage(stored)));

	[Fact]
	public void 装箱为object时依然序列化为裸值()
	{
		object boxedText = ConfigValue.FromStorage("arg-nori");
		Assert.Equal("\"arg-nori\"", JsonSerializer.Serialize(boxedText));
		object boxedInt = ConfigValue.FromStorage("42");
		Assert.Equal("42", JsonSerializer.Serialize(boxedInt));
		object boxedBool = ConfigValue.FromStorage("1");
		Assert.Equal("true", JsonSerializer.Serialize(boxedBool));
	}

	[Fact]
	public void 序列化布尔与整数为裸值()
	{
		Assert.Equal("true", JsonSerializer.Serialize(ConfigValue.FromStorage("1")));
		Assert.Equal("42", JsonSerializer.Serialize(ConfigValue.FromStorage("42")));
	}

	[Fact]
	public void AsStringOr_覆盖各类型()
	{
		Assert.Equal("arg-nori", ConfigValue.AsStringOr(ConfigValue.FromStorage("arg-nori"), "fallback"));
		Assert.Equal("42", ConfigValue.AsStringOr(ConfigValue.FromStorage("42"), "fallback"));
		Assert.Equal("true", ConfigValue.AsStringOr(ConfigValue.FromStorage("1"), "fallback"));
		Assert.Equal("fallback", ConfigValue.AsStringOr(null, "fallback"));
		// 空字符串视为缺失, 与 Rust 的 `if !value.is_empty()` 一致
		Assert.Equal("fallback", ConfigValue.AsStringOr(ConfigValue.FromStorage(""), "fallback"));
	}
}
