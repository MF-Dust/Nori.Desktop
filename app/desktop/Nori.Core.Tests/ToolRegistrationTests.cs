using System.Text.Json.Nodes;
using Nori.Core.Tools;
using static Nori.Core.Tools.ToolProperty;
using static Nori.Core.Tools.ToolRegistration;

namespace Nori.Core.Tests;

/// <summary>
/// 工具注册与参数 schema 的统一入口。
///
/// 三族工具此前各写各的：两份逐字节相同的 <c>Register</c>、两份签名不同的 <c>Schema</c>，
/// 外加一处内联构造。合并之后这里守住行为，尤其是参数类型 —— 那一项此前靠参数名硬编码推断。
/// </summary>
public sealed class ToolRegistrationTests
{
	private static JsonObject Properties(JsonObject schema) => schema["properties"]!.AsObject();

	private static string[] Required(JsonObject schema) =>
		[.. schema["required"]!.AsArray().Select(node => node!.GetValue<string>())];

	[Fact]
	public void 无参工具产出空属性与空必填()
	{
		JsonObject schema = Schema();

		Assert.Equal("object", schema["type"]!.GetValue<string>());
		Assert.Empty(Properties(schema));
		Assert.Empty(Required(schema));
	}

	/// <summary>
	/// 类型由工厂方法显式给出，不再从参数名推断。
	///
	/// 旧实现按名字硬编码判断（importance / intensity / delayMinutes 算数字，其余算字符串），
	/// 新增一个数值参数会静默变成 string，而模型只在传值被拒之后才知道 —— 没有编译期信号。
	/// </summary>
	[Fact]
	public void 参数类型由工厂方法决定而不是参数名()
	{
		JsonObject schema = Schema(
			Text("subject", "文本"),
			Number("score", "数值"),
			Boolean("force", "布尔"),
			Object("payload", "对象"));

		JsonObject props = Properties(schema);
		Assert.Equal("string", props["subject"]!["type"]!.GetValue<string>());
		Assert.Equal("number", props["score"]!["type"]!.GetValue<string>());
		Assert.Equal("boolean", props["force"]!["type"]!.GetValue<string>());
		Assert.Equal("object", props["payload"]!["type"]!.GetValue<string>());
	}

	/// <summary>一个叫 importance 的字符串参数必须是 string —— 这正是旧实现会判错的形状。</summary>
	[Fact]
	public void 名字像数值的字符串参数仍然是字符串()
	{
		Assert.Equal(
			"string",
			Properties(Schema(Text("importance", "这里是一段描述文本")))["importance"]!["type"]!.GetValue<string>());
	}

	[Fact]
	public void 必填项只收标记为必填的()
	{
		JsonObject schema = Schema(
			Text("a", "必填"),
			Text("b", "非必填", required: false),
			Number("c", "非必填", required: false));

		Assert.Equal(["a"], Required(schema));
	}

	/// <summary>布尔参数默认非必填：缺省即为 false，强制必填会让模型每次都要显式传。</summary>
	[Fact]
	public void 布尔参数默认非必填()
	{
		Assert.Empty(Required(Schema(Boolean("force", "开关"))));
	}

	[Fact]
	public void 枚举参数把取值写进schema()
	{
		JsonObject props = Properties(Schema(Choice("name", "任务名", ["构建", "测试"])));

		Assert.Equal(
			["构建", "测试"],
			props["name"]!["enum"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
	}

	[Fact]
	public void 没有枚举时不写enum字段()
	{
		Assert.False(Properties(Schema(Text("x", "文本")))["x"]!.AsObject().ContainsKey("enum"));
	}

	[Fact]
	public void 注册把五项原样写进工具()
	{
		ToolRegistry registry = new();

		Register(registry, "probeTool", "描述", "confirm", Schema(Text("x", "参数")), (_, _) => Task.FromResult<object?>(null));

		RegisteredTool tool = registry.Get("probeTool")!;
		Assert.Equal("描述", tool.Description);
		Assert.Equal("confirm", tool.PermissionLevel);
		Assert.True(Properties(tool.Parameters.AsObject()).ContainsKey("x"));
	}
}
