using System.Text.Json.Serialization;

namespace Nori.Core.Resources;

/// <summary>
/// 资源类型
///
/// 所有应用资源都通过 ResourceType 管理. 字符串值用于本地目录 / 前端传参 / 日志.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResourceType>))]
public enum ResourceType
{
	/// <summary>Live2D 模型</summary>
	Live2D,
}

/// <summary>
/// 资源类型扩展
/// </summary>
public static class ResourceTypeExtensions
{
	/// <summary>
	/// 资源在 data/resources 下的目录名
	/// </summary>
	public static string AsString(this ResourceType type) => type switch
	{
		ResourceType.Live2D => "live2d",
		_ => throw new ArgumentOutOfRangeException(nameof(type)),
	};
}

/// <summary>
/// 资源信息
/// </summary>
public sealed record ResourceInfo
{
	/// <summary>资源名称</summary>
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	/// <summary>资源类型</summary>
	[JsonPropertyName("resourceType")]
	public required ResourceType ResourceType { get; init; }

	/// <summary>
	/// 资源实际路径
	///
	/// 属于宿主内部文件系统信息, 前端只用于展示, 不应据此直接访问文件
	/// </summary>
	[JsonPropertyName("path")]
	public required string Path { get; init; }

	/// <summary>资源总大小 (字节)</summary>
	[JsonPropertyName("size")]
	public required long Size { get; init; }
}

/// <summary>
/// 资源相关错误, 消息直接展示给用户
/// </summary>
public sealed class ResourceException(string message, Exception? inner = null) : Exception(message, inner);
