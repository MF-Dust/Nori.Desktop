using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Network;

namespace Nori.Core.Chat.Adapters;

/// <summary>
/// Anthropic 模型目录适配器 (/models)
/// 聊天与流式对话统一走 ChatClientLlmAdapter (Microsoft.Extensions.AI)。
/// </summary>
public sealed class AnthropicAdapter(HttpClient httpClient) : IModelCatalogAdapter
{
	private const string AnthropicVersion = "2023-06-01";
	private readonly HttpClient _httpClient = httpClient;

	public async Task<IReadOnlyList<string>> FetchModelsAsync(
		string baseUrl,
		string apiKey,
		CancellationToken cancellationToken = default)
	{
		string endpoint = FormatEndpoint(baseUrl, "models");

		using HttpRequestMessage request = new(HttpMethod.Get, ChatEndpoint.CreateHttpUri(endpoint));
		request.Headers.Add("x-api-key", apiKey);
		request.Headers.Add("anthropic-version", AnthropicVersion);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

		HttpResponseMessage response;
		try
		{
			response = await _httpClient.SendAsync(request, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
		{
			throw new ChatException($"获取模型列表失败: {exception.Message}", exception);
		}

		using (response)
		{
			if (!response.IsSuccessStatusCode)
			{
				string errorText = await UrlAccessPolicy.ReadSafeProviderErrorAsync(response.Content, cancellationToken);
				throw new ChatException($"获取模型列表失败: HTTP {(int)response.StatusCode}{(errorText.Length > 0 ? $", {errorText}" : "")}");
			}

			JsonNode? body;
			try
			{
				string text = await UrlAccessPolicy.ReadCappedTextAsync(
					response.Content, UrlAccessPolicy.MaxResponseBytes, cancellationToken);
				body = JsonNode.Parse(text);
			}
			catch (JsonException exception)
			{
				throw new ChatException($"获取模型列表失败: 响应 JSON 无效: {exception.Message}", exception);
			}

			if (body?["data"] is not JsonArray data)
			{
				throw new ChatException("获取模型列表失败: 响应缺少 data 数组");
			}

			SortedSet<string> models = new(StringComparer.Ordinal);
			foreach (JsonNode? item in data)
			{
				if (item?["id"] is JsonValue idVal && idVal.TryGetValue(out string? id) && !string.IsNullOrWhiteSpace(id))
				{
					models.Add(id);
				}
			}

			if (models.Count == 0)
			{
				throw new ChatException("获取模型列表失败: 服务端返回了空模型列表");
			}
			return [.. models];
		}
	}

	private static string FormatEndpoint(string baseUrl, string path)
	{
		baseUrl = baseUrl.Trim().TrimEnd('/');
		if (baseUrl.Length == 0) throw new ChatException("Base URL 不能为空");

		if (path == "models")
		{
			if (baseUrl.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
			{
				baseUrl = baseUrl[..^"/messages".Length].TrimEnd('/');
			}
			if (baseUrl.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) return baseUrl;
			return $"{baseUrl}/models";
		}

		return $"{baseUrl}/{path}";
	}

}
