using System.Net;
using System.Text;
using Nori.Core.Chat;
using Nori.Core.Chat.Adapters;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

public sealed class AnthropicModelDiscoveryTests
{
	[Fact]
	public async Task 获取模型接口失败时明确报错而不是返回内置列表()
	{
		using HttpClient client = new(new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
		{
			Content = new StringContent("{\"error\":{\"message\":\"denied\"}}", Encoding.UTF8, "application/json"),
		}));
		AnthropicAdapter adapter = new(client);

		ChatException exception = await Assert.ThrowsAsync<ChatException>(() =>
			adapter.FetchModelsAsync("https://example.test", "secret"));

		Assert.Contains("HTTP 401", exception.Message, StringComparison.Ordinal);
		Assert.Contains("denied", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 非法BaseURL转成领域错误而不是UriFormatException()
	{
		using HttpClient client = new(new HttpTestHandler(_ => throw new InvalidOperationException("不应发起网络请求")));
		AnthropicAdapter adapter = new(client);

		ChatException exception = await Assert.ThrowsAsync<ChatException>(() =>
			adapter.FetchModelsAsync("not-a-valid-url", "secret"));

		Assert.Contains("Base URL 格式无效", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 空模型列表被视为发现失败()
	{
		using HttpClient client = new(new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
		}));
		AnthropicAdapter adapter = new(client);

		ChatException exception = await Assert.ThrowsAsync<ChatException>(() =>
			adapter.FetchModelsAsync("https://example.test", "secret"));

		Assert.Contains("空模型列表", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 成功时只返回服务端实际模型并排序去重()
	{
		using HttpClient client = new(new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(
				"{\"data\":[{\"id\":\"claude-z\"},{\"id\":\"claude-a\"},{\"id\":\"claude-z\"}]}",
				Encoding.UTF8,
				"application/json"),
		}));
		AnthropicAdapter adapter = new(client);

		IReadOnlyList<string> models = await adapter.FetchModelsAsync("https://example.test/messages", "secret");

		Assert.Equal(["claude-a", "claude-z"], models);
	}
}
