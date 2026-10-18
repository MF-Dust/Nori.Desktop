using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace Nori.Core.Chat;

/// <summary>插件聊天提供商的公开元数据；空默认地址表示允许无地址的本地客户端。</summary>
public sealed record CustomChatProviderDescriptor(string Id, string Name, string DefaultBaseUrl, bool RequiresApiKey);

/// <summary>实例拥有的聊天扩展注册表；只向宿主暴露元数据，不向外保留插件委托。</summary>
public sealed class CustomChatProviderRegistry
{
	private sealed record Registration(
		CustomChatProviderDescriptor Descriptor,
		Func<string, string, string, IChatClient> Factory,
		Func<string, string, CancellationToken, Task<IReadOnlyList<string>>> FetchModelsAsync);

	private readonly object _gate = new();
	private readonly Dictionary<string, Registration> _providers = new(StringComparer.Ordinal);

	/// <summary>注册完整的 plugin:插件ID:提供商ID；重复 ID 必须先撤销，避免覆盖另一项注册。</summary>
	public void Register(
		CustomChatProviderDescriptor descriptor,
		Func<string, string, string, IChatClient> factory,
		Func<string, string, CancellationToken, Task<IReadOnlyList<string>>> fetchModelsAsync)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		ArgumentNullException.ThrowIfNull(factory);
		ArgumentNullException.ThrowIfNull(fetchModelsAsync);
		ArgumentNullException.ThrowIfNull(descriptor.Id);
		string[] parts = descriptor.Id.Split(':');
		if (parts.Length != 3 || parts[0] != "plugin"
			|| parts.Skip(1).Any(part => string.IsNullOrWhiteSpace(part) || part.Any(char.IsWhiteSpace) || part.Any(char.IsControl)))
			throw new ArgumentException("插件提供商 ID 必须为 plugin:<pluginId>:<providerId>", nameof(descriptor));
		if (string.IsNullOrWhiteSpace(descriptor.Name)) throw new ArgumentException("提供商名称不能为空", nameof(descriptor));
		ArgumentNullException.ThrowIfNull(descriptor.DefaultBaseUrl);

		lock (_gate)
		{
			if (!_providers.TryAdd(descriptor.Id, new Registration(descriptor, factory, fetchModelsAsync)))
				throw new InvalidOperationException($"插件聊天提供商已注册: {descriptor.Id}");
		}
	}

	/// <summary>撤销注册并释放注册表持有的插件委托；已开始的请求由各自客户端负责结束。</summary>
	public bool Unregister(string providerId)
	{
		lock (_gate) return _providers.Remove(providerId);
	}

	/// <summary>取得不包含工厂或回调的元数据快照。</summary>
	public IReadOnlyList<CustomChatProviderDescriptor> GetProviders()
	{
		lock (_gate) return _providers.Values.Select(registration => registration.Descriptor).ToArray();
	}

	/// <summary>按稳定 ID 查询元数据。</summary>
	public bool TryGet(string providerId, [NotNullWhen(true)] out CustomChatProviderDescriptor? descriptor)
	{
		lock (_gate)
		{
			descriptor = _providers.TryGetValue(providerId, out Registration? registration) ? registration.Descriptor : null;
			return descriptor is not null;
		}
	}

	/// <summary>适配器仅持有注册表与 ID；每次调用重新解析工厂，撤销后不能继续创建客户端。</summary>
	public ILlmAdapter CreateAdapter(string providerId, LlmGenerationOptions? generation = null)
	{
		_ = GetRegistration(providerId);
		return new ChatClientLlmAdapter((baseUrl, apiKey, model) => CreateClient(providerId, baseUrl, apiKey, model), generation);
	}

	/// <summary>使用同一地址、密钥与取消令牌取得插件模型列表，不回退到内置服务。</summary>
	public Task<IReadOnlyList<string>> FetchModelsAsync(
		string providerId,
		string baseUrl,
		string apiKey,
		CancellationToken cancellationToken = default)
	{
		Registration registration = GetRegistration(providerId);
		string url = ResolveConfiguration(registration.Descriptor, baseUrl, apiKey);
		cancellationToken.ThrowIfCancellationRequested();
		return registration.FetchModelsAsync(url, apiKey, cancellationToken);
	}

	internal static ChatException Unavailable(string providerId) =>
		new($"插件聊天提供商不可用，请启用或重新安装对应插件: {providerId}");

	private Registration GetRegistration(string providerId)
	{
		lock (_gate) return _providers.TryGetValue(providerId, out Registration? registration)
			? registration : throw Unavailable(providerId);
	}

	private IChatClient CreateClient(string providerId, string baseUrl, string apiKey, string model)
	{
		Registration registration = GetRegistration(providerId);
		string url = ResolveConfiguration(registration.Descriptor, baseUrl, apiKey);
		return registration.Factory(url, apiKey, model) ?? throw new ChatException("插件提供商未返回聊天客户端");
	}

	private static string ResolveConfiguration(CustomChatProviderDescriptor descriptor, string baseUrl, string apiKey)
	{
		if (descriptor.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey)) throw new ChatException("API Key 不能为空");
		return (string.IsNullOrWhiteSpace(baseUrl) ? descriptor.DefaultBaseUrl : baseUrl).Trim().TrimEnd('/');
	}
}
