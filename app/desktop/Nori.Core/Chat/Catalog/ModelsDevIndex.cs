using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nori.Core.Chat.Catalog;

/// <summary>
/// models.dev 目录的精简索引。
///
/// 原始 api.json 有数 MB，且含价格、发布日期等用不到的字段；这里只保留能力字段，
/// 本地缓存也写这份精简结果，启动读取不必再解析整份原文。
/// </summary>
public sealed class ModelsDevIndex
{
	/// <summary>思考强度的升序排列，用于就近取值。</summary>
	internal static readonly string[] EffortOrder = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

	/// <summary>同名模型出现在多个服务商时的优先顺序：原厂优先，其次按字母序。</summary>
	private static readonly string[] PreferredProviders =
	[
		"openai", "anthropic", "google", "deepseek", "xai", "mistral", "moonshotai", "zhipuai",
		"alibaba", "minimax", "groq", "openrouter",
	];

	private readonly Dictionary<string, ProviderEntry> _providers;

	private ModelsDevIndex(Dictionary<string, ProviderEntry> providers, DateTimeOffset fetchedAt)
	{
		_providers = providers;
		FetchedAt = fetchedAt;
	}

	/// <summary>目录下载时间。</summary>
	public DateTimeOffset FetchedAt { get; }

	/// <summary>收录的模型总数。</summary>
	public int ModelCount => _providers.Values.Sum(provider => provider.Models.Count);

	/// <summary>解析 models.dev 的 api.json 原文。格式不符时抛 JsonException / InvalidDataException。</summary>
	public static ModelsDevIndex ParseSource(string json, DateTimeOffset fetchedAt)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("models.dev 目录格式无效");
		Dictionary<string, ProviderEntry> providers = new(StringComparer.OrdinalIgnoreCase);
		foreach (JsonProperty providerProperty in document.RootElement.EnumerateObject())
		{
			JsonElement provider = providerProperty.Value;
			if (provider.ValueKind != JsonValueKind.Object) continue;
			string providerId = String(provider, "id") ?? providerProperty.Name;
			List<ModelCapabilities> models = [];
			if (provider.TryGetProperty("models", out JsonElement modelsElement) && modelsElement.ValueKind == JsonValueKind.Object)
			{
				foreach (JsonProperty modelProperty in modelsElement.EnumerateObject())
				{
					if (modelProperty.Value.ValueKind != JsonValueKind.Object) continue;
					models.Add(ParseModel(providerId, modelProperty.Name, modelProperty.Value));
				}
			}
			providers[providerId] = new ProviderEntry(providerId, String(provider, "api"), models);
		}
		if (providers.Count == 0) throw new InvalidDataException("models.dev 目录为空");
		return new ModelsDevIndex(providers, fetchedAt);
	}

	/// <summary>读取本地精简缓存。</summary>
	public static ModelsDevIndex ParseCache(string json)
	{
		CacheFile? cache = JsonSerializer.Deserialize<CacheFile>(json, CacheJson);
		if (cache?.Providers is not {Count: > 0} providers) throw new InvalidDataException("模型目录缓存为空");
		Dictionary<string, ProviderEntry> map = new(StringComparer.OrdinalIgnoreCase);
		foreach (ProviderEntry provider in providers)
		{
			if (string.IsNullOrWhiteSpace(provider.Id)) continue;
			map[provider.Id] = provider with {Models = provider.Models ?? []};
		}
		return new ModelsDevIndex(map, cache.FetchedAt);
	}

	/// <summary>序列化为本地精简缓存。</summary>
	public string ToCacheJson() => JsonSerializer.Serialize(
		new CacheFile(FetchedAt, _providers.Values.OrderBy(provider => provider.Id, StringComparer.Ordinal).ToList()),
		CacheJson);

	/// <summary>
	/// 按协议、Base URL 与模型名查找能力。
	///
	/// 依次尝试：Base URL 主机与服务商 api 地址一致的服务商 → 协议原厂 → 全目录同名模型。
	/// 每一层先精确匹配，再匹配带日期等后缀的变体 (如 claude-sonnet-4-5-20250929)。
	/// </summary>
	public ModelCapabilities? Find(LlmProvider protocol, string? baseUrl, string model)
	{
		if (string.IsNullOrWhiteSpace(model)) return null;
		string normalized = model.Trim();
		if (normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) normalized = normalized["models/".Length..];
		List<string> candidates = [normalized];
		int slash = normalized.LastIndexOf('/');
		if (slash >= 0 && slash < normalized.Length - 1) candidates.Add(normalized[(slash + 1)..]);

		string? host = ChatEndpoint.TryCreateHttpUri(baseUrl, out Uri? uri) ? uri!.Host : null;
		List<ProviderEntry> scoped = [];
		if (host is not null)
		{
			scoped.AddRange(_providers.Values
				.Where(provider => provider.Api is { } api && ChatEndpoint.TryCreateHttpUri(api, out Uri? apiUri)
					&& string.Equals(apiUri!.Host, host, StringComparison.OrdinalIgnoreCase))
				.OrderBy(provider => provider.Id, StringComparer.Ordinal));
		}
		string native = protocol switch
		{
			LlmProvider.Anthropic => "anthropic",
			LlmProvider.Google => "google",
			_ => "openai",
		};
		if (_providers.TryGetValue(native, out ProviderEntry? nativeProvider) && !scoped.Contains(nativeProvider))
		{
			// 自建代理的主机认不出服务商，但协议本身说明了模型大概率来自哪家。
			scoped.Add(nativeProvider);
		}

		foreach (ProviderEntry provider in scoped)
		{
			if (MatchIn(provider, candidates) is { } found) return found;
		}

		IEnumerable<ProviderEntry> ordered = _providers.Values
			.OrderBy(provider => Array.IndexOf(PreferredProviders, provider.Id.ToLowerInvariant()) is var index and >= 0 ? index : int.MaxValue)
			.ThenBy(provider => provider.Id, StringComparer.Ordinal);
		foreach (ProviderEntry provider in ordered)
		{
			if (Exact(provider, candidates) is { } found) return found;
		}
		foreach (ProviderEntry provider in ordered)
		{
			if (Prefixed(provider, candidates) is { } found) return found;
		}
		return null;
	}

	private static ModelCapabilities? MatchIn(ProviderEntry provider, IReadOnlyList<string> candidates) =>
		Exact(provider, candidates) ?? Prefixed(provider, candidates);

	private static ModelCapabilities? Exact(ProviderEntry provider, IReadOnlyList<string> candidates)
	{
		foreach (string candidate in candidates)
		{
			ModelCapabilities? match = provider.Models.FirstOrDefault(model => string.Equals(model.ModelId, candidate, StringComparison.OrdinalIgnoreCase));
			if (match is not null) return match;
		}
		return null;
	}

	/// <summary>带日期或版本后缀的模型名退回到最长的同前缀条目。</summary>
	private static ModelCapabilities? Prefixed(ProviderEntry provider, IReadOnlyList<string> candidates)
	{
		foreach (string candidate in candidates)
		{
			ModelCapabilities? match = provider.Models
				.Where(model => model.ModelId.Length >= 3
					&& candidate.StartsWith(model.ModelId + "-", StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(model => model.ModelId.Length)
				.FirstOrDefault();
			if (match is not null) return match;
		}
		return null;
	}

	private static ModelCapabilities ParseModel(string providerId, string key, JsonElement model)
	{
		List<string> efforts = [];
		bool toggle = false;
		bool budget = false;
		int? budgetMin = null;
		int? budgetMax = null;
		if (model.TryGetProperty("reasoning_options", out JsonElement options) && options.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement option in options.EnumerateArray())
			{
				if (option.ValueKind != JsonValueKind.Object) continue;
				switch (String(option, "type"))
				{
					case "toggle":
						toggle = true;
						break;
					case "budget_tokens":
						budget = true;
						budgetMin = Int(option, "min") ?? budgetMin;
						budgetMax = Int(option, "max") ?? budgetMax;
						break;
					case "effort" when option.TryGetProperty("values", out JsonElement values) && values.ValueKind == JsonValueKind.Array:
						foreach (JsonElement value in values.EnumerateArray())
						{
							if (value.ValueKind == JsonValueKind.String && value.GetString() is { } effort
								&& Array.IndexOf(EffortOrder, effort.ToLowerInvariant()) >= 0
								&& !efforts.Contains(effort.ToLowerInvariant()))
							{
								efforts.Add(effort.ToLowerInvariant());
							}
						}
						break;
				}
			}
		}
		efforts.Sort((left, right) => Array.IndexOf(EffortOrder, left).CompareTo(Array.IndexOf(EffortOrder, right)));

		JsonElement limit = model.TryGetProperty("limit", out JsonElement limitElement) && limitElement.ValueKind == JsonValueKind.Object ? limitElement : default;
		string? interleaved = null;
		if (model.TryGetProperty("interleaved", out JsonElement interleavedElement))
		{
			interleaved = interleavedElement.ValueKind switch
			{
				JsonValueKind.Object => String(interleavedElement, "field"),
				JsonValueKind.True => "reasoning_content",
				_ => null,
			};
		}

		return new ModelCapabilities
		{
			ProviderId = providerId,
			ModelId = String(model, "id") ?? key,
			Name = String(model, "name") ?? key,
			ContextWindow = limit.ValueKind == JsonValueKind.Object ? Int(limit, "context") ?? 0 : 0,
			InputLimit = limit.ValueKind == JsonValueKind.Object ? Int(limit, "input") ?? 0 : 0,
			OutputLimit = limit.ValueKind == JsonValueKind.Object ? Int(limit, "output") ?? 0 : 0,
			Reasoning = Bool(model, "reasoning"),
			ReasoningEfforts = efforts,
			ReasoningToggle = toggle,
			ReasoningBudget = budget,
			ReasoningBudgetMin = budgetMin,
			ReasoningBudgetMax = budgetMax,
			ToolCall = Bool(model, "tool_call"),
			ImageInput = Modalities(model, "input").Contains("image"),
			ImageOutput = Modalities(model, "output").Contains("image"),
			InterleavedField = interleaved,
		};
	}

	private static HashSet<string> Modalities(JsonElement model, string direction)
	{
		HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
		if (model.TryGetProperty("modalities", out JsonElement modalities) && modalities.ValueKind == JsonValueKind.Object
			&& modalities.TryGetProperty(direction, out JsonElement values) && values.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement value in values.EnumerateArray())
			{
				if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text) result.Add(text);
			}
		}
		return result;
	}

	private static string? String(JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static bool Bool(JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

	private static int? Int(JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number) return null;
		if (value.TryGetInt64(out long integer)) return (int)Math.Clamp(integer, 0, int.MaxValue);
		return value.TryGetDouble(out double number) ? (int)Math.Clamp(number, 0, int.MaxValue) : null;
	}

	private static readonly JsonSerializerOptions CacheJson = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>精简缓存中的服务商条目。</summary>
	internal sealed record ProviderEntry(string Id, string? Api, IReadOnlyList<ModelCapabilities> Models);

	private sealed record CacheFile(DateTimeOffset FetchedAt, IReadOnlyList<ProviderEntry> Providers);
}
