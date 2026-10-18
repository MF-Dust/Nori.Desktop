using Microsoft.Extensions.AI;

namespace Nori.PluginRuntime;

/// <summary>仅在用户选择此提供商时交给插件的连接配置，不包含其他服务的配置。</summary>
public sealed record PluginChatClientOptions(string BaseUrl, string ApiKey, string Model);

/// <summary>
/// 插件聊天提供商（API 2.4）。客户端遵循 Microsoft.Extensions.AI，支持文本、图片、流式输出和工具调用。
/// 宿主拥有每次 CreateClient 返回的客户端；插件不得返回共享的进程级单例。
/// </summary>
public interface IPluginChatProviderContribution : IPluginContribution
{
	/// <summary>插件内唯一的稳定 ID。</summary>
	string Id { get; }

	/// <summary>模型服务设置中的显示名称。</summary>
	string Name { get; }

	/// <summary>默认接口地址；本地客户端可留空。</summary>
	string DefaultBaseUrl => "";

	/// <summary>是否需要用户配置 API 密钥；本地客户端可返回 false。</summary>
	bool RequiresApiKey => true;

	/// <summary>创建由宿主负责释放的独立客户端，不得在此执行阻塞网络请求。</summary>
	IChatClient CreateClient(PluginChatClientOptions options);

	/// <summary>查询模型 ID；宿主不会将返回内容写入聊天历史。</summary>
	ValueTask<IReadOnlyList<string>> GetModelsAsync(PluginChatClientOptions options, CancellationToken cancellationToken);
}

[PluginCapability(PluginCapabilityIds.ChatProvider)]
internal sealed class ChatProviderCapability : IPluginCapability
{
}
