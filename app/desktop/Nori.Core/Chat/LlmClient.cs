namespace Nori.Core.Chat;

/// <summary>
/// LLM 接口客户端 (模型获取与管理)
/// </summary>
public sealed class LlmClient(HttpClient httpClient, CustomChatProviderRegistry? customProviders = null)
{
	private readonly HttpClient _httpClient = httpClient;

	/// <summary>与 ChatService 共享注入的扩展注册表；不使用静态插件状态。</summary>
	public CustomChatProviderRegistry CustomProviders { get; } = customProviders ?? new();

	/// <summary>
	/// 拉取指定协议的模型列表 (排序去重)
	/// </summary>
	public Task<IReadOnlyList<string>> FetchModelsAsync(
		string? providerStr,
		string baseUrl,
		string apiKey,
		CancellationToken cancellationToken = default)
	{
		if (LlmProviderExtensions.IsCustomProvider(providerStr))
			return CustomProviders.FetchModelsAsync(providerStr!.Trim(), baseUrl, apiKey, cancellationToken);
		LlmProvider provider = LlmProviderExtensions.ParseProvider(providerStr);
		return ProviderModelCatalog.FetchAsync(provider, _httpClient, baseUrl, apiKey, cancellationToken);
	}

	/// <summary>按原始 ID 创建请求适配器，插件不可用时明确失败，不回退至内置协议。</summary>
	public ILlmAdapter CreateAdapter(string? providerId, LlmGenerationOptions? generation = null) =>
		LlmProviderExtensions.IsCustomProvider(providerId)
			? CustomProviders.CreateAdapter(providerId!.Trim(), generation)
			: new ChatClientLlmAdapter(LlmProviderExtensions.ParseProvider(providerId), _httpClient, generation);

	/// <summary>保留现有内置协议调用方式。</summary>
	public static ILlmAdapter CreateAdapter(LlmProvider provider, HttpClient httpClient) =>
		ChatClientFactory.Create(provider, httpClient);
}
