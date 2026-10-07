using System.Text.Json.Nodes;

namespace Nori.PluginRuntime;

/// <summary>插件描述符。身份、版本与入口信息来自已验证的 manifest.json。</summary>
public sealed record PluginDescriptor
{
	/// <summary>插件唯一 ID。</summary>
	public required string Id { get; init; }

	/// <summary>插件名称。</summary>
	public required string Name { get; init; }

	/// <summary>插件描述。</summary>
	public required string Description { get; init; }

	/// <summary>插件语义化版本。</summary>
	public required string Version { get; init; }

	/// <summary>插件 API 版本。</summary>
	public required string ApiVersion { get; init; }

	/// <summary>插件 manifest 声明的能力。</summary>
	public IReadOnlyList<string> Capabilities { get; init; } = [];
}

/// <summary>受信任的进程内插件入口。AssemblyLoadContext 不是安全沙箱。</summary>
public interface INoriPlugin
{
	/// <summary>激活插件并注册贡献。</summary>
	ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken);

	/// <summary>停用插件并释放自身资源。</summary>
	ValueTask DeactivateAsync(CancellationToken cancellationToken);
}

/// <summary>插件可见的最小宿主上下文。</summary>
public interface IPluginContext
{
	PluginDescriptor Plugin { get; }
	IPluginLogger Logger { get; }
	IPluginStorage Storage { get; }
	IPluginAssets Assets { get; }
	IContributionRegistry Contributions { get; }
	IPluginCapabilities Capabilities { get; }
	CancellationToken StoppingToken { get; }
}

/// <summary>插件日志接口，不暴露宿主日志实现。</summary>
public interface IPluginLogger
{
	void Debug(string message);
	void Info(string message);
	void Warn(string message);
	void Error(string message, Exception? exception = null);
}

/// <summary>插件逻辑 KV/JSON 存储。</summary>
public interface IPluginStorage
{
	ValueTask<JsonNode?> GetAsync(string key, CancellationToken cancellationToken = default);
	ValueTask SetAsync(string key, JsonNode? value, CancellationToken cancellationToken = default);
	ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>插件包公开资源。</summary>
public interface IPluginAssets
{
	Stream OpenRead(string relativePath);
	Uri GetUri(string relativePath);
}

/// <summary>插件提供的贡献标记。</summary>
public interface IPluginContribution
{
}

/// <summary>
/// 插件向宿主贡献的可执行动作。
/// 宿主会把活跃插件的动作注册为 AI 工具 (plugin__&lt;pluginId&gt;__&lt;actionId&gt;)，供伴侣对话调用。
/// </summary>
public interface IPluginActionContribution : IPluginContribution
{
	/// <summary>动作 ID (插件内唯一，用于宿主工具名)。</summary>
	string Id { get; }

	/// <summary>面向模型的动作描述。</summary>
	string Description { get; }

	/// <summary>参数 JSON Schema (可为 null 表示无参数)。</summary>
	JsonNode? ParametersSchema { get; }

	/// <summary>执行动作并返回可序列化结果。</summary>
	Task<JsonObject?> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken);
}

/// <summary>原生页面惰性工厂；宿主只在 UI 线程创建页面。</summary>
public interface IPluginPageContribution : IPluginContribution
{
	string Id { get; }
	string Title { get; }
	IPluginPage CreatePage();
}

/// <summary>独占的未挂载控件与页面资源；宿主在 UI 线程释放。</summary>
public interface IPluginPage : IDisposable
{
	Avalonia.Controls.Control Control { get; }
}

/// <summary>一个插件贡献注册项的可撤销句柄。</summary>
public interface IPluginRegistration : IDisposable
{
}

/// <summary>插件贡献注册表。注册项的所有权属于当前插件上下文。</summary>
public interface IContributionRegistry
{
	IPluginRegistration Register<T>(T contribution)
		where T : class, IPluginContribution;
}

/// <summary>插件声明、授权与宿主实现的独立状态。</summary>
public sealed record PluginCapabilityStatus(
	string Id,
	bool Declared,
	bool Granted,
	bool Available);

/// <summary>宿主向插件提供的能力标记。</summary>
public interface IPluginCapability
{
}

/// <summary>插件能力查询。</summary>
public interface IPluginCapabilities
{
	bool TryGet<T>(out T? capability)
		where T : class, IPluginCapability;

	T GetRequired<T>()
		where T : class, IPluginCapability;

	IReadOnlyList<PluginCapabilityStatus> Statuses { get; }
}

/// <summary>能力接口或实现的 manifest 能力标识。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class PluginCapabilityAttribute(string id) : Attribute
{
	/// <summary>能力 ID。</summary>
	public string Id { get; } = string.IsNullOrWhiteSpace(id)
		? throw new ArgumentException("能力 ID 不能为空", nameof(id))
		: id;
}

/// <summary>宿主支持的界面能力标识。</summary>
public static class PluginCapabilityIds
{
	public const string AvaloniaUi = "ui.avalonia";
}

[PluginCapability(PluginCapabilityIds.AvaloniaUi)]
internal sealed class AvaloniaPageCapability : IPluginCapability
{
}

/// <summary>插件边界错误，Code 是稳定的机器可读错误码。</summary>
public class PluginException : Exception
{
	public PluginException(string code, string message)
		: base(message)
	{
		Code = code;
	}

	public PluginException(string code, string message, Exception innerException)
		: base(message, innerException)
	{
		Code = code;
	}

	public string Code { get; }

	// ---- 宿主侧装配的脱敏诊断 (仅用于日志与遥测标签, 不含完整安装路径/用户目录/私有配置) ----

	/// <summary>失败插件的 manifest ID。</summary>
	public string? DiagnosticPluginId { get; internal set; }

	/// <summary>失败插件的 manifest 版本。</summary>
	public string? DiagnosticPluginVersion { get; internal set; }

	/// <summary>宿主 API 版本 (major.minor)。</summary>
	public string? DiagnosticHostApiVersion { get; internal set; }

	/// <summary>宿主产品版本。</summary>
	public string? DiagnosticHostVersion { get; internal set; }

	/// <summary>根因异常类型全名 (不含消息正文)。</summary>
	public string? DiagnosticExceptionType { get; internal set; }

	/// <summary>TypeLoadException.TypeName (截断, 仅类型名)。</summary>
	public string? DiagnosticTypeLoadTypeName { get; internal set; }

	/// <summary>FileLoadException 涉及的程序集文件名 (仅文件名, 非完整路径)。</summary>
	public string? DiagnosticAssemblyName { get; internal set; }
}
