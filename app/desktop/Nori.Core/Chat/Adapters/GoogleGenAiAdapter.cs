using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Nori.Core.Network;

namespace Nori.Core.Chat.Adapters;

/// <summary>
/// Google GenAI 模型目录适配器 (/models)
/// 聊天与流式对话统一走 ChatClientLlmAdapter (Microsoft.Extensions.AI)。
/// </summary>
public sealed class GoogleGenAiAdapter(HttpClient httpClient) : IModelCatalogAdapter
{
	private readonly HttpClient _httpClient = httpClient;

	public async Task<IReadOnlyList<string>> FetchModelsAsync(
		string baseUrl,
		string apiKey,
		CancellationToken cancellationToken = default)
	{
		string endpoint = FormatEndpoint(baseUrl, "models");

		using HttpRequestMessage request = new(HttpMethod.Get, ChatEndpoint.CreateHttpUri(endpoint));
		request.Headers.Add("x-goog-api-key", apiKey);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

		HttpResponseMessage? response = null;
		try
		{
			response = await _httpClient.SendAsync(request, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception)
		{
			// 请求失败回退内置列表
		}

		if (response is not null)
		{
			using (response)
			{
				if (response.IsSuccessStatusCode)
				{
					try
					{
						string text = await UrlAccessPolicy.ReadCappedTextAsync(
							response.Content, UrlAccessPolicy.MaxResponseBytes, cancellationToken);
						JsonNode? body = JsonNode.Parse(text);
						if (body?["models"] is JsonArray modelsArray && modelsArray.Count > 0)
						{
							SortedSet<string> models = new(StringComparer.Ordinal);
							foreach (JsonNode? item in modelsArray)
							{
								if (item?["name"] is JsonValue nameVal && nameVal.TryGetValue(out string? name) && !string.IsNullOrWhiteSpace(name))
								{
									// 支持判断是否包含 generateContent
									if (item["supportedGenerationMethods"] is JsonArray methods)
									{
										bool supportsGenerate = methods.Any(m => m is JsonValue mv && mv.TryGetValue(out string? s) && s == "generateContent");
										if (!supportsGenerate) continue;
									}

									models.Add(NormalizeModelName(name));
								}
							}
							if (models.Count > 0) return [.. models];
						}
					}
					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
					{
						throw;
					}
					catch
					{
						// 解析异常回退
					}
				}
			}
		}

		// 回退内置常见 Gemini 模型列表
		return
		[
			"gemini-2.5-flash",
			"gemini-2.5-pro",
			"gemini-2.0-flash",
			"gemini-1.5-flash",
			"gemini-1.5-pro",
		];
	}

	private static string NormalizeModelName(string model)
	{
		string trimmed = model.Trim();
		if (trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
		{
			return trimmed["models/".Length..];
		}
		return trimmed;
	}

	private static string FormatEndpoint(string baseUrl, string path)
	{
		baseUrl = baseUrl.Trim().TrimEnd('/');
		if (baseUrl.Length == 0) throw new ChatException("Base URL 不能为空");

		if (path == "models")
		{
			if (baseUrl.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) return baseUrl;
			return $"{baseUrl}/models";
		}

		return $"{baseUrl}/{path}";
	}
}
