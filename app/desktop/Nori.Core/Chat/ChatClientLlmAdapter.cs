using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Nori.Core.Tools;

namespace Nori.Core.Chat;

/// <summary>
/// 将官方 Microsoft.Extensions.AI IChatClient 适配到 Nori 的旧 ILlmAdapter 契约。
/// PromptBuilder、流式 JSON 解析与桥接协议因此无需改动。
/// </summary>
public sealed class ChatClientLlmAdapter : ILlmAdapter, IToolCallingLlmAdapter, IConfigurableLlmAdapter
{
	private readonly Func<string, string, string, LlmGenerationOptions, IChatClient> _clientFactory;
	private readonly LlmGenerationOptions _generation;

	/// <summary>内置协议沿用原有客户端与生成参数转换。</summary>
	public ChatClientLlmAdapter(LlmProvider provider, HttpClient httpClient, LlmGenerationOptions? generation = null)
		: this((baseUrl, apiKey, model, options) => ChatClientFactory.TryCreateChatClient(provider, httpClient, baseUrl, apiKey, model, options)
			?? throw new ChatException($"不支持的模型提供商: {provider}"), generation)
	{
	}

	/// <summary>每次请求创建独立客户端；客户端由适配器在请求结束时释放。</summary>
	public ChatClientLlmAdapter(Func<string, string, string, IChatClient> clientFactory, LlmGenerationOptions? generation = null)
		: this((baseUrl, apiKey, model, _) => clientFactory(baseUrl, apiKey, model), generation)
	{
		ArgumentNullException.ThrowIfNull(clientFactory);
	}

	/// <summary>允许客户端工厂接收生成参数；WithGeneration 保留同一个工厂。</summary>
	public ChatClientLlmAdapter(
		Func<string, string, string, LlmGenerationOptions, IChatClient> clientFactory,
		LlmGenerationOptions? generation = null)
	{
		ArgumentNullException.ThrowIfNull(clientFactory);
		_clientFactory = clientFactory;
		_generation = generation ?? LlmGenerationOptions.Default;
	}

	/// <summary>当前生成参数。</summary>
	public LlmGenerationOptions Generation => _generation;

	/// <inheritdoc />
	public ILlmAdapter WithGeneration(LlmGenerationOptions options) => new ChatClientLlmAdapter(_clientFactory, options);

	public async Task<string> CompleteAsync(
		string baseUrl,
		string apiKey,
		string model,
		string systemPrompt,
		IReadOnlyList<ChatMessageInput> messages,
		CancellationToken cancellationToken = default)
	{
		using IChatClient client = _clientFactory(baseUrl, apiKey, model, _generation);

		try
		{
			ChatResponse response = await client.GetResponseAsync(
				BuildMessages(systemPrompt, messages),
				new ChatOptions {ModelId = model},
				cancellationToken);
			if (string.IsNullOrEmpty(response.Text)) throw new ChatException("接口响应格式异常: 缺少回复文本");
			return response.Text;
		}
		catch (ChatException)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new ChatException($"请求失败: {exception.Message}", exception);
		}
	}

	public async Task<string> StreamAsync(
		string baseUrl,
		string apiKey,
		string model,
		string systemPrompt,
		IReadOnlyList<ChatMessageInput> messages,
		Action<string> onChunk,
		Action<LlmUsageInfo>? onUsage = null,
		CancellationToken cancellationToken = default)
	{
		using IChatClient client = _clientFactory(baseUrl, apiKey, model, _generation);

		System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
		System.Text.StringBuilder fullText = new();
		UsageDetails? usage = null;
		try
		{
			await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
				BuildMessages(systemPrompt, messages),
				new ChatOptions {ModelId = model},
				cancellationToken))
			{
				if (!string.IsNullOrEmpty(update.Text))
				{
					fullText.Append(update.Text);
					onChunk(update.Text);
				}
				foreach (AIContent content in update.Contents)
				{
					if (content is UsageContent usageContent) usage = usageContent.Details;
				}
			}
			return FinishStream(usage, fullText, systemPrompt, messages, model, stopwatch, onUsage);
		}
		catch (ChatException)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new ChatException($"请求失败: {exception.Message}", exception);
		}
	}

	public async Task<string> StreamWithToolsAsync(
		string baseUrl,
		string apiKey,
		string model,
		string systemPrompt,
		IReadOnlyList<ChatMessageInput> messages,
		IReadOnlyList<RegisteredTool> tools,
		Func<string, JsonNode?, Task<ToolResult>> executeTool,
		Action<string> onChunk,
		Action<LlmUsageInfo>? onUsage = null,
		CancellationToken cancellationToken = default)
	{
		IChatClient rawClient = _clientFactory(baseUrl, apiKey, model, _generation);
		using IChatClient client = new ChatClientBuilder(rawClient).UseFunctionInvocation().Build();

		AIFunction[] functions = tools.Select(tool => AIFunctionFactory.Create(
			async (AIFunctionArguments arguments, CancellationToken token) =>
			{
				JsonObject json = [];
				foreach ((string key, object? value) in arguments)
				{
					json[key] = value is null ? null : JsonSerializer.SerializeToNode(value);
				}
				ToolResult result = await executeTool(tool.Name, json);
				if (!result.IsSuccess) throw new InvalidOperationException(result.Error);
				return result.Result;
			}, tool.Name, tool.Description)).ToArray();

		ChatOptions options = new()
		{
			ModelId = model,
			Tools = functions,
			AllowMultipleToolCalls = true,
		};

		System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
		System.Text.StringBuilder fullText = new();
		UsageDetails? usage = null;
		try
		{
			await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
				BuildMessages(systemPrompt, messages), options, cancellationToken))
			{
				if (!string.IsNullOrEmpty(update.Text))
				{
					fullText.Append(update.Text);
					onChunk(update.Text);
				}
				foreach (AIContent content in update.Contents)
				{
					if (content is UsageContent usageContent) usage = usageContent.Details;
				}
			}

			return FinishStream(usage, fullText, systemPrompt, messages, model, stopwatch, onUsage);
		}
		catch (Exception exception) when (ToolsUnsupportedException.TryCreate(exception, out ToolsUnsupportedException? unsupported))
		{
			throw unsupported!;
		}
		catch (ChatException)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new ChatException($"工具调用请求失败: {exception.Message}", exception);
		}
	}

	internal static IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> BuildMessages(string systemPrompt, IReadOnlyList<ChatMessageInput> messages)
	{
		ChatMessageInput.ValidateImageLimits(messages);
		List<Microsoft.Extensions.AI.ChatMessage> result = [new(ChatRole.System, systemPrompt)];
		foreach (ChatMessageInput message in messages)
		{
			ChatRole role = message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
				? ChatRole.Assistant
				: ChatRole.User;
			if (message.ImageParts.Count == 0)
			{
				result.Add(new Microsoft.Extensions.AI.ChatMessage(role, message.Content));
				continue;
			}

			List<AIContent> contents = [new TextContent(message.Content)];
			foreach (ChatImagePart imagePart in message.ImageParts)
			{
				contents.Add(new DataContent(imagePart.Bytes.ToArray(), imagePart.MimeType));
			}
			result.Add(new Microsoft.Extensions.AI.ChatMessage(role, contents));
		}
		return result;
	}

	private static int EstimateTokens(string systemPrompt, IReadOnlyList<ChatMessageInput> messages) =>
		(int)((systemPrompt.Length + messages.Sum(message => message.Content.Length)) / 3.2);

	/// <summary>流式收尾: 汇报用量(缺省时按 token 估算兜底)并返回完整回复。</summary>
	private string FinishStream(
		UsageDetails? usage,
		System.Text.StringBuilder fullText,
		string systemPrompt,
		IReadOnlyList<ChatMessageInput> messages,
		string model,
		System.Diagnostics.Stopwatch stopwatch,
		Action<LlmUsageInfo>? onUsage)
	{
		stopwatch.Stop();
		UsageDetails actual = usage ?? new UsageDetails
		{
			InputTokenCount = Math.Max(1, EstimateTokens(systemPrompt, messages)),
			OutputTokenCount = Math.Max(1, (int)(fullText.Length / 3.2)),
		};
		onUsage?.Invoke(new LlmUsageInfo
		{
			PromptTokens = (int)Math.Min(int.MaxValue, actual.InputTokenCount ?? 0),
			CompletionTokens = (int)Math.Min(int.MaxValue, actual.OutputTokenCount ?? 0),
			TotalTokens = (int)Math.Min(int.MaxValue, actual.TotalTokenCount ?? (actual.InputTokenCount ?? 0) + (actual.OutputTokenCount ?? 0)),
			CachedTokens = (int)Math.Min(int.MaxValue, actual.CachedInputTokenCount ?? 0),
			DurationMs = stopwatch.ElapsedMilliseconds,
			Model = model,
		});
		return fullText.ToString();
	}
}
