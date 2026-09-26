using System.Text.Json;
using System.Text.Json.Serialization;
using Nori.Core.Configuration;

namespace Nori.Desktop.Bridge;

/// <summary>
/// 桥接层 JSON 约定
/// </summary>
public static class BridgeJson
{
	/// <summary>
	/// 统一的序列化选项: 保持 camelCase, 不转义中文
	/// </summary>
	public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
	{
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters =
		{
			new ConfigValueJsonConverter(),
			new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
		},
	};
}

/// <summary>
/// 页面发过来的消息
/// </summary>
public sealed record BridgeMessage
{
	/// <summary>消息种类: invoke</summary>
	[JsonPropertyName("kind")]
	public string Kind { get; init; } = "";

	/// <summary>invoke 的调用序号</summary>
	[JsonPropertyName("id")]
	public long Id { get; init; }

	/// <summary>命令名</summary>
	[JsonPropertyName("cmd")]
	public string? Cmd { get; init; }

	/// <summary>命令参数</summary>
	[JsonPropertyName("args")]
	public JsonElement Args { get; init; }
}

/// <summary>
/// 回给页面的 invoke 结果
/// </summary>
public sealed record BridgeResult
{
	/// <summary>resolve / reject</summary>
	[JsonPropertyName("kind")]
	public required string Kind { get; init; }

	/// <summary>调用序号</summary>
	[JsonPropertyName("id")]
	public required long Id { get; init; }

	/// <summary>成功返回值</summary>
	[JsonPropertyName("value")]
	public object? Value { get; init; }

	/// <summary>失败信息, 直接展示给用户</summary>
	[JsonPropertyName("error")]
	public string? Error { get; init; }
}
