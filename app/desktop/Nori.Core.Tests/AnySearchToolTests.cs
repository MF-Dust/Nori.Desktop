using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Emotion;
using Nori.Core.Embedding;
using Nori.Core.Logging;
using Nori.Core.Memory;
using Nori.Core.Network;
using Nori.Core.Proactive;
using Nori.Core.Tools;

namespace Nori.Core.Tests;

public sealed class AnySearchToolTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nori-anysearch-" + Guid.NewGuid().ToString("N"));
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;
	private readonly MemoryService _memory;
	private readonly EmotionManager _emotion;
	private readonly ProactiveScheduler _proactive;
	private readonly RecordingHandler _handler = new();
	private readonly HttpClient _http;
	private readonly ToolRegistry _tools;
	private readonly List<string> _diagnosticLogs = [];

	public AnySearchToolTests()
	{
		AppStoragePaths paths = new(_root);
		Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);
		_database = NoriDatabase.Open(paths.DatabasePath, paths);
		_config = new ConfigStore(_database, new Security.SecretKeyStore(paths));
		_memory = new MemoryService(new MemoryStore(_database), new NoEmbedding(), _config, startBackgroundWorker: false);
		_emotion = new EmotionManager(_config);
		_proactive = new ProactiveScheduler(
			new ReminderStore(_database), _config, new FileLogger(paths.LogsDirectory), () => null);
		_http = new HttpClient(_handler);
		_tools = new ToolRegistry
		{
			FailureDiagnostic = (tool, diagnostic) => _diagnosticLogs.Add(diagnostic.ToLogMessage(tool)),
		};
		BuiltinTools.RegisterAll(_tools, new BuiltinToolDeps
		{
			Memory = _memory,
			Emotion = _emotion,
			Proactive = _proactive,
			SystemInfo = new StubSystemInfo(),
			Fetcher = new StubFetcher(),
			Http = _http,
			Config = _config,
		});
	}

	[Theory]
	[InlineData("searchWeb")]
	[InlineData("anySearch")]
	public async Task 默认调用只发送Query且两个工具名行为一致(string toolName)
	{
		ToolResult result = await Call(toolName, new JsonObject {["query"] = "OpenAI news"});

		Assert.True(result.IsSuccess);
		JsonObject payload = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.Single(_handler.RequestBodies)));
		Assert.Equal("OpenAI news", payload["query"]!.GetValue<string>());
		Assert.False(payload.ContainsKey("tag"));
	}

	[Theory]
	[InlineData("general")]
	[InlineData("web")]
	[InlineData("news")]
	[InlineData(" General ")]
	[InlineData("WEB")]
	public async Task LegacyTag交给AnySearch自动路由(string tag)
	{
		ToolResult result = await Call("anySearch", new JsonObject {["query"] = "x", ["tag"] = tag});

		Assert.True(result.IsSuccess);
		JsonObject payload = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.Single(_handler.RequestBodies)));
		Assert.False(payload.ContainsKey("tag"));
	}

	[Theory]
	[InlineData("code.doc")]
	[InlineData("foo.bar")]
	public async Task CapabilityTag原样发送(string tag)
	{
		ToolResult result = await Call("searchWeb", new JsonObject {["query"] = "x", ["tag"] = tag});

		Assert.True(result.IsSuccess);
		JsonObject payload = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.Single(_handler.RequestBodies)));
		Assert.Equal(tag, payload["tag"]!.GetValue<string>());
	}

	[Fact]
	public void 工具Schema只描述CapabilityTag()
	{
		foreach (string toolName in new[] {"searchWeb", "anySearch"})
		{
			RegisteredTool tool = Assert.IsType<RegisteredTool>(_tools.Get(toolName));
			string description = tool.Parameters["properties"]!["tag"]!["description"]!.GetValue<string>();
			Assert.Contains("domain.sub_domain", description, StringComparison.Ordinal);
			Assert.DoesNotContain("general", description, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("news", description, StringComparison.OrdinalIgnoreCase);
		}
	}

	[Fact]
	public async Task 四百错误返回固定安全信息而不是原始Body()
	{
		_handler.ResponseFactory = () => JsonResponse(HttpStatusCode.BadRequest,
			"""{"code":-1,"message":"invalid tag:foo"}""");

		ToolResult result = await Call("anySearch", new JsonObject {["query"] = "x", ["tag"] = "foo"});

		Assert.Equal("AnySearch 请求参数无效。", result.Error);
		Assert.DoesNotContain("invalid tag", result.Error, StringComparison.OrdinalIgnoreCase);
		Assert.Equal("invalid_request", result.Diagnostic?.Category);
		Assert.Equal(400, result.Diagnostic?.StatusCode);
		Assert.Equal("-1", result.Diagnostic?.Code);
	}

	[Fact]
	public async Task 四百零二响应中的凭据不会进入错误异常或诊断日志()
	{
		const string Username = "private-user";
		const string Password = "private-password";
		const string ApiKey = "secret-api-key";
		string body = $$"""
			{"username":"{{Username}}","password":"{{Password}}","api_key":"{{ApiKey}}","message":"quota for {{Username}}"}
			""";
		_handler.ResponseFactory = () => JsonResponse(HttpStatusCode.PaymentRequired, body);

		ToolResult result = await Call("anySearch", new JsonObject {["query"] = "x"});
		string log = Assert.Single(_diagnosticLogs);
		AnySearchException exception = AnySearchError.Parse(HttpStatusCode.PaymentRequired, body);

		Assert.Equal("AnySearch 匿名额度已用尽，请配置 API Key。", result.Error);
		foreach (string secret in new[] {Username, Password, ApiKey})
		{
			Assert.DoesNotContain(secret, result.Error, StringComparison.Ordinal);
			Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
			Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
		}
		Assert.Contains("tool=anySearch", log, StringComparison.Ordinal);
		Assert.Contains("category=quota", log, StringComparison.Ordinal);
		Assert.Contains("status=402", log, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(HttpStatusCode.Unauthorized, "authentication")]
	[InlineData(HttpStatusCode.TooManyRequests, "rate_limit")]
	[InlineData(HttpStatusCode.InternalServerError, "upstream")]
	public void 常见状态码映射为稳定错误分类(HttpStatusCode statusCode, string category)
	{
		AnySearchException exception = AnySearchError.Parse(statusCode, """{"message":"private upstream detail"}""");

		Assert.Equal(category, exception.Category);
		Assert.DoesNotContain("private upstream detail", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 自定义端点不会继承已保存密钥()
	{
		_config.Set("anysearch_api_key", new ConfigValue.Text("stored-secret"));

		ToolResult result = await Call("anySearch", new JsonObject
		{
			["query"] = "x",
			["endpoint"] = "https://relay.example.com/v1/search",
		});

		Assert.Equal("AnySearch 配置无效。", result.Error);
		Assert.Equal("configuration", result.Diagnostic?.Category);
		Assert.Empty(_handler.RequestBodies);
		Assert.DoesNotContain("stored-secret", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 超长错误响应继续受CappedRead限制()
	{
		_handler.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.BadGateway)
		{
			Content = new ByteArrayContent(new byte[checked((int)UrlAccessPolicy.MaxResponseBytes + 1)]),
		};

		ToolResult result = await Call("searchWeb", new JsonObject {["query"] = "x"});

		Assert.NotNull(result.Error);
		Assert.Contains("大小上限", result.Error, StringComparison.Ordinal);
	}

	private Task<ToolResult> Call(string name, JsonObject args) =>
		_tools.ExecuteAsync(name, args, new ToolContext {CancellationToken = CancellationToken.None});

	private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) => new(statusCode)
	{
		Content = new StringContent(body, Encoding.UTF8, "application/json"),
	};

	public void Dispose()
	{
		_memory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_proactive.Dispose();
		_emotion.Dispose();
		_http.Dispose();
		_database.Dispose();
		try { Directory.Delete(_root, recursive: true); }
		catch (IOException) { }
		GC.SuppressFinalize(this);
	}

	private sealed class RecordingHandler : HttpMessageHandler
	{
		public List<string> RequestBodies { get; } = [];

		public Func<HttpResponseMessage> ResponseFactory { get; set; } = () =>
			JsonResponse(HttpStatusCode.OK, """{"results":[]}""");

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			RequestBodies.Add(request.Content is null
				? ""
				: await request.Content.ReadAsStringAsync(cancellationToken));
			return ResponseFactory();
		}
	}

	private sealed class NoEmbedding : IEmbeddingAdapter
	{
		public Task<float[]> GetEmbeddingAsync(string baseUrl, string apiKey, string model, string input,
			int? dimensions = null, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<float>());

		public Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(string baseUrl, string apiKey, string model,
			IReadOnlyList<string> inputs, int? dimensions = null, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => Array.Empty<float>()).ToArray());
	}

	private sealed class StubSystemInfo : ISystemInfoProvider
	{
		public object GetInfo() => new {os = "test"};
		public object? GetBatteryStatus() => null;
	}

	private sealed class StubFetcher : IWebPageFetcher
	{
		public Task<object> FetchAsync(string url, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}
}
