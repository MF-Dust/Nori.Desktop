using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using AiEmbeddingGenerationOptions = Microsoft.Extensions.AI.EmbeddingGenerationOptions;
using Nori.Core.Network;

namespace Nori.Core.Embedding;

/// <summary>
/// OpenAI 规范 Embedding 适配器。
/// 直接实现 /embeddings 协议的 IEmbeddingGenerator，并在进程内用 8 MiB 分布式缓存包装。
/// </summary>
public sealed class OpenAiEmbeddingAdapter(HttpClient httpClient) : IEmbeddingAdapter
{
	private const long CacheSizeLimit = 8 * 1024 * 1024;
	private readonly HttpClient _httpClient = httpClient;
	private readonly Lock _gate = new();
	private GeneratorState? _state;
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

			IEmbeddingGenerator<string, Embedding<float>> inner = new HttpEmbeddingGenerator(_httpClient, baseUrl, apiKey, model, dimensions);
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

	/// <summary>
	/// 发送一次 OpenAI 规范的 /embeddings 请求。
	/// 空密钥用于本地或匿名服务：同样的协议，但不伪造 Authorization 头。
	/// </summary>
	internal static async Task<IReadOnlyList<float[]>> RequestEmbeddingsAsync(
		HttpClient httpClient,
		string baseUrl,
		string apiKey,
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
		if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
		using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		string body = await UrlAccessPolicy.ReadCappedTextAsync(
			response.Content, UrlAccessPolicy.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException($"Embedding HTTP {(int)response.StatusCode}", null, response.StatusCode);
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
			float[] vector = values.EnumerateArray().Select(element => element.GetSingle()).ToArray();
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

	/// <summary>供分布式缓存包装的最小 IEmbeddingGenerator。</summary>
	private sealed class HttpEmbeddingGenerator(HttpClient httpClient, string baseUrl, string apiKey, string model, int? dimensions)
		: IEmbeddingGenerator<string, Embedding<float>>
	{
		public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
			IEnumerable<string> values,
			AiEmbeddingGenerationOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			IReadOnlyList<float[]> vectors = await RequestEmbeddingsAsync(
				httpClient,
				baseUrl,
				apiKey,
				options?.ModelId ?? model,
				values.ToList(),
				options?.Dimensions ?? dimensions,
				cancellationToken).ConfigureAwait(false);
			return new GeneratedEmbeddings<Embedding<float>>(vectors.Select(vector => new Embedding<float>(vector)));
		}

		public object? GetService(Type serviceType, object? serviceKey = null) =>
			serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

		public void Dispose()
		{
			// 共享 HttpClient 属于调用方。
		}
	}
}
