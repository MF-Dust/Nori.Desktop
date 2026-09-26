using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Nori.Core.Chat;
using Nori.Core.Chat.Adapters;
using Nori.Core.Network;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

public class LlmProviderTests
{
	[Theory]
	[InlineData("openai", LlmProvider.OpenAi)]
	[InlineData("OPENAI", LlmProvider.OpenAi)]
	[InlineData("openai_responses", LlmProvider.OpenAiResponses)]
	[InlineData("responses", LlmProvider.OpenAiResponses)]
	[InlineData("anthropic", LlmProvider.Anthropic)]
	[InlineData("claude", LlmProvider.Anthropic)]
	[InlineData("google", LlmProvider.Google)]
	[InlineData("gemini", LlmProvider.Google)]
	[InlineData("google_genai", LlmProvider.Google)]
	[InlineData("", LlmProvider.OpenAi)]
	[InlineData(null, LlmProvider.OpenAi)]
	public void 解析协议类型(string? input, LlmProvider expected)
	{
		Assert.Equal(expected, LlmProviderExtensions.ParseProvider(input));
	}

	[Theory]
	[InlineData(LlmProvider.OpenAi, "https://api.openai.com/v1")]
	[InlineData(LlmProvider.OpenAiResponses, "https://api.openai.com/v1")]
	[InlineData(LlmProvider.Anthropic, "https://api.anthropic.com/v1")]
	[InlineData(LlmProvider.Google, "https://generativelanguage.googleapis.com/v1beta")]
	public void 默认BaseUrl正确(LlmProvider provider, string expected)
	{
		Assert.Equal(expected, provider.DefaultBaseUrl());
	}
}

/// <summary>
/// 模型目录适配器测试。聊天/流式协议由官方 SDK 承担, 适配器只负责拉取模型列表。
/// </summary>
public class LlmAdapterTests
{
	[Fact]
	public async Task GoogleGenAiAdapter拉取模型列表()
	{
		using HttpTestHandler handler = new(req =>
		{
			Assert.Equal(HttpMethod.Get, req.Method);
			Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models", req.RequestUri?.ToString());

			string responseJson = """
			{
				"models": [
					{
						"name": "models/gemini-2.5-flash",
						"supportedGenerationMethods": ["generateContent", "countTokens"]
					},
					{
						"name": "models/embedding-001",
						"supportedGenerationMethods": ["embedContent"]
					},
					{
						"name": "models/gemini-2.5-pro",
						"supportedGenerationMethods": ["generateContent"]
					}
				]
			}
			""";

			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
			};
		});

		using HttpClient client = new(handler);
		GoogleGenAiAdapter adapter = new(client);

		IReadOnlyList<string> models = await adapter.FetchModelsAsync("https://generativelanguage.googleapis.com/v1beta", "key");

		Assert.Equal(2, models.Count);
		Assert.Contains("gemini-2.5-flash", models);
		Assert.Contains("gemini-2.5-pro", models);
		Assert.DoesNotContain("embedding-001", models);
	}

	[Fact]
	public async Task 模型目录响应有大小上限且错误正文不外泄()
	{
		using HttpTestHandler oversized = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(new string('x', checked((int)UrlAccessPolicy.MaxResponseBytes + 1)))
		});
		using HttpClient oversizedClient = new(oversized);
		OpenAiChatAdapter oversizedAdapter = new(oversizedClient);
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			oversizedAdapter.FetchModelsAsync("https://example.test/v1", "key"));

		using HttpTestHandler error = new(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
		{
			Content = new StringContent("secret=do-not-show")
		});
		using HttpClient errorClient = new(error);
		OpenAiChatAdapter errorAdapter = new(errorClient);
		ChatException failure = await Assert.ThrowsAsync<ChatException>(() =>
			errorAdapter.FetchModelsAsync("https://example.test/v1", "key"));
		Assert.DoesNotContain("do-not-show", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ChatMessageInput保留纯文本构造与对象初始化兼容()
	{
		ChatMessageInput constructed = new("user", "hello");
		ChatMessageInput initialized = new() {Role = "assistant", Content = "hi"};

		Assert.Equal("hello", constructed.Content);
		Assert.Equal("assistant", initialized.Role);
		Assert.Empty(constructed.ImageParts);
		IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> mapped = ChatClientLlmAdapter.BuildMessages("system", [constructed]);
		Assert.Equal("hello", mapped[1].Text);
	}

	[Fact]
	public void 图片限制拒绝空单张超大与总大小超限()
	{
		Assert.Throws<ChatException>(() => new ChatImagePart(Array.Empty<byte>(), "image/png"));
		Assert.Throws<ChatException>(() => new ChatImagePart(new byte[ChatImagePart.MaxBytes + 1], "image/png"));

		ChatImagePart first = new(new byte[ChatImagePart.MaxBytes], "image/png");
		ChatImagePart second = new(new byte[ChatImagePart.MaxBytes], "image/jpeg");
		Assert.Throws<ChatException>(() => new ChatMessageInput("user", "图片")
		{
			ImageParts = [first, second, new ChatImagePart([1], "image/webp")],
		});
	}

	[Theory]
	[InlineData("")]
	[InlineData("image/gif")]
	[InlineData("application/octet-stream")]
	public void 图片MIME白名单拒绝其他类型(string mimeType) =>
		Assert.Throws<ChatException>(() => new ChatImagePart([1], mimeType));

	[Fact]
	public void ChatClient映射图片为文本与数据内容()
	{
		byte[] source = [1, 2, 3];
		ChatMessageInput input = new("user", "请看看")
		{
			ImageParts = [new ChatImagePart(source, "IMAGE/PNG")],
		};
		source[0] = 9;
		IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages = ChatClientLlmAdapter.BuildMessages("系统提示", [input]);

		Assert.Equal("系统提示", messages[0].Text);
		Assert.Equal(ChatRole.User, messages[1].Role);
		Assert.Equal(2, messages[1].Contents.Count);
		Assert.Equal("请看看", Assert.IsType<TextContent>(messages[1].Contents[0]).Text);
		DataContent data = Assert.IsType<DataContent>(messages[1].Contents[1]);
		Assert.Equal("image/png", data.MediaType);
		Assert.Equal([1, 2, 3], data.Data.ToArray());
	}
}
