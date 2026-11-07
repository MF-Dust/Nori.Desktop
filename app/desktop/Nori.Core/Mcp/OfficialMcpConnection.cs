using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Nori.Core.Mcp;

/// <summary>
/// 官方 ModelContextProtocol 客户端适配器。应用其余部分继续消费
/// <see cref="McpModels"/> 中的小型 DTO 面，因此桥接协议保持稳定，传输与协议细节交给 SDK。
/// </summary>
internal sealed class OfficialMcpConnection : IAsyncDisposable
{
	private readonly ModelContextProtocol.Client.McpClient _client;
	private bool _connected = true;

	private OfficialMcpConnection(ModelContextProtocol.Client.McpClient client)
	{
		_client = client;
	}

	public bool IsConnected => _connected;

	public static async Task<OfficialMcpConnection> ConnectAsync(
		McpServerConfig config,
		HttpClient httpClient,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(httpClient);

		IClientTransport transport = CreateTransport(config, httpClient);
		try
		{
			// 保持 ProtocolVersion 未设置，以便 SDK 2.2 能探测服务端并降级到其实际支持的最新版本。
			McpClientOptions options = new();

			ModelContextProtocol.Client.McpClient client = await ModelContextProtocol.Client.McpClient.CreateAsync(
				transport,
				options,
				NullLoggerFactory.Instance,
				cancellationToken);

			return new OfficialMcpConnection(client);
		}
		catch
		{
			if (transport is IAsyncDisposable disposable)
			{
				try { await disposable.DisposeAsync(); }
				catch { /* 保留原始连接失败异常 */ }
			}
			throw;
		}
	}

	public async Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
	{
		IReadOnlyList<McpToolDefinition> tools = (await _client.ListToolsAsync(cancellationToken: cancellationToken))
			.Select(tool => new McpToolDefinition
			{
				Name = tool.Name,
				Description = tool.Description,
				InputSchema = ParseObject(tool.ProtocolTool.InputSchema),
			})
			.Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
			.ToArray();

		return tools;
	}

	public async Task<IReadOnlyList<McpResourceDefinition>> ListResourcesAsync(CancellationToken cancellationToken)
	{
		try
		{
			return (await _client.ListResourcesAsync(cancellationToken: cancellationToken))
				.Where(resource => !string.IsNullOrWhiteSpace(resource.Uri) && !string.IsNullOrWhiteSpace(resource.Name))
				.Select(resource => new McpResourceDefinition
				{
					Uri = resource.Uri,
					Name = resource.Name,
					Description = resource.Description,
					MimeType = resource.MimeType,
				})
				.ToArray();
		}
		catch
		{
			// 服务器可能不声明 resources；保持原管理器行为，按空列表处理。
			return [];
		}
	}

	public async Task<McpToolResult> CallToolAsync(
		string toolName,
		JsonObject? arguments,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(toolName))
		{
			throw new ArgumentException("工具名称不能为空", nameof(toolName));
		}

		Dictionary<string, object?> argumentValues = [];
		if (arguments is not null)
		{
			foreach ((string key, JsonNode? value) in arguments)
			{
				argumentValues[key] = ParseElement(value);
			}
		}

		ModelContextProtocol.Protocol.CallToolResult result = await _client.CallToolAsync(
			toolName,
			argumentValues,
			progress: null,
			options: null,
			cancellationToken);

		List<McpContentItem> content = [];
		foreach (ModelContextProtocol.Protocol.ContentBlock block in result.Content ?? [])
		{
			JsonObject? json = JsonSerializer.SerializeToNode(block)?.AsObject();
			content.Add(new McpContentItem
			{
				Type = block.Type ?? "text",
				Text = GetString(json, "text"),
				Data = GetString(json, "data"),
				MimeType = GetString(json, "mimeType"),
			});
		}

		return new McpToolResult
		{
			Content = content,
			IsError = result.IsError ?? false,
		};
	}

	public async ValueTask DisposeAsync()
	{
		if (!_connected) return;
		_connected = false;

		try
		{
			await _client.DisposeAsync();
		}
		catch (ClientTransportClosedException)
		{
			// 传输通道关闭是预期的关闭流程。
		}
		catch (AggregateException exception) when (exception.Flatten().InnerExceptions.All(IsExpectedClose))
		{
			// SDK 可能会将预期的会话关闭包装在 AggregateException 中。
		}
		GC.SuppressFinalize(this);
	}

	private static bool IsExpectedClose(Exception exception) =>
		exception is ClientTransportClosedException
			|| (exception is AggregateException aggregate && aggregate.Flatten().InnerExceptions.All(IsExpectedClose));

	private static IClientTransport CreateTransport(McpServerConfig config, HttpClient httpClient)
	{
		if (string.Equals(config.Transport, McpTransportType.Sse, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(config.Transport, McpTransportType.Http, StringComparison.OrdinalIgnoreCase))
		{
			bool sse = string.Equals(config.Transport, McpTransportType.Sse, StringComparison.OrdinalIgnoreCase);
			if (!Uri.TryCreate(config.Url, UriKind.Absolute, out Uri? endpoint) ||
				(endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
			{
				throw new InvalidOperationException(sse
					? $"MCP SSE 服务器 {config.Name} 的 URL 无效"
					: $"MCP HTTP 服务器 {config.Name} 的 URL 无效");
			}

			HttpClientTransportOptions options = new()
			{
				Endpoint = endpoint,
				TransportMode = sse ? HttpTransportMode.Sse : HttpTransportMode.AutoDetect,
				Name = config.Name,
				AdditionalHeaders = config.Headers is {Count: > 0}
					? new Dictionary<string, string>(config.Headers)
					: null,
			};
			return new HttpClientTransport(options, httpClient, NullLoggerFactory.Instance, ownsHttpClient: false);
		}

		if (string.IsNullOrWhiteSpace(config.Command))
		{
			throw new InvalidOperationException($"MCP 服务器 {config.Name} 缺少启动命令 (Command)");
		}

		Dictionary<string, string?> environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
		if (config.Env is not null)
		{
			foreach ((string key, string value) in config.Env)
			{
				environment[key] = value;
			}
		}

		StdioClientTransportOptions stdioOptions = new()
		{
			Command = config.Command,
			Arguments = config.Args ?? [],
			Name = config.Name,
			InheritEnvironmentVariables = false,
			EnvironmentVariables = environment,
		};
		return new StdioClientTransport(stdioOptions, NullLoggerFactory.Instance);
	}

	private static JsonObject? ParseObject(JsonElement element)
	{
		if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
		try
		{
			return JsonNode.Parse(element.GetRawText()) as JsonObject;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static JsonElement ParseElement(JsonNode? value)
	{
		return JsonSerializer.Deserialize<JsonElement>(value?.ToJsonString() ?? "null");
	}

	private static string? GetString(JsonObject? json, string propertyName)
	{
		if (json is null || json[propertyName] is null) return null;
		try
		{
			return json[propertyName]!.GetValue<string>();
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}
}
