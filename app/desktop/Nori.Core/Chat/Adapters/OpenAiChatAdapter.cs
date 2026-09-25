using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Network;

namespace Nori.Core.Chat.Adapters;

/// <summary>
/// OpenAI 兼容模型目录适配器 (/models)
/// 聊天与流式对话统一走 ChatClientLlmAdapter (Microsoft.Extensions.AI)。
/// </summary>
public sealed class OpenAiChatAdapter(HttpClient httpClient) : IModelCatalogAdapter
{
	private readonly HttpClient _httpClient = httpClient;

	public async Task<IReadOnlyList<string>> FetchModelsAsync(
		string baseUrl,
		string apiKey,
		CancellationToken cancellationToken = default)
	{
		string endpoint = FormatEndpoint(baseUrl, "models");

		using HttpRequestMessage request = new(HttpMethod.Get, ChatEndpoint.CreateHttpUri(endpoint));
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

		HttpResponseMessage response;
		try
		{
			response = await _httpClient.SendAsync(request, cancellationToken);
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
		{
			throw new ChatException($"请求失败: {exception.Message}", exception);
		}

		using (response)
		{
			if (!response.IsSuccessStatusCode)
			{
				string errorText = await UrlAccessPolicy.ReadSafeProviderErrorAsync(response.Content, cancellationToken);
				throw new ChatException($"接口返回错误: HTTP {(int)response.StatusCode}{(errorText.Length > 0 ? $", {errorText}" : "")}");
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
				throw new ChatException($"解析响应失败: {exception.Message}", exception);
			}

			if (body?["data"] is not JsonArray data) throw new ChatException("接口返回成功，但缺少 data 字段");

			SortedSet<string> models = new(StringComparer.Ordinal);
			foreach (JsonNode? item in data)
			{
				if (item is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
				{
					models.Add(text);
					continue;
				}
				if (item?["id"] is JsonValue idValue && idValue.TryGetValue(out string? id) && !string.IsNullOrWhiteSpace(id))
				{
					models.Add(id);
				}
			}

			if (models.Count == 0) throw new ChatException("接口返回成功，但没有解析到任何模型");
			return [.. models];
		}
	}

	private static string FormatEndpoint(string baseUrl, string path)
	{
		baseUrl = baseUrl.Trim().TrimEnd('/');
		if (baseUrl.Length == 0) throw new ChatException("Base URL 不能为空");

		if (path == "models")
		{
			if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
			{
				baseUrl = baseUrl[..^"/chat/completions".Length].TrimEnd('/');
			}
			else if (baseUrl.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
			{
				baseUrl = baseUrl[..^"/responses".Length].TrimEnd('/');
			}
			if (baseUrl.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) return baseUrl;
			return $"{baseUrl}/models";
		}

		return $"{baseUrl}/{path}";
	}

}
