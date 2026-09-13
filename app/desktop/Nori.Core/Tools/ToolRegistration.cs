using System.Text.Json.Nodes;

namespace Nori.Core.Tools;

/// <summary>
/// 工具参数的一条属性。
///
/// 类型由工厂方法显式指定，不从参数名推断。此前 <c>BuiltinTools</c> 按名字硬编码判断
/// （`importance` / `intensity` / `delayMinutes` 算数字，其余算字符串），新增一个数值参数
/// 会静默变成 `"string"`，而模型只会在传值被拒之后才知道 —— 这类错误没有编译期信号。
/// </summary>
public sealed record ToolProperty(string Name, string Description)
{
	/// <summary>是否必填。</summary>
	public bool Required { get; init; } = true;

	/// <summary>JSON Schema 类型名。</summary>
	public string JsonType { get; init; } = "string";

	/// <summary>取值枚举；为空表示不限。</summary>
	public IReadOnlyList<string>? Choices { get; init; }

	/// <summary>字符串参数。</summary>
	public static ToolProperty Text(string name, string description, bool required = true) =>
		new(name, description) {Required = required};

	/// <summary>数值参数。</summary>
	public static ToolProperty Number(string name, string description, bool required = true) =>
		new(name, description) {Required = required, JsonType = "number"};

	/// <summary>布尔参数；默认非必填，缺省即为 false。</summary>
	public static ToolProperty Boolean(string name, string description, bool required = false) =>
		new(name, description) {Required = required, JsonType = "boolean"};

	/// <summary>对象参数。</summary>
	public static ToolProperty Object(string name, string description, bool required = true) =>
		new(name, description) {Required = required, JsonType = "object"};

	/// <summary>
	/// 取值受限的字符串参数。
	///
	/// 枚举写进 schema 之后模型在调用前就知道有哪些取值，不必靠试错；服务端仍要再校验一次，
	/// schema 是给模型的提示而不是约束。
	/// </summary>
	public static ToolProperty Choice(
		string name, string description, IReadOnlyList<string> choices, bool required = true) =>
		new(name, description) {Required = required, Choices = choices};
}

/// <summary>
/// 工具注册与参数 schema 的统一入口。
///
/// 此前三族工具各写各的：<c>BuiltinTools</c> 与 <c>WorkspaceTools</c> 各有一份逐字节相同的
/// <c>Register</c>，两份签名不同的 <c>Schema</c>，而 <c>TaskTools</c> 两个都不用、直接内联
/// 构造 <see cref="JsonObject"/>。新增一族工具时照哪份抄全凭运气。
/// </summary>
public static class ToolRegistration
{
	/// <summary>按属性列表生成 JSON Schema。不传属性即为无参工具。</summary>
	public static JsonObject Schema(params ToolProperty[] properties)
	{
		ArgumentNullException.ThrowIfNull(properties);
		JsonObject props = new();
		JsonArray required = [];

		foreach (ToolProperty property in properties)
		{
			JsonObject entry = new() {["type"] = property.JsonType, ["description"] = property.Description};
			if (property.Choices is {Count: > 0} choices)
			{
				JsonArray values = [];
				foreach (string choice in choices) values.Add(choice);
				entry["enum"] = values;
			}

			props[property.Name] = entry;
			if (property.Required) required.Add(property.Name);
		}

		return new JsonObject {["type"] = "object", ["properties"] = props, ["required"] = required};
	}

	/// <summary>注册一件工具。</summary>
	public static void Register(
		ToolRegistry registry,
		string name,
		string description,
		string permissionLevel,
		JsonObject parameters,
		Func<JsonNode?, ToolContext, Task<object?>> execute)
	{
		ArgumentNullException.ThrowIfNull(registry);
		registry.Register(new RegisteredTool
		{
			Name = name,
			Description = description,
			Parameters = parameters,
			PermissionLevel = permissionLevel,
			Execute = execute,
		});
	}
}
