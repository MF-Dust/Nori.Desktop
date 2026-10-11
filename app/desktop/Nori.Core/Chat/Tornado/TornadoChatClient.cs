using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using LlmTornado;
using LlmTornado.Chat;
using LlmTornado.Chat.Models;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using LlmTornado.Common;
using LlmTornado.Responses.Events;
using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using TornadoChatMessage = LlmTornado.Chat.ChatMessage;
using TornadoFunctionCall = LlmTornado.ChatFunctions.FunctionCall;

namespace Nori.Core.Chat.Tornado;

/// <summary>
/// 基于 LlmTornado 的 <see cref="IChatClient"/>。
///
/// 只实现 Nori 用到的子集：文本、流式、函数工具、图片输入与用量。四种协议共享一套消息转换；
/// OpenAI Responses 协议的流式输出走 LlmTornado 的 Conversation 事件流。
/// </summary>
internal sealed class TornadoChatClient : IChatClient
{
	/// <summary>Gemini 3 要求回传工具调用的 thought signature，借 FunctionCallContent 的扩展属性保存。</summary>
	internal const string ThoughtSignatureKey = "nori.thought_signature";

	/// <summary>Anthropic 的 redacted_thinking 块在 TextReasoningContent 上的标记。</summary>
	internal const string RedactedReasoningKey = "nori.reasoning_redacted";

	private readonly TornadoApi _api;
	private readonly TornadoCallScope _scope;
	private readonly string _model;
	private readonly LLmProviders _provider;
	private readonly int? _defaultMaxOutputTokens;
	private readonly LlmGenerationOptions _generation;

	public TornadoChatClient(
		TornadoApi api,
		TornadoCallScope scope,
		string model,
		LLmProviders provider,
		int? defaultMaxOutputTokens,
		LlmGenerationOptions? generation = null)
	{
		_api = api;
		_scope = scope;
		_model = model;
		_provider = provider;
		_defaultMaxOutputTokens = defaultMaxOutputTokens;
		_generation = generation ?? LlmGenerationOptions.Default;
	}

	public async Task<ChatResponse> GetResponseAsync(
		IEnumerable<AiChatMessage> messages,
		ChatOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(messages);
		ChatRequest request = BuildRequest(messages.ToList(), options, cancellationToken);
		_scope.Reset();
		ChatResult? result;
		try
		{
			result = await _api.Chat.CreateChatCompletion(request).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			throw TranslateException(exception, cancellationToken);
		}
		if (result is null) throw new InvalidOperationException("模型服务响应格式无法解析 (空结果)");

		StreamState state = new();
		List<ChatResponseUpdate> updates = [];
		if (state.Accept(result) is { } update) updates.Add(update);
		updates.AddRange(state.Finish());
		ChatResponse response = updates.ToChatResponse();
		response.ResponseId ??= result.Id;
		response.ModelId ??= request.Model?.Name;
		return response;
	}

	public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<AiChatMessage> messages,
		ChatOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(messages);
		ChatRequest request = BuildRequest(messages.ToList(), options, cancellationToken);
		return _scope.Protocol == LlmProvider.OpenAiResponses
			? StreamResponsesAsync(request, cancellationToken)
			: StreamChatAsync(request, cancellationToken);
	}

	public object? GetService(Type serviceType, object? serviceKey = null) =>
		serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

	public void Dispose()
	{
		// TornadoApi 不持有非托管资源；HTTP 连接属于调用方的共享 HttpClient。
	}

	private async IAsyncEnumerable<ChatResponseUpdate> StreamChatAsync(
		ChatRequest request,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		_scope.Reset();
		StreamState state = new();
		IAsyncEnumerator<ChatResult> enumerator = _api.Chat.StreamChatEnumerable(request).GetAsyncEnumerator(cancellationToken);
		try
		{
			while (true)
			{
				ChatResult result;
				try
				{
					if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
					result = enumerator.Current;
				}
				catch (Exception exception)
				{
					throw TranslateException(exception, cancellationToken);
				}

				if (state.Accept(result) is { } update) yield return update;
			}
		}
		finally
		{
			await enumerator.DisposeAsync().ConfigureAwait(false);
		}

		foreach (ChatResponseUpdate update in state.Finish()) yield return update;
	}

	/// <summary>Responses 协议：LlmTornado 只在 Conversation 上提供事件流，转成异步序列。</summary>
	private async IAsyncEnumerable<ChatResponseUpdate> StreamResponsesAsync(
		ChatRequest request,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		_scope.Reset();
		// CreateConversation 会把原型请求里的消息复制进会话，不能再逐条追加。
		Conversation conversation = _api.Chat.CreateConversation(request);

		Channel<string> tokens = Channel.CreateUnbounded<string>(new UnboundedChannelOptions {SingleReader = true});
		List<TornadoFunctionCall> calls = [];
		UsageDetails? usage = null;
		ChatStreamEventHandler handler = new()
		{
			MessageTokenHandler = async token =>
			{
				if (!string.IsNullOrEmpty(token)) await tokens.Writer.WriteAsync(token, cancellationToken).ConfigureAwait(false);
			},
			FunctionCallHandler = found =>
			{
				calls.AddRange(found);
				return ValueTask.CompletedTask;
			},
			OnResponseEvent = evt =>
			{
				if (evt is ResponseEventCompleted {Response.Usage: { } responseUsage})
				{
					usage = new UsageDetails
					{
						InputTokenCount = responseUsage.InputTokens,
						OutputTokenCount = responseUsage.OutputTokens,
						TotalTokenCount = responseUsage.TotalTokens,
						CachedInputTokenCount = responseUsage.InputTokenDetails?.CachedTokens,
					};
				}
				return ValueTask.CompletedTask;
			},
		};

		Task streaming = Task.Run(async () =>
		{
			try
			{
				await conversation.StreamResponseRich(handler, cancellationToken).ConfigureAwait(false);
				tokens.Writer.TryComplete();
			}
			catch (Exception exception)
			{
				tokens.Writer.TryComplete(TranslateException(exception, cancellationToken));
			}
		}, CancellationToken.None);

		ChannelReader<string> reader = tokens.Reader;
		while (true)
		{
			bool more;
			try
			{
				more = await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (ChannelClosedException closed) when (closed.InnerException is not null)
			{
				throw closed.InnerException;
			}
			if (!more) break;
			while (reader.TryRead(out string? token))
			{
				yield return new ChatResponseUpdate(ChatRole.Assistant, token);
			}
		}
		await streaming.ConfigureAwait(false);
		if (reader.Completion.Exception?.InnerException is { } failure) throw failure;

		StreamState state = new();
		foreach (TornadoFunctionCall call in calls) state.AddCall(call.ToolCall?.Id, call.Name, call.Arguments, call.ToolCall?.ThoughtSignature);
		state.SetUsage(usage);
		foreach (ChatResponseUpdate update in state.Finish()) yield return update;
	}

	private Exception TranslateException(Exception exception, CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested) return new OperationCanceledException(cancellationToken);
		if (_scope.TakeRecordedException() is { } recorded) return recorded;
		if (exception is OperationCanceledException) return new TimeoutException("模型服务响应超时");
		// LlmTornado 的消息可能带有地址、请求头与提示词正文，只保留异常类型。
		return new InvalidOperationException($"模型服务响应格式无法解析 ({exception.GetType().Name})");
	}

	private ChatRequest BuildRequest(List<AiChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
	{
		string model = string.IsNullOrWhiteSpace(options?.ModelId) ? _model : options.ModelId!;
		ChatRequest request = new()
		{
			Model = new ChatModel(model, _provider),
			Messages = ConvertMessages(messages),
			CancellationToken = cancellationToken,
			UseResponseEndpoint = _scope.Protocol == LlmProvider.OpenAiResponses,
		};
		if (options?.Temperature is { } temperature) request.Temperature = temperature;
		if (options?.TopP is { } topP) request.TopP = topP;
		if ((options?.MaxOutputTokens ?? _defaultMaxOutputTokens) is { } maxTokens) request.MaxTokens = maxTokens;
		ApplyReasoning(request, model);
		ApplyInterleavedReasoning(request);

		List<Tool> tools = options?.Tools?.OfType<AIFunctionDeclaration>()
			.Select(function => new Tool(new ToolFunction(function.Name, function.Description, function.JsonSchema)))
			.ToList() ?? [];
		if (tools.Count > 0)
		{
			request.Tools = tools;
			// 由 FunctionInvokingChatClient 负责执行工具，LlmTornado 不能自行调用。
			request.InvokeClrToolsAutomatically = false;
		}
		return request;
	}

	/// <summary>按用户档位与模型能力写入思考参数；Auto 时请求保持原样。</summary>
	private void ApplyReasoning(ChatRequest request, string model)
	{
		ReasoningRequest? plan = ReasoningPlanner.Plan(_scope.Protocol, model, _generation.Reasoning, _generation.Capabilities);
		if (plan is null) return;
		if (plan.Effort is { } effort) request.ReasoningEffort = MapEffort(effort);
		if (plan.BudgetTokens is { } budget)
		{
			request.ReasoningBudget = budget;
			if (_scope.Protocol == LlmProvider.Anthropic && budget > 0)
			{
				// Anthropic 的 max_tokens 包含思考预算，必须严格大于预算，否则直接 400。
				int required = budget + ReasoningPlanner.MinVisibleOutputTokens;
				if (_generation.Capabilities is {OutputLimit: > 0} capabilities) required = Math.Min(required, capabilities.OutputLimit);
				request.MaxTokens = Math.Max(request.MaxTokens ?? 0, required);
			}
		}
		if (plan.DisableThinking)
		{
			Chain(request, (json, _) => json["thinking"] = new Newtonsoft.Json.Linq.JObject {["type"] = "disabled"});
		}
	}

	/// <summary>
	/// 声明了 interleaved 思考的 OpenAI 兼容模型 (如 DeepSeek 思考模式) 在工具往返时要求回传
	/// reasoning_content；LlmTornado 不序列化这个字段，在 JSON 层按助手消息顺序补上。
	/// </summary>
	private void ApplyInterleavedReasoning(ChatRequest request)
	{
		if (_scope.Protocol != LlmProvider.OpenAi) return;
		if (_generation.Capabilities?.InterleavedField is not { Length: > 0 } field) return;
		List<string?> reasoning = request.Messages?
			.Where(message => message.Role == ChatMessageRoles.Assistant)
			.Select(message => message.ReasoningContent)
			.ToList() ?? [];
		if (!reasoning.Any(text => !string.IsNullOrEmpty(text))) return;
		Chain(request, (json, _) =>
		{
			if (json["messages"] is not Newtonsoft.Json.Linq.JArray items) return;
			List<Newtonsoft.Json.Linq.JObject> assistants = items.OfType<Newtonsoft.Json.Linq.JObject>()
				.Where(item => (string?)item["role"] == "assistant")
				.ToList();
			// 条数对不上说明序列化结构变了，宁可不补也不能把思考内容错配到别的消息上。
			if (assistants.Count != reasoning.Count) return;
			for (int index = 0; index < assistants.Count; index++)
			{
				if (!string.IsNullOrEmpty(reasoning[index])) assistants[index][field] = reasoning[index];
			}
		});
	}

	private static void Chain(ChatRequest request, Action<Newtonsoft.Json.Linq.JObject, ChatRequest> transform)
	{
		Action<Newtonsoft.Json.Linq.JObject, ChatRequest>? previous = request.OnSerialize;
		request.OnSerialize = (json, source) =>
		{
			previous?.Invoke(json, source);
			transform(json, source);
		};
	}

	private static ChatReasoningEfforts MapEffort(string effort) => effort switch
	{
		"none" => ChatReasoningEfforts.None,
		"minimal" => ChatReasoningEfforts.Minimal,
		"low" => ChatReasoningEfforts.Low,
		"medium" => ChatReasoningEfforts.Medium,
		"xhigh" => ChatReasoningEfforts.XHigh,
		"max" => ChatReasoningEfforts.Max,
		_ => ChatReasoningEfforts.High,
	};

	private List<TornadoChatMessage> ConvertMessages(IReadOnlyList<AiChatMessage> messages)
	{
		Dictionary<string, string> callNames = new(StringComparer.Ordinal);
		List<TornadoChatMessage> result = [];
		foreach (AiChatMessage message in messages)
		{
			if (message.Role == ChatRole.Tool)
			{
				foreach (FunctionResultContent toolResult in message.Contents.OfType<FunctionResultContent>())
				{
					result.Add(new TornadoChatMessage(ChatMessageRoles.Tool, SerializeResult(toolResult.Result))
					{
						ToolCallId = toolResult.CallId,
						// Gemini 的 functionResponse 需要函数名，从前面的调用里找回。
						Name = callNames.GetValueOrDefault(toolResult.CallId),
					});
				}
				continue;
			}

			ChatMessageRoles role = message.Role == ChatRole.System
				? ChatMessageRoles.System
				: message.Role == ChatRole.Assistant ? ChatMessageRoles.Assistant : ChatMessageRoles.User;
			TornadoChatMessage converted = new(role);
			List<ChatMessagePart> parts = [];
			bool hasImage = false;
			bool hasReasoning = false;
			foreach (AIContent content in message.Contents)
			{
				switch (content)
				{
					// Anthropic 开启思考后，带 tool_use 的助手消息必须原样带回签名过的思考块。
					case TextReasoningContent reasoning when role == ChatMessageRoles.Assistant
						&& _scope.Protocol == LlmProvider.Anthropic
						&& !string.IsNullOrEmpty(reasoning.ProtectedData):
						parts.Insert(parts.Count(part => part.Type == ChatMessageTypes.Reasoning), new ChatMessagePart(new ChatMessageReasoningData
						{
							// redacted_thinking 只有加密数据 (在签名里)，正文为空；IsRedacted 由 LlmTornado 据此推断。
							Content = reasoning.AdditionalProperties?.ContainsKey(RedactedReasoningKey) == true && reasoning.Text.Length == 0 ? null : reasoning.Text,
							Signature = reasoning.ProtectedData,
						}));
						hasReasoning = true;
						break;
					case TextReasoningContent {Text.Length: > 0} reasoning when role == ChatMessageRoles.Assistant
						&& _scope.Protocol == LlmProvider.OpenAi:
						converted.ReasoningContent = string.Concat(converted.ReasoningContent, reasoning.Text);
						break;
					case TextContent {Text.Length: > 0} text:
						parts.Add(new ChatMessagePart(text.Text));
						break;
					case DataContent data when data.HasTopLevelMediaType("image"):
						parts.Add(new ChatMessagePart(CreateImage(data)));
						hasImage = true;
						break;
				}
			}

			if (hasImage || hasReasoning) converted.Parts = parts;
			else converted.Content = string.Concat(parts.Select(part => part.Text));

			List<ToolCall> toolCalls = [];
			foreach (FunctionCallContent call in message.Contents.OfType<FunctionCallContent>())
			{
				callNames[call.CallId] = call.Name;
				toolCalls.Add(new ToolCall
				{
					Id = call.CallId,
					FunctionCall = new TornadoFunctionCall
					{
						Name = call.Name,
						Arguments = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions),
					},
					ThoughtSignature = call.AdditionalProperties?.TryGetValue(ThoughtSignatureKey, out object? signature) == true
						? signature as string
						: null,
				});
			}
			if (toolCalls.Count > 0)
			{
				converted.ToolCalls = toolCalls;
				if (string.IsNullOrEmpty(converted.Content) && converted.Parts is null) converted.Content = null;
			}
			result.Add(converted);
		}
		return result;
	}

	/// <summary>
	/// 图片编码按协议区分：Gemini 的 inlineData 只收裸 base64，其余协议接受 data URL
	/// (Anthropic 序列化时会自行剥掉 data 头并使用 MimeType)。
	/// </summary>
	private ChatImage CreateImage(DataContent data)
	{
		string mediaType = data.MediaType;
		string base64 = Convert.ToBase64String(data.Data.Span);
		return _scope.Protocol == LlmProvider.Google
			? new ChatImage(base64, mediaType)
			: new ChatImage($"data:{mediaType};base64,{base64}", mediaType);
	}

	private static string SerializeResult(object? result) => result switch
	{
		null => string.Empty,
		string text => text,
		JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText(),
		_ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions),
	};

	/// <summary>把 LlmTornado 的流式块折算为 MEAI 更新；工具调用与用量在流结束时一次性给出。</summary>
	private sealed class StreamState
	{
		private readonly Dictionary<string, PendingCall> _calls = new(StringComparer.Ordinal);
		private UsageDetails? _usage;
		private ChatFinishReason? _finishReason;
		private readonly System.Text.StringBuilder _reasoningText = new();
		private List<ChatMessageReasoningData>? _reasoningBlocks;

		public ChatResponseUpdate? Accept(ChatResult result)
		{
			if (result.Usage is { } usage) _usage = ToDetails(usage);
			ChatChoice? choice = result.Choices?.FirstOrDefault();
			if (choice is null) return null;
			if (MapFinishReason(choice.FinishReason) is { } finish) _finishReason = finish;

			// LlmTornado 在流末尾合成的聚合块只是重复已经流出的文本或思考内容。
			TornadoChatMessage? message = choice.Delta ?? choice.Message;
			if (IsSynthesized(result))
			{
				// 但 Anthropic 思考块的签名只出现在聚合块里，必须从这里取完整的思考块。
				CaptureReasoningBlocks(message);
				return null;
			}
			if (message is null) return null;
			CaptureReasoningDelta(message);
			if (message.ToolCalls is {Count: > 0} toolCalls)
			{
				foreach (ToolCall call in toolCalls)
				{
					if (call.FunctionCall is { } function)
						AddCall(call.Id ?? call.Index?.ToString(), function.Name, function.Arguments, call.ThoughtSignature);
				}
				return null;
			}

			string? text = message.Content;
			if (string.IsNullOrEmpty(text) && message.Parts is {Count: > 0} parts)
				text = string.Concat(parts.Where(part => part.Type == ChatMessageTypes.Text).Select(part => part.Text));
			return string.IsNullOrEmpty(text)
				? null
				: new ChatResponseUpdate(ChatRole.Assistant, text) {ResponseId = result.Id};
		}

		public void AddCall(string? id, string? name, string? arguments, string? thoughtSignature)
		{
			if (string.IsNullOrEmpty(name)) return;
			string callId = string.IsNullOrEmpty(id) ? $"call_{_calls.Count}" : id;
			// Anthropic 会在每个块结束时重复推送累积中的工具消息，按 ID 覆盖为最新状态。
			_calls[callId] = new PendingCall(callId, name, arguments, thoughtSignature);
		}

		public void SetUsage(UsageDetails? usage)
		{
			if (usage is not null) _usage = usage;
		}

		/// <summary>
		/// 从聚合块中取完整的思考块；只有带签名 (或已脱敏) 的块才需要原样回传。
		/// </summary>
		private void CaptureReasoningBlocks(TornadoChatMessage? message)
		{
			List<ChatMessageReasoningData> blocks = message?.Parts?
				.Where(part => part.Type == ChatMessageTypes.Reasoning && part.Reasoning is not null)
				.Select(part => part.Reasoning!)
				.ToList() ?? [];
			if (blocks.Any(block => !string.IsNullOrEmpty(block.Signature) || block.IsRedacted == true)) _reasoningBlocks = blocks;
		}

		private void CaptureReasoningDelta(TornadoChatMessage message)
		{
			if (!string.IsNullOrEmpty(message.ReasoningContent)) _reasoningText.Append(message.ReasoningContent);
			if (message.Parts is not {Count: > 0} parts) return;
			foreach (ChatMessagePart part in parts)
			{
				if (part.Type == ChatMessageTypes.Reasoning && part.Reasoning?.Content is {Length: > 0} content) _reasoningText.Append(content);
			}
		}

		public IEnumerable<ChatResponseUpdate> Finish()
		{
			List<AIContent> contents = [];
			if (_reasoningBlocks is {Count: > 0} blocks)
			{
				foreach (ChatMessageReasoningData block in blocks)
				{
					TextReasoningContent reasoning = new(block.Content ?? string.Empty) {ProtectedData = block.Signature};
					if (block.IsRedacted == true) reasoning.AdditionalProperties = new AdditionalPropertiesDictionary {[RedactedReasoningKey] = true};
					contents.Add(reasoning);
				}
			}
			else if (_reasoningText.Length > 0)
			{
				contents.Add(new TextReasoningContent(_reasoningText.ToString()));
			}
			foreach (PendingCall call in _calls.Values)
			{
				FunctionCallContent content = new(call.Id, call.Name, ParseArguments(call.Arguments));
				if (!string.IsNullOrEmpty(call.ThoughtSignature))
					content.AdditionalProperties = new AdditionalPropertiesDictionary {[ThoughtSignatureKey] = call.ThoughtSignature};
				contents.Add(content);
			}
			if (_usage is { } usage) contents.Add(new UsageContent(usage));
			ChatFinishReason? finish = _calls.Count > 0 ? ChatFinishReason.ToolCalls : _finishReason;
			if (contents.Count > 0 || finish is not null)
			{
				yield return new ChatResponseUpdate(ChatRole.Assistant, contents) {FinishReason = finish};
			}
		}

		/// <summary>
		/// 合成块由 LlmTornado 的内部标记 StreamInternalKind 区分 (未公开)，通过反射读取；
		/// 升级 LlmTornado 时由流式去重测试兜底。
		/// </summary>
		private static readonly System.Reflection.PropertyInfo? StreamInternalKindProperty = typeof(ChatResult).GetProperty(
			"StreamInternalKind",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

		private static bool IsSynthesized(ChatResult result) => StreamInternalKindProperty?.GetValue(result) is not null;

		private static UsageDetails ToDetails(ChatUsage usage) => new()
		{
			InputTokenCount = usage.PromptTokens,
			OutputTokenCount = usage.CompletionTokens,
			TotalTokenCount = usage.TotalTokens > 0 ? usage.TotalTokens : usage.PromptTokens + usage.CompletionTokens,
			CachedInputTokenCount = usage.CacheReadTokens ?? usage.PromptTokenDetails?.CachedTokens,
		};

		private static IDictionary<string, object?>? ParseArguments(string? arguments)
		{
			if (string.IsNullOrWhiteSpace(arguments)) return new Dictionary<string, object?>();
			try
			{
				Dictionary<string, JsonElement>? parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments);
				return parsed?.ToDictionary(pair => pair.Key, pair => (object?)pair.Value) ?? new Dictionary<string, object?>();
			}
			catch (JsonException)
			{
				// 交给 FunctionInvokingChatClient 以参数错误回报模型，而不是中断整个对话。
				return null;
			}
		}

		private static ChatFinishReason? MapFinishReason(ChatMessageFinishReasons? reason) => reason switch
		{
			ChatMessageFinishReasons.EndTurn or ChatMessageFinishReasons.StopSequence => ChatFinishReason.Stop,
			ChatMessageFinishReasons.Length => ChatFinishReason.Length,
			ChatMessageFinishReasons.ToolCalls => ChatFinishReason.ToolCalls,
			ChatMessageFinishReasons.ContentFilter => ChatFinishReason.ContentFilter,
			_ => null,
		};

		private sealed record PendingCall(string Id, string Name, string? Arguments, string? ThoughtSignature);
	}
}
