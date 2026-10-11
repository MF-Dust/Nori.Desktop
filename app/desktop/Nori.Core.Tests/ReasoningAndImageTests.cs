using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nori.Core.Chat;
using Nori.Core.Chat.Catalog;
using Nori.Core.Chat.Images;
using Nori.Core.Configuration;
using Nori.Core.Tools;

namespace Nori.Core.Tests;

/// <summary>思考参数的请求接线、思考块回传与图像生成服务。服务端全部是假响应。</summary>
public sealed class ReasoningAndImageTests
{
	private static readonly ChatMessageInput[] Hello = [new("user", "你好")];

	private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
	{
		private int _index;

		public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
			Requests.Add((request, body));
			HttpResponseMessage response = responses[Math.Min(_index++, responses.Length - 1)](request);
			response.RequestMessage = request;
			return response;
		}
	}

	private static HttpResponseMessage Sse(params string[] events) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(string.Concat(events.Select(data => $"data: {data}\n\n")), Encoding.UTF8, "text/event-stream"),
	};

	private static HttpResponseMessage NamedSse(params (string Event, string Data)[] events) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(string.Concat(events.Select(item => $"event: {item.Event}\ndata: {item.Data}\n\n")), Encoding.UTF8, "text/event-stream"),
	};

	private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(json, Encoding.UTF8, "application/json"),
	};

	private static RegisteredTool WeatherTool() => new()
	{
		Name = "get_weather",
		Description = "查询天气",
		Parameters = JsonNode.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""")!.AsObject(),
		PermissionLevel = "safe",
		Execute = (_, _) => Task.FromResult<object?>(null),
	};

	private static ModelCapabilities Capabilities(string provider, string model) => new()
	{
		ProviderId = provider,
		ModelId = model,
		Reasoning = true,
	};

	private static HttpResponseMessage OpenAiText(string text) => Sse(
		$$$"""{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","content":"{{{text}}}"}}]}""",
		"""{"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
		"[DONE]");

	[Fact]
	public async Task 自动档位不发送任何思考参数()
	{
		ScriptedHandler handler = new(_ => OpenAiText("好"));
		using HttpClient http = new(handler);
		ChatClientLlmAdapter adapter = new(LlmProvider.OpenAi, http, new LlmGenerationOptions());

		await adapter.StreamAsync("https://example.test/v1", "sk", "gpt-5", "系统", Hello, _ => { });

		string body = Assert.Single(handler.Requests).Body;
		Assert.DoesNotContain("reasoning", body, StringComparison.Ordinal);
		Assert.DoesNotContain("thinking", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task OpenAI发送就近档位且思考文本不进入正文()
	{
		ScriptedHandler handler = new(_ => Sse(
			"""{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"先想一想"}}]}""",
			"""{"id":"c1","choices":[{"index":0,"delta":{"content":"好"}}]}""",
			"""{"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
			"[DONE]"));
		using HttpClient http = new(handler);
		ModelCapabilities capabilities = Capabilities("openai", "gpt-5") with {ReasoningEfforts = ["minimal", "low", "medium", "high"]};
		ChatClientLlmAdapter adapter = new(LlmProvider.OpenAi, http, new LlmGenerationOptions {Reasoning = LlmReasoningLevel.Max, Capabilities = capabilities});
		List<string> chunks = [];

		string text = await adapter.StreamAsync("https://example.test/v1", "sk", "gpt-5", "系统", Hello, chunks.Add);

		Assert.Equal("好", text);
		Assert.Equal(["好"], chunks);
		Assert.Equal("high", JsonNode.Parse(Assert.Single(handler.Requests).Body)!["reasoning_effort"]!.GetValue<string>());
	}

	[Fact]
	public async Task 只支持开关的模型用thinking字段关闭思考()
	{
		ScriptedHandler handler = new(_ => OpenAiText("好"));
		using HttpClient http = new(handler);
		ModelCapabilities capabilities = Capabilities("deepseek", "deepseek-chat") with {ReasoningToggle = true};
		ChatClientLlmAdapter adapter = new(LlmProvider.OpenAi, http, new LlmGenerationOptions {Reasoning = LlmReasoningLevel.Off, Capabilities = capabilities});

		await adapter.StreamAsync("https://api.deepseek.com", "sk", "deepseek-chat", "系统", Hello, _ => { });

		JsonNode body = JsonNode.Parse(Assert.Single(handler.Requests).Body)!;
		Assert.Equal("disabled", body["thinking"]!["type"]!.GetValue<string>());
		Assert.Null(body["reasoning_effort"]);
	}

	[Fact]
	public async Task Interleaved模型在工具往返时回传reasoning_content()
	{
		ScriptedHandler handler = new(
			_ => Sse(
				"""{"id":"t1","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"要查天气"}}]}""",
				"""{"id":"t1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"上海\"}"}}]}}]}""",
				"""{"id":"t1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
				"[DONE]"),
			_ => OpenAiText("上海晴"));
		using HttpClient http = new(handler);
		ModelCapabilities capabilities = Capabilities("deepseek", "deepseek-chat") with {InterleavedField = "reasoning_content"};
		ChatClientLlmAdapter adapter = new(LlmProvider.OpenAi, http, new LlmGenerationOptions {Capabilities = capabilities});

		string text = await adapter.StreamWithToolsAsync(
			"https://api.deepseek.com", "sk", "deepseek-chat", "系统", Hello, [WeatherTool()],
			(_, _) => Task.FromResult(new ToolResult(JsonValue.Create("晴"), null)), _ => { });

		Assert.Equal("上海晴", text);
		JsonArray second = JsonNode.Parse(handler.Requests[1].Body)!["messages"]!.AsArray();
		JsonNode assistant = second.Single(message => message?["role"]?.GetValue<string>() == "assistant")!;
		Assert.Equal("要查天气", assistant["reasoning_content"]!.GetValue<string>());
	}

	[Fact]
	public async Task Anthropic开启思考后工具往返带回签名思考块()
	{
		ScriptedHandler handler = new(
			_ => NamedSse(
				("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude","usage":{"input_tokens":20,"output_tokens":1}}}"""),
				("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}"""),
				("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"需要查北京天气"}}"""),
				("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG-1"}}"""),
				("content_block_stop", """{"type":"content_block_stop","index":0}"""),
				("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{}}}"""),
				("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"city\":\"北京\"}"}}"""),
				("content_block_stop", """{"type":"content_block_stop","index":1}"""),
				("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":9}}"""),
				("message_stop", """{"type":"message_stop"}""")),
			_ => NamedSse(
				("message_start", """{"type":"message_start","message":{"id":"msg_2","type":"message","role":"assistant","content":[],"model":"claude","usage":{"input_tokens":30,"output_tokens":1}}}"""),
				("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
				("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"北京多云"}}"""),
				("content_block_stop", """{"type":"content_block_stop","index":0}"""),
				("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":4}}"""),
				("message_stop", """{"type":"message_stop"}""")));
		using HttpClient http = new(handler);
		ModelCapabilities capabilities = Capabilities("anthropic", "claude-sonnet-4-5") with {ReasoningBudget = true, OutputLimit = 64000};
		ChatClientLlmAdapter adapter = new(LlmProvider.Anthropic, http, new LlmGenerationOptions {Reasoning = LlmReasoningLevel.Medium, Capabilities = capabilities});
		List<string> chunks = [];

		string text = await adapter.StreamWithToolsAsync(
			"https://api.anthropic.com/v1", "ant", "claude-sonnet-4-5", "系统", Hello, [WeatherTool()],
			(_, _) => Task.FromResult(new ToolResult(JsonValue.Create("多云"), null)), chunks.Add);

		Assert.Equal("北京多云", text);
		Assert.DoesNotContain(chunks, chunk => chunk.Contains("需要查", StringComparison.Ordinal));
		JsonNode first = JsonNode.Parse(handler.Requests[0].Body)!;
		Assert.Equal(8192, first["thinking"]!["budget_tokens"]!.GetValue<int>());
		Assert.True(first["max_tokens"]!.GetValue<int>() > 8192);

		JsonArray messages = JsonNode.Parse(handler.Requests[1].Body)!["messages"]!.AsArray();
		JsonArray assistant = messages.Single(message => message?["role"]?.GetValue<string>() == "assistant")!["content"]!.AsArray();
		Assert.Equal("thinking", assistant[0]!["type"]!.GetValue<string>());
		Assert.Equal("SIG-1", assistant[0]!["signature"]!.GetValue<string>());
		Assert.Equal("需要查北京天气", assistant[0]!["thinking"]!.GetValue<string>());
		Assert.Equal("tool_use", assistant[1]!["type"]!.GetValue<string>());
	}

	[Fact]
	public async Task Gemini3使用thinkingLevel而2点5使用预算()
	{
		ScriptedHandler handler = new(_ => Json("""{"candidates":[{"content":{"parts":[{"text":"好"}],"role":"model"},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":1,"totalTokenCount":4}}"""));
		using HttpClient http = new(handler);

		ChatClientLlmAdapter gemini3 = new(LlmProvider.Google, http, new LlmGenerationOptions {Reasoning = LlmReasoningLevel.Low});
		await gemini3.CompleteAsync("https://example.test/v1beta", "g", "gemini-3-pro-preview", "系统", Hello);
		ChatClientLlmAdapter gemini25 = new(LlmProvider.Google, http, new LlmGenerationOptions {Reasoning = LlmReasoningLevel.Low});
		await gemini25.CompleteAsync("https://example.test/v1beta", "g", "gemini-2.5-flash", "系统", Hello);

		JsonNode first = JsonNode.Parse(handler.Requests[0].Body)!["generationConfig"]!["thinkingConfig"]!;
		Assert.Equal("low", first["thinkingLevel"]!.GetValue<string>());
		JsonNode second = JsonNode.Parse(handler.Requests[1].Body)!["generationConfig"]!["thinkingConfig"]!;
		Assert.Equal(2048, second["thinkingBudget"]!.GetValue<int>());
	}

	private static (ImageGenerationService Service, GeneratedImageStore Store, string Directory) ImageService(
		HttpMessageHandler handler, AiImageSettings settings)
	{
		string directory = Path.Combine(Path.GetTempPath(), $"nori-images-{Guid.NewGuid():N}");
		GeneratedImageStore store = new(directory);
		return (new ImageGenerationService(new HttpClient(handler), () => settings, store), store, directory);
	}

	private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

	[Fact]
	public async Task OpenAI图像接口保存base64结果()
	{
		ScriptedHandler handler = new(_ => Json($$"""{"data":[{"b64_json":"{{Convert.ToBase64String(Png)}}","revised_prompt":"一只橘猫"}]}"""));
		AiImageSettings settings = new() {Provider = "openai", BaseUrl = "https://example.test/v1/chat/completions", ApiKey = "sk-img", Model = "gpt-image-1", Size = "auto"};
		(ImageGenerationService service, GeneratedImageStore store, string directory) = ImageService(handler, settings);
		try
		{
			GeneratedImage image = await service.GenerateAsync("一只猫", "16:9");

			(HttpRequestMessage request, string body) = Assert.Single(handler.Requests);
			Assert.Equal("https://example.test/v1/images/generations", request.RequestUri?.ToString());
			Assert.Equal("sk-img", request.Headers.Authorization?.Parameter);
			JsonNode json = JsonNode.Parse(body)!;
			Assert.Equal("1536x1024", json["size"]!.GetValue<string>());
			Assert.Null(json["response_format"]);
			Assert.EndsWith(".png", image.Name, StringComparison.Ordinal);
			Assert.Equal("一只橘猫", image.RevisedPrompt);
			Assert.Equal(Png, await store.ReadAsync(image.Name));
			Assert.Equal(image.Name, GeneratedImageStore.ParseReference(image.Markdown[(image.Markdown.IndexOf('(') + 1)..^1]));
		}
		finally
		{
			if (Directory.Exists(directory)) Directory.Delete(directory, true);
		}
	}

	[Fact]
	public async Task Gemini图像模型读取inlineData()
	{
		ScriptedHandler handler = new(_ => Json($$$"""{"candidates":[{"content":{"parts":[{"text":"给你"},{"inlineData":{"mimeType":"image/png","data":"{{{Convert.ToBase64String(Png)}}}"}}]}}]}"""));
		AiImageSettings settings = new() {Provider = "google", BaseUrl = "https://example.test", ApiKey = "g-key", Model = "gemini-2.5-flash-image", Size = "1:1"};
		(ImageGenerationService service, _, string directory) = ImageService(handler, settings);
		try
		{
			GeneratedImage image = await service.GenerateAsync("一只猫", null);

			(HttpRequestMessage request, string body) = Assert.Single(handler.Requests);
			Assert.Equal("https://example.test/v1beta/models/gemini-2.5-flash-image:generateContent", request.RequestUri?.ToString());
			Assert.Equal("g-key", request.Headers.GetValues("x-goog-api-key").Single());
			Assert.Equal("1:1", JsonNode.Parse(body)!["generationConfig"]!["imageConfig"]!["aspectRatio"]!.GetValue<string>());
			Assert.Equal("给你", image.RevisedPrompt);
		}
		finally
		{
			if (Directory.Exists(directory)) Directory.Delete(directory, true);
		}
	}

	[Fact]
	public void 图片引用只接受本存储生成的文件名()
	{
		Assert.Null(GeneratedImageStore.ParseReference("nori-image:../secret.png"));
		Assert.Null(GeneratedImageStore.ParseReference("nori-image:a/b.png"));
		Assert.Null(GeneratedImageStore.ParseReference("https://example.test/a.png"));
		Assert.Equal("20260101-000000-abcd1234.png", GeneratedImageStore.ParseReference("nori-image:20260101-000000-abcd1234.png"));
		Assert.Equal("画好啦", GeneratedImageStore.StripReferences("画好啦\n\n![猫](nori-image:20260101-000000-abcd1234.png)"));
	}

	[Fact]
	public void 图像配置留空时只沿用同协议的对话服务()
	{
		AiProviderSettings settings = new()
		{
			Chat = new AiChatSettings {Provider = LlmProvider.OpenAi, BaseUrl = "https://chat.test/v1", ApiKey = "chat-key", Model = "gpt-5", Persona = ""},
			Embedding = new AiEmbeddingSettings {BaseUrl = "", ApiKey = "", Model = ""},
			Image = AiImageSettings.Empty with {Model = "gpt-image-1"},
		};

		AiImageSettings resolved = AiSettingsStore.ResolveImage(settings);
		Assert.True(resolved.IsConfigured);
		Assert.Equal("chat-key", resolved.ApiKey);

		AiImageSettings google = AiSettingsStore.ResolveImage(settings with {Image = settings.Image with {Provider = "google"}});
		Assert.False(google.IsConfigured);
	}

	[Fact]
	public void 未配置时不注册图像工具()
	{
		ToolRegistry registry = new();
		ImageTools.RegisterAll(registry, new StubGenerator(false));
		Assert.Null(registry.Get(ImageTools.GenerateImageName));

		ImageTools.RegisterAll(registry, new StubGenerator(true));
		Assert.Equal("confirm", registry.Get(ImageTools.GenerateImageName)?.PermissionLevel);
	}

	private sealed class StubGenerator(bool configured) : IImageGenerator
	{
		public bool IsConfigured => configured;

		public Task<GeneratedImage> GenerateAsync(string prompt, string? aspectRatio, CancellationToken cancellationToken = default) =>
			Task.FromResult(new GeneratedImage("a.png", "![x](nori-image:a.png)", null));
	}
}
