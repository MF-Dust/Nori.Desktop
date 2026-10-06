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
