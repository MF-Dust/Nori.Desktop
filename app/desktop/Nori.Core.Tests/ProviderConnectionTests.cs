using System.Net;
using System.Text;
using Nori.Core.Chat;
using Nori.Core.Embedding;

namespace Nori.Core.Tests;

public sealed class ProviderConnectionTests
{
	private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(handler(request));
	}

	[Fact]
	public async Task LLM探测使用固定请求并返回结构化成功结果()
	{
		using MockHandler handler = new(request =>
		{
			Assert.Equal(HttpMethod.Post, request.Method);
			Assert.Equal("https://example.test/v1/chat/completions", request.RequestUri?.ToString());
			Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
			Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
			return JsonResponse("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}");
		});
		using HttpClient http = new(handler);
		OpenAiEmbeddingAdapter embedding = new(http);
		ProviderConnectionTester tester = new(http, embedding);

		ProviderConnectionTestResult result = await tester.TestLlmAsync(
			"openai", "https://example.test/v1", "test-key", "test-model");

		Assert.True(result.Success);
		Assert.Equal("ok", result.Category);
		Assert.True(result.LatencyMs >= 0);
		Assert.DoesNotContain("test-key", result.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task LLM探测失败时不回传密钥或凭据()
	{
		using MockHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
		{
			Content = new StringContent("{\"error\":{\"message\":\"api_key=secret-key\"}}", Encoding.UTF8, "application/json"),
		});
		using HttpClient http = new(handler);
		OpenAiEmbeddingAdapter embedding = new(http);
		ProviderConnectionTester tester = new(http, embedding);

		ProviderConnectionTestResult result = await tester.TestLlmAsync(
			"openai", "https://example.test/v1", "secret-key", "test-model");

		Assert.False(result.Success);
		Assert.Equal("authentication", result.Category);
		Assert.DoesNotContain("secret-key", result.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(HttpStatusCode.Forbidden, "authentication", "forbidden-secret")]
	[InlineData(HttpStatusCode.TooManyRequests, "rate_limited", "rate-secret")]
	public async Task LLM探测按HTTP状态分类且不泄露密钥(HttpStatusCode status, string category, string secret)
	{
		using MockHandler handler = new(_ => new HttpResponseMessage(status)
		{
			Content = new StringContent($"{{\"error\":{{\"message\":\"{secret}\"}}}}", Encoding.UTF8, "application/json"),
		});
		using HttpClient http = new(handler);
		OpenAiEmbeddingAdapter embedding = new(http);
		ProviderConnectionTester tester = new(http, embedding);

		ProviderConnectionTestResult result = await tester.TestLlmAsync(
			"openai", "https://example.test/v1", secret, "test-model");

		Assert.False(result.Success);
		Assert.Equal(category, result.Category);
		Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task LLM探测错误不会回传URL响应正文或本机路径()
	{
		using MockHandler handler = new(_ => throw new HttpRequestException(
			"请求 https://user:password@example.test/v1 失败: response-body=private text C:\\Users\\Nori\\secret.log")); // NOSONAR
		using HttpClient http = new(handler);
		OpenAiEmbeddingAdapter embedding = new(http);
		ProviderConnectionTester tester = new(http, embedding);

		ProviderConnectionTestResult result = await tester.TestLlmAsync(
			"openai", "https://example.test/v1", "test-key", "test-model");

		Assert.False(result.Success);
		Assert.DoesNotContain("password", result.Message, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("response-body", result.Message, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("C:\\Users\\Nori", result.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task LLM探测超时归类为超时()
	{
		using MockHandler handler = new(_ => throw new TaskCanceledException("request timeout"));
		using HttpClient http = new(handler);
		OpenAiEmbeddingAdapter embedding = new(http);
		ProviderConnectionTester tester = new(http, embedding);
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		ProviderConnectionTestResult result = await tester.TestLlmAsync(
			"openai", "https://example.test/v1", "timeout-secret", "test-model", cancellation.Token);

		Assert.False(result.Success);
		Assert.Equal("timeout", result.Category);
		Assert.DoesNotContain("timeout-secret", result.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task LLM探测成功响应格式错误时归类为协议错误()
	{
		using MockHandler handler = new(_ => JsonResponse("{\"choices\":[]}"));
		using HttpClient http = new(handler);
		OpenAiEmbeddingAdapter embedding = new(http);
		ProviderConnectionTester tester = new(http, embedding);

		ProviderConnectionTestResult result = await tester.TestLlmAsync(
			"openai", "https://example.test/v1", "protocol-secret", "test-model");

		Assert.False(result.Success);
		Assert.Equal("protocol", result.Category);
		Assert.Contains("格式", result.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("protocol-secret", result.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Embedding探测允许无密钥的OpenAI兼容服务()
	{
		using MockHandler handler = new(_ =>
			JsonResponse("{\"data\":[{\"embedding\":[0.1,0.2],\"index\":0}]}"));
		using HttpClient http = new(handler);
		OpenAiEmbeddingAdapter embedding = new(http);
		ProviderConnectionTester tester = new(http, embedding);

		ProviderConnectionTestResult result = await tester.TestEmbeddingAsync(
			"https://example.test/v1", "", "test-embedding");

		Assert.True(result.Success, $"{result.Category}: {result.Message}");
		Assert.Equal("ok", result.Category);
	}

	[Fact]
	public async Task Embedding探测要求返回非空向量且不写入配置()
	{
		using MockHandler handler = new(request =>
		{
			Assert.Equal(HttpMethod.Post, request.Method);
			Assert.EndsWith("/embeddings", request.RequestUri?.AbsolutePath, StringComparison.Ordinal);
			return JsonResponse("{\"data\":[{\"embedding\":[0.1,0.2,0.3],\"index\":0}],\"model\":\"test-embedding\"}");
		});
		using HttpClient http = new(handler);
		OpenAiEmbeddingAdapter embedding = new(http);
		ProviderConnectionTester tester = new(http, embedding);

		ProviderConnectionTestResult result = await tester.TestEmbeddingAsync(
			"https://example.test/v1", "test-key", "test-embedding");

		Assert.True(result.Success);
		Assert.Equal("embedding", result.Provider);
		Assert.Equal("ok", result.Category);
	}

	private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(json, Encoding.UTF8, "application/json"),
	};
}
