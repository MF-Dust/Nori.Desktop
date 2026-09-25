using System.ClientModel;
using System.ClientModel.Primitives;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Embeddings;
using AiEmbeddingGenerationOptions = Microsoft.Extensions.AI.EmbeddingGenerationOptions;
using Nori.Core.Network;

namespace Nori.Core.Embedding;

/// <summary>
/// OpenAI 规范 Embedding 适配器。
/// 使用官方 EmbeddingClient + IEmbeddingGenerator，并在进程内用 8 MiB 分布式缓存包装。
/// </summary>
public sealed class OpenAiEmbeddingAdapter(HttpClient httpClient) : IEmbeddingAdapter
{
	private const long CacheSizeLimit = 8 * 1024 * 1024;
	private readonly HttpClient _httpClient = httpClient;
	private readonly Lock _gate = new();
	private GeneratorState? _state;
	private HttpClient? _sdkHttpClient;
	private long _cacheEpoch;

	public async Task<float[]> GetEmbeddingAsync(
		string baseUrl,
		string apiKey,
		string model,
		string input,
		int? dimensions = null,
		CancellationToken cancellationToken = default)
	{
		IReadOnlyList<float[]> results = await GetEmbeddingsAsync(baseUrl, apiKey, model, [input], dimensions, cancellationToken);
		if (results.Count == 0) throw new InvalidOperationException("Embedding 服务未返回任何向量数据。");
		return results[0];
	}

	public async Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(
		string baseUrl,
		string apiKey,
		string model,
		IReadOnlyList<string> inputs,
		int? dimensions = null,
		CancellationToken cancellationToken = default)
	{
		if (inputs.Count == 0) return [];
		string normalizedBase = NormalizeBaseUrl(baseUrl);
		string normalizedModel = string.IsNullOrWhiteSpace(model) ? "BAAI/bge-m3" : model.Trim();
		// System.ClientModel 的 ApiKeyCredential 不接受空值; 本地/匿名服务走同样的
		// OpenAI /embeddings 协议, 但不伪造 Authorization 头。
		if (string.IsNullOrEmpty(apiKey))
		{
			return await GetEmbeddingsWithoutApiKeyAsync(normalizedBase, normalizedModel, inputs, dimensions, cancellationToken).ConfigureAwait(false);
		}
		string fingerprint = Fingerprint(normalizedBase, apiKey, normalizedModel, dimensions);
		IEmbeddingGenerator<string, Embedding<float>> generator = GetGenerator(
			normalizedBase, apiKey, normalizedModel, dimensions, fingerprint);

		AiEmbeddingGenerationOptions options = new()
		{
			ModelId = normalizedModel,
			Dimensions = dimensions,
		};
		GeneratedEmbeddings<Embedding<float>> generated = await generator.GenerateAsync(inputs, options, cancellationToken);
		return generated.Select(embedding => embedding.Vector.ToArray()).ToList();
	}

	/// <summary>使内存分布式缓存失效；旧条目保留到进程结束但不会再次命中。</summary>
	public void ClearCache()
	{
		lock (_gate)
		{
			_cacheEpoch++;
			_state = null;
		}
	}

	private IEmbeddingGenerator<string, Embedding<float>> GetGenerator(
		string baseUrl,
		string apiKey,
		string model,
		int? dimensions,
		string fingerprint)
	{
		lock (_gate)
		{
			if (_state is {Fingerprint: var current} && current == fingerprint) return _state.Generator;

			HttpClient sdkHttpClient = _sdkHttpClient ??= UrlAccessPolicy.CreateCappedHttpClient(_httpClient);
			OpenAIClientOptions options = new()
			{
				Endpoint = new Uri($"{baseUrl}/"),
				Transport = new HttpClientPipelineTransport(sdkHttpClient),
			};
			EmbeddingClient client = new(model, new ApiKeyCredential(apiKey ?? ""), options);
			IEmbeddingGenerator<string, Embedding<float>> inner = client.AsIEmbeddingGenerator(dimensions);
			MemoryDistributedCache cache = new(Options.Create(new MemoryDistributedCacheOptions {SizeLimit = CacheSizeLimit}));
			DistributedCachingEmbeddingGenerator<string, Embedding<float>> cached =
				new(inner, cache)
				{
					CacheKeyAdditionalValues = [fingerprint, _cacheEpoch],
				};
			_state = new GeneratorState(fingerprint, cached);
			return cached;
		}
	}

	private async Task<IReadOnlyList<float[]>> GetEmbeddingsWithoutApiKeyAsync(
		string baseUrl,
		string model,
		IReadOnlyList<string> inputs,
		int? dimensions,
		CancellationToken cancellationToken)
	{
		object payload = dimensions is { } value
			? new {model, input = inputs, dimensions = value}
			: new {model, input = inputs};
		using HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl}/embeddings")
		{
			Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
		};
		using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		string body = await UrlAccessPolicy.ReadCappedTextAsync(
			response.Content, UrlAccessPolicy.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException($"Embedding HTTP {(int)response.StatusCode}");
		}
		using JsonDocument document = JsonDocument.Parse(body);
		if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
		{
			throw new InvalidOperationException("Embedding 响应缺少 data");
		}
		List<(int Index, float[] Vector)> vectors = [];
		foreach (JsonElement item in data.EnumerateArray())
		{
			if (!item.TryGetProperty("embedding", out JsonElement values) || values.ValueKind != JsonValueKind.Array)
				throw new InvalidOperationException("Embedding 响应缺少向量");
			float[] vector = values.EnumerateArray().Select(value => value.GetSingle()).ToArray();
			int index = item.TryGetProperty("index", out JsonElement indexElement) && indexElement.TryGetInt32(out int parsed)
				? parsed
				: vectors.Count;
			vectors.Add((index, vector));
		}
		return vectors.OrderBy(item => item.Index).Select(item => item.Vector).ToList();
	}

	private static string NormalizeBaseUrl(string baseUrl)
	{
		string normalized = baseUrl.Trim().TrimEnd('/');
		if (normalized.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
		{
			normalized = normalized[..^"/embeddings".Length];
		}
		if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
		{
			throw new InvalidOperationException("Embedding API Base URL 必须是绝对 HTTP(S) 地址");
		}
		return normalized;
	}

	private static string Fingerprint(string baseUrl, string apiKey, string model, int? dimensions)
	{
		string input = $"{baseUrl}\n{apiKey}\n{model}\n{dimensions?.ToString() ?? ""}";
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
	}

	private sealed record GeneratorState(string Fingerprint, IEmbeddingGenerator<string, Embedding<float>> Generator);
}
