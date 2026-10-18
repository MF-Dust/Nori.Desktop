using System.Diagnostics;
using Nori.Core.Embedding;
using Nori.Core.Security;

namespace Nori.Core.Chat;

/// <summary>Provider 连接探测结果，不包含密钥、请求正文或响应正文。</summary>
public sealed record ProviderConnectionTestResult
{
	public bool Success { get; init; }
	public required string Provider { get; init; }
	public long LatencyMs { get; init; }
	public required string Category { get; init; }
	public required string Message { get; init; }
}

/// <summary>
/// 非持久化 Provider 连接探测器。
///
/// 探测只发送固定短内容，不写配置、聊天或记忆；异常消息经过统一脱敏后才返回。
/// </summary>
public sealed class ProviderConnectionTester(HttpClient httpClient, OpenAiEmbeddingAdapter embedding, CustomChatProviderRegistry? customProviders = null)
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
	private const string ProbeSystemPrompt = "只回复 OK。不要调用工具，不要输出任何其他内容。";
	private const string ProbeText = "Nori connection test";

	private readonly LlmClient _llm = new(httpClient, customProviders);
	private readonly OpenAiEmbeddingAdapter _embedding = embedding;

	public async Task<ProviderConnectionTestResult> TestLlmAsync(
		string? provider,
		string baseUrl,
		string apiKey,
		string model,
		CancellationToken cancellationToken = default)
	{
		bool custom = LlmProviderExtensions.IsCustomProvider(provider);
		string providerName = custom ? provider!.Trim() : LlmProviderExtensions.ParseProvider(provider).ToString().ToLowerInvariant();
		if (custom)
		{
			if (!_llm.CustomProviders.TryGet(providerName, out CustomChatProviderDescriptor? descriptor))
				return Invalid(providerName, "插件聊天提供商不可用，请启用或重新安装对应插件");
			if (descriptor.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey)) return Invalid(providerName, "API Key 不能为空");
		}
		else
		{
			if (string.IsNullOrWhiteSpace(baseUrl)) return Invalid(providerName, "API Base URL 不能为空");
			if (string.IsNullOrWhiteSpace(apiKey)) return Invalid(providerName, "API Key 不能为空");
		}
		if (string.IsNullOrWhiteSpace(model)) return Invalid(providerName, "模型不能为空");

		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(Timeout);
			ILlmAdapter adapter = _llm.CreateAdapter(provider);
			await adapter.CompleteAsync(
				baseUrl.Trim(),
				apiKey,
				model.Trim(),
				ProbeSystemPrompt,
				[new ChatMessageInput {Role = "user", Content = ProbeText}],
				timeout.Token).ConfigureAwait(false);
			return new ProviderConnectionTestResult
			{
				Success = true,
				Provider = providerName,
				LatencyMs = stopwatch.ElapsedMilliseconds,
				Category = "ok",
				Message = "连接成功",
			};
		}
		catch (Exception exception)
		{
			return Failure(providerName, stopwatch.ElapsedMilliseconds, exception, apiKey, cancellationToken);
		}
	}

	public async Task<ProviderConnectionTestResult> TestEmbeddingAsync(
		string baseUrl,
		string apiKey,
		string model,
		int? dimensions = null,
		CancellationToken cancellationToken = default)
	{
		const string providerName = "embedding";
		if (string.IsNullOrWhiteSpace(baseUrl)) return Invalid(providerName, "Embedding API Base URL 不能为空");
		// OpenAI 兼容的本地/内网服务可以不要求 API Key。
		if (string.IsNullOrWhiteSpace(model)) return Invalid(providerName, "Embedding 模型不能为空");

		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(Timeout);
			IReadOnlyList<float[]> vectors = await _embedding.GetEmbeddingsAsync(
				baseUrl.Trim(), apiKey, model.Trim(), [ProbeText], dimensions, timeout.Token).ConfigureAwait(false);
			if (vectors.Count == 0 || vectors[0].Length == 0)
				throw new InvalidOperationException("Embedding 服务未返回有效向量");
			return new ProviderConnectionTestResult
			{
				Success = true,
				Provider = providerName,
				LatencyMs = stopwatch.ElapsedMilliseconds,
				Category = "ok",
				Message = "连接成功",
			};
		}
		catch (Exception exception)
		{
			return Failure(providerName, stopwatch.ElapsedMilliseconds, exception, apiKey, cancellationToken);
		}
	}

	private static ProviderConnectionTestResult Invalid(string provider, string message) => new()
	{
		Provider = provider,
		Category = "invalid_config",
		Message = message,
	};

	private static ProviderConnectionTestResult Failure(
		string provider,
		long latency,
		Exception exception,
		string secret,
		CancellationToken callerToken)
	{
		bool cancelled = callerToken.IsCancellationRequested;
		string classificationText = SensitiveDataRedactor.Redact(exception.Message);
		if (!string.IsNullOrEmpty(secret)) classificationText = classificationText.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
		string category = cancelled || exception is TimeoutException or TaskCanceledException
			? "timeout"
			: Classify(classificationText);
		return new ProviderConnectionTestResult
		{
			Provider = provider,
			LatencyMs = latency,
			Category = category,
			Message = SafeMessage(category),
		};
	}

	private static string Classify(string message)
	{
		if (message.Contains("401", StringComparison.Ordinal) || message.Contains("403", StringComparison.Ordinal)) return "authentication";
		if (message.Contains("429", StringComparison.Ordinal)) return "rate_limited";
		if (message.Contains("解析", StringComparison.Ordinal)
			|| message.Contains("格式", StringComparison.Ordinal)
			|| message.Contains("parse", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("deserialize", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("index was out of range", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("collection", StringComparison.OrdinalIgnoreCase)) return "protocol";
		if (message.Contains("URL", StringComparison.Ordinal) || message.Contains("地址", StringComparison.Ordinal)) return "invalid_config";
		if (message.Contains("请求", StringComparison.Ordinal) || message.Contains("网络", StringComparison.Ordinal)) return "network";
		return "error";
	}

	private static string SafeMessage(string category) => category switch
	{
		"authentication" => "Provider 身份验证失败",
		"rate_limited" => "Provider 请求过于频繁，请稍后重试",
		"protocol" => "Provider 响应格式无效",
		"invalid_config" => "Provider 地址或配置无效",
		"network" => "Provider 网络连接失败",
		"timeout" => "Provider 连接超时",
		_ => "Provider 连接失败",
	};
}
