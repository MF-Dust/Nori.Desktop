using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nori.Core.Chat;
using Nori.Core.Tests.TestSupport;
using Nori.Core.Tools;

namespace Nori.Core.Tests;

/// <summary>
/// 四种模型协议经 LlmTornado 的端到端行为：请求地址与认证头、流式去重、工具往返、图片编码与错误脱敏。
/// 服务端全部是假响应，断言的是发出去的 HTTP 和交还给 Nori 的结果。
/// </summary>
public sealed class TornadoChatClientTests
{
	private static readonly ChatMessageInput[] Hello = [new("user", "你好")];

	/// <summary>按顺序回放响应并记录每次请求。</summary>
	private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
	{
		private int _index;

		public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
			Requests.Add((request, body));
			Func<HttpRequestMessage, HttpResponseMessage> responder = responses[Math.Min(_index++, responses.Length - 1)];
			HttpResponseMessage response = responder(request);
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

	private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
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

	[Fact]
	public async Task OpenAI流式文本不重复且回报用量与标识()
	{
		ScriptedHandler handler = new(_ => Sse(
			"""{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","content":"你"}}]}""",
			"""{"id":"c1","choices":[{"index":0,"delta":{"content":"好"}}]}""",
			"""{"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
			"""{"id":"c1","choices":[],"usage":{"prompt_tokens":11,"completion_tokens":2,"total_tokens":13}}""",
			"[DONE]"));
		using HttpClient http = new(handler);
		ChatClientLlmAdapter adapter = new(LlmProvider.OpenAi, http);
		List<string> chunks = [];
		LlmUsageInfo? usage = null;

		string text = await adapter.StreamAsync("https://example.test/v1", "sk-secret", "deepseek-chat", "系统", Hello, chunks.Add, info => usage = info);

		Assert.Equal("你好", text);
		Assert.Equal(["你", "好"], chunks);
		Assert.Equal(11, usage?.PromptTokens);
		Assert.Equal(2, usage?.CompletionTokens);
		(HttpRequestMessage request, string body) = Assert.Single(handler.Requests);
		Assert.Equal("https://example.test/v1/chat/completions", request.RequestUri?.ToString());
		Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
		Assert.Equal("sk-secret", request.Headers.Authorization?.Parameter);
		Assert.False(request.Headers.Contains("api-key"));
		Assert.Equal(ChatClientFactory.UserAgent, string.Join(" ", request.Headers.UserAgent.Select(part => part.ToString())));
		JsonObject json = JsonNode.Parse(body)!.AsObject();
		Assert.Equal("deepseek-chat", json["model"]!.GetValue<string>());
		Assert.True(json["stream"]!.GetValue<bool>());
		Assert.Equal("system", json["messages"]![0]!["role"]!.GetValue<string>());
	}

	[Fact]
	public async Task OpenAI非流式请求与图片数据URL()
	{
		ScriptedHandler handler = new(_ => Json("""{"id":"c2","choices":[{"index":0,"message":{"role":"assistant","content":"看到了"},"finish_reason":"stop"}]}"""));
		using HttpClient http = new(handler);
		ChatClientLlmAdapter adapter = new(LlmProvider.OpenAi, http);
		ChatMessageInput input = new("user", "看图") {ImageParts = [new ChatImagePart([1, 2, 3], "image/png")]};

		string text = await adapter.CompleteAsync("https://example.test/v1/chat/completions", "sk", "gpt-4o-mini", "系统", [input]);

		Assert.Equal("看到了", text);
		string body = Assert.Single(handler.Requests).Body;
		Assert.Contains("data:image/png;base64,AQID", body, StringComparison.Ordinal);
		Assert.DoesNotContain("\"stream\":true", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task OpenAI流式工具调用往返()
	{
		ScriptedHandler handler = new(
			_ => Sse(
				"""{"id":"t1","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":""}}]}}]}""",
				"""{"id":"t1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":"}}]}}]}""",
				"""{"id":"t1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"上海\"}"}}]}}]}""",
				"""{"id":"t1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
				"[DONE]"),
			_ => Sse(
				"""{"id":"t2","choices":[{"index":0,"delta":{"role":"assistant","content":"上海晴"}}]}""",
				"""{"id":"t2","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
				"[DONE]"));
		using HttpClient http = new(handler);
		ChatClientLlmAdapter adapter = new(LlmProvider.OpenAi, http);
		List<(string Name, JsonNode? Args)> calls = [];

		string text = await adapter.StreamWithToolsAsync(
			"https://example.test/v1", "sk", "gpt-4o-mini", "系统", Hello, [WeatherTool()],
			(name, args) =>
			{
				calls.Add((name, args));
				return Task.FromResult(new ToolResult(JsonValue.Create("晴天"), null));
			},
			_ => { });

		Assert.Equal("上海晴", text);
		(string name, JsonNode? arguments) = Assert.Single(calls);
		Assert.Equal("get_weather", name);
		Assert.Equal("上海", arguments?["city"]?.GetValue<string>());
		Assert.Equal(2, handler.Requests.Count);
		JsonObject first = JsonNode.Parse(handler.Requests[0].Body)!.AsObject();
		Assert.Equal("get_weather", first["tools"]![0]!["function"]!["name"]!.GetValue<string>());
		JsonArray second = JsonNode.Parse(handler.Requests[1].Body)!["messages"]!.AsArray();
		JsonNode assistant = second.Single(message => message?["role"]?.GetValue<string>() == "assistant")!;
		Assert.Equal("call_1", assistant["tool_calls"]![0]!["id"]!.GetValue<string>());
		JsonNode tool = second.Single(message => message?["role"]?.GetValue<string>() == "tool")!;
		Assert.Equal("call_1", tool["tool_call_id"]!.GetValue<string>());
		Assert.Contains("晴天", tool["content"]!.GetValue<string>(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Anthropic流式工具调用使用原生协议()
	{
		ScriptedHandler handler = new(
			_ => NamedSse(
				("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude","usage":{"input_tokens":20,"output_tokens":1}}}"""),
				("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{}}}"""),
				("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"city\":\"北京\"}"}}"""),
				("content_block_stop", """{"type":"content_block_stop","index":0}"""),
				("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":9}}"""),
				("message_stop", """{"type":"message_stop"}""")),
			_ => NamedSse(
				("message_start", """{"type":"message_start","message":{"id":"msg_2","type":"message","role":"assistant","content":[],"model":"claude","usage":{"input_tokens":30,"output_tokens":1}}}"""),
				("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
				("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"北京"}}"""),
				("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"多云"}}"""),
				("content_block_stop", """{"type":"content_block_stop","index":0}"""),
				("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":4}}"""),
				("message_stop", """{"type":"message_stop"}""")));
		using HttpClient http = new(handler);
		ChatClientLlmAdapter adapter = new(LlmProvider.Anthropic, http);
		List<string> chunks = [];
		string? city = null;

		string text = await adapter.StreamWithToolsAsync(
			"https://api.anthropic.com/v1", "ant-secret", "claude-sonnet-4-5", "系统", Hello, [WeatherTool()],
			(_, args) =>
			{
				city = args?["city"]?.GetValue<string>();
				return Task.FromResult(new ToolResult(JsonValue.Create("多云"), null));
			},
			chunks.Add);

		Assert.Equal("北京多云", text);
		Assert.Equal(["北京", "多云"], chunks);
		Assert.Equal("北京", city);
		(HttpRequestMessage request, string body) = handler.Requests[0];
		Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri?.ToString());
		Assert.Equal("ant-secret", request.Headers.GetValues("x-api-key").Single());
		Assert.Equal(4096, JsonNode.Parse(body)!["max_tokens"]!.GetValue<int>());
		string second = handler.Requests[1].Body;
		Assert.Contains("\"tool_use_id\":\"toolu_1\"", second, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Gemini流式文本密钥走请求头且图片为裸base64()
	{
		// Gemini 的 streamGenerateContent 不带 alt=sse 时返回逐步写出的 JSON 数组。
		// 此处覆盖带 usageMetadata 的响应块；LlmTornado 当前无法解析缺失用量的块。
		ScriptedHandler handler = new(_ => Json("""
			[{"candidates":[{"content":{"role":"model","parts":[{"text":"晴"}]},"index":0}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":1,"totalTokenCount":6}},
			{"candidates":[{"content":{"role":"model","parts":[{"text":"天"}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":2,"totalTokenCount":7}}]
			"""));
		using HttpClient http = new(handler);
		ChatClientLlmAdapter adapter = new(LlmProvider.Google, http);
		ChatMessageInput input = new("user", "看图") {ImageParts = [new ChatImagePart([1, 2, 3], "image/png")]};
		List<string> chunks = [];

		string text = await adapter.StreamAsync("https://generativelanguage.googleapis.com/v1beta", "g-secret", "gemini-2.5-flash", "系统", [input], chunks.Add);

		Assert.Equal("晴天", text);
		Assert.Equal(["晴", "天"], chunks);
		(HttpRequestMessage request, string body) = Assert.Single(handler.Requests);
		string url = request.RequestUri!.ToString();
		Assert.StartsWith("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent", url, StringComparison.Ordinal);
		Assert.DoesNotContain("g-secret", url, StringComparison.Ordinal);
		Assert.Equal("g-secret", request.Headers.GetValues("x-goog-api-key").Single());
		Assert.Contains("\"data\":\"AQID\"", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Responses协议流式文本()
	{
		ScriptedHandler handler = new(_ => NamedSse(
			("response.created", """{"type":"response.created","sequence_number":0,"response":{"id":"resp_1","object":"response","created_at":1,"status":"in_progress","model":"gpt-5","output":[]}}"""),
			("response.output_text.delta", """{"type":"response.output_text.delta","sequence_number":1,"item_id":"msg_1","output_index":0,"content_index":0,"delta":"早"}"""),
			("response.output_text.delta", """{"type":"response.output_text.delta","sequence_number":2,"item_id":"msg_1","output_index":0,"content_index":0,"delta":"安"}"""),
			("response.completed", """{"type":"response.completed","sequence_number":3,"response":{"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"gpt-5","output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"早安","annotations":[]}]}],"usage":{"input_tokens":8,"output_tokens":2,"total_tokens":10}}}""")));
		using HttpClient http = new(handler);
		ChatClientLlmAdapter adapter = new(LlmProvider.OpenAiResponses, http);
		List<string> chunks = [];
		LlmUsageInfo? usage = null;

		string text = await adapter.StreamAsync("https://example.test/v1", "sk", "gpt-5", "系统", Hello, chunks.Add, info => usage = info);

		Assert.Equal("早安", text);
		Assert.Equal(["早", "安"], chunks);
		Assert.Equal(8, usage?.PromptTokens);
		(HttpRequestMessage request, string body) = Assert.Single(handler.Requests);
		Assert.Equal("https://example.test/v1/responses", request.RequestUri?.ToString());
		JsonObject json = JsonNode.Parse(body)!.AsObject();
		Assert.True(json["stream"]!.GetValue<bool>());
		// 系统提示与用户消息各出现一次，不能被会话重复追加。
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "你好"));
	}

	[Fact]
	public async Task Responses协议工具调用往返()
	{
		ScriptedHandler handler = new(
			_ => NamedSse(
				("response.created", """{"type":"response.created","sequence_number":0,"response":{"id":"resp_t","object":"response","created_at":1,"status":"in_progress","model":"gpt-5","output":[]}}"""),
				("response.completed", """{"type":"response.completed","sequence_number":1,"response":{"id":"resp_t","object":"response","created_at":1,"status":"completed","model":"gpt-5","output":[{"type":"function_call","id":"fc_1","call_id":"call_r1","name":"get_weather","arguments":"{\"city\":\"广州\"}","status":"completed"}],"tools":[{"type":"function","name":"get_weather","parameters":{"type":"object","properties":{}}}],"usage":{"input_tokens":9,"output_tokens":3,"total_tokens":12}}}""")),
			_ => NamedSse(
				("response.output_text.delta", """{"type":"response.output_text.delta","sequence_number":0,"item_id":"msg_2","output_index":0,"content_index":0,"delta":"广州有雨"}"""),
				("response.completed", """{"type":"response.completed","sequence_number":1,"response":{"id":"resp_2","object":"response","created_at":1,"status":"completed","model":"gpt-5","output":[{"type":"message","id":"msg_2","status":"completed","role":"assistant","content":[{"type":"output_text","text":"广州有雨","annotations":[]}]}],"usage":{"input_tokens":15,"output_tokens":4,"total_tokens":19}}}""")));
		using HttpClient http = new(handler);
		ChatClientLlmAdapter adapter = new(LlmProvider.OpenAiResponses, http);
		string? city = null;

		string text = await adapter.StreamWithToolsAsync(
			"https://example.test/v1", "sk", "gpt-5", "系统", Hello, [WeatherTool()],
			(_, args) =>
			{
				city = args?["city"]?.GetValue<string>();
				return Task.FromResult(new ToolResult(JsonValue.Create("小雨"), null));
			},
			_ => { });

		Assert.Equal("广州有雨", text);
		Assert.Equal("广州", city);
		Assert.Equal(2, handler.Requests.Count);
		string second = handler.Requests[1].Body;
		Assert.Contains("\"call_id\":\"call_r1\"", second, StringComparison.Ordinal);
		Assert.Contains("function_call_output", second, StringComparison.Ordinal);
	}

	[Fact]
	public async Task HTTP错误保留状态码且不泄露密钥与提示词()
	{
		ScriptedHandler handler = new(_ => Json("""{"error":{"message":"Incorrect API key provided: sk-secret"}}""", HttpStatusCode.Unauthorized));
		using HttpClient http = new(handler);
		ChatClientLlmAdapter adapter = new(LlmProvider.OpenAi, http);

		ChatException error = await Assert.ThrowsAsync<ChatException>(() =>
			adapter.StreamAsync("https://example.test/v1", "sk-secret", "gpt-4o", "系统", [new("user", "私密提示词")], _ => { }));

		Assert.Contains("401", error.Message, StringComparison.Ordinal);
		Assert.Contains("Incorrect API key", error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("sk-secret", error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("私密提示词", error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("example.test", error.Message, StringComparison.Ordinal);
		HttpRequestException http401 = Assert.IsType<HttpRequestException>(error.InnerException);
		Assert.Equal(HttpStatusCode.Unauthorized, http401.StatusCode);
	}

	[Fact]
	public async Task 端点明确拒绝工具时归类为工具不支持()
	{
		ScriptedHandler handler = new(_ => Json("""{"error":{"message":"tools is not supported by this model"}}""", HttpStatusCode.BadRequest));
		using HttpClient http = new(handler);
		ChatClientLlmAdapter adapter = new(LlmProvider.OpenAi, http);

		ToolsUnsupportedException error = await Assert.ThrowsAsync<ToolsUnsupportedException>(() =>
			adapter.StreamWithToolsAsync("https://example.test/v1", "sk", "local", "系统", Hello, [WeatherTool()],
				(_, _) => Task.FromResult(new ToolResult(null, null)), _ => { }));

		Assert.Equal(400, error.StatusCode);
	}

	[Fact]
	public async Task 传输层异常原样交还()
	{
		using HttpTestHandler handler = new(_ => throw new InvalidOperationException("不允许访问的地址 (解析到私网或保留地址)"));
		using HttpClient http = new(handler);
		ChatClientLlmAdapter adapter = new(LlmProvider.OpenAi, http);

		ChatException error = await Assert.ThrowsAsync<ChatException>(() =>
			adapter.CompleteAsync("https://example.test/v1", "sk", "gpt-4o", "系统", Hello));

		Assert.IsType<InvalidOperationException>(error.InnerException);
		Assert.Contains("不允许访问的地址", error.Message, StringComparison.Ordinal);
	}
}
