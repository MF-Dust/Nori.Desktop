using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Emotion;
using Nori.Core.Memory;
using Nori.Core.Skills;
using Nori.Core.Tools;

namespace Nori.Core.Agent;

/// <summary>
/// LLM 用量与缓存命中指标
/// </summary>
public sealed record AgentUsage(
	int PromptTokens,
	int CompletionTokens,
	int TotalTokens,
	int CachedTokens,
	double CacheHitRate,
	long DurationMs,
	string? Model);

/// <summary>
/// Agent 引擎回调集合
/// </summary>
public sealed class AgentCallbacks
{
	/// <summary>运行状态变化</summary>
	public Action<AgentRunState>? OnState { get; init; }

	/// <summary>流式文本增量 (已解析协议后的可见文本)</summary>
	public Action<string>? OnTextChunk { get; init; }

	/// <summary>完整协议解析发现增量投影不一致时的替换文本。</summary>
	public Action<string>? OnTextCorrection { get; init; }

	/// <summary>工具开始执行</summary>
	public Action<string, JsonNode?>? OnToolExecuting { get; init; }

	/// <summary>工具执行完成</summary>
	public Action<string, object?, string?>? OnToolExecuted { get; init; }

	/// <summary>LLM 用量指标</summary>
	public Action<AgentUsage>? OnUsage { get; init; }

	/// <summary>逐调用工具授权; confirm/dangerous 工具执行前必须经用户批准</summary>
	public Func<ToolApprovalRequest, Task<bool>>? RequestApproval { get; init; }

	/// <summary>最终回复产出 (多轮工具调用后)</summary>
	public Action<ProtocolMessage>? OnComplete { get; init; }
}

/// <summary>
/// Agent 引擎
///
/// 在后端执行完整对话回路: 读取配置与秘密 → 组装人格/记忆/技能/情绪/工具提示词 →
/// 流式 LLM 调用 → 协议解析 → 多轮 Tool Calling → 副本分发与最终落库。
/// 对应前端 services/agent/engine.ts 的职责迁移。
/// </summary>
public sealed class AgentEngine
{
	/// <summary>单轮 LLM 调用超时 (秒), 与 ChatService 上限一致</summary>
	public const int CallTimeoutSeconds = ChatService.TimeoutSeconds;

	private const int MaxContextRounds = 12;
	private const int DefaultContextTokens = 12_000;
	private const int DefaultReservedOutputTokens = 2_000;
	private readonly int _maxToolIterations;
	private readonly AgentSessionCoordinator _sessionCoordinator;
	private readonly AgentTraceSink _trace;
	private readonly Func<LlmProvider, HttpClient, ILlmAdapter> _adapterFactory;

	/// <summary>
	/// 把对话交给 LuoLiCore 的那条路。为 null 或未启用时本类行为不变。
	///
	/// 可选而不是必填：这条路是一个可选后端，缺省时桌宠仍然完整可用，
	/// 不该让一个没配它的实例连构造都过不去。
	/// </summary>
	private readonly Chat.LuoLiCore.LuoLiCoreConversation? _luoLiCore;

	/// <summary>
	/// 回复文本 → 表情动作。只在走 LuoLiCore 那条路时用到：本机那条由模型在协议里直接给。
	/// 为 null 时不挑，退化成只有物理摆动与口型。
	/// </summary>
	private readonly ReplyReactionService? _replyReaction;

	private readonly HttpClient _http;
	private readonly ConfigStore _config;
	private readonly ChatService _chat;
	private readonly ToolRegistry _tools;
	private readonly SkillService _skills;
	private readonly EmotionManager _emotion;
	private readonly MemoryService _memory;
	private readonly IPetActions? _pet;
	private readonly Func<IReadOnlyList<string>> _motionNames;
	private readonly Func<IReadOnlyList<string>> _expressionNames;

	private static readonly JsonSerializerOptions JsonOptions = new() {PropertyNamingPolicy = JsonNamingPolicy.CamelCase};

	public AgentEngine(
		HttpClient http,
		ConfigStore config,
		ChatService chat,
		ToolRegistry tools,
		SkillService skills,
		EmotionManager emotion,
		MemoryService memory,
		IPetActions? pet,
		Func<IReadOnlyList<string>> motionNames,
		Func<IReadOnlyList<string>> expressionNames,
		int maxToolIterations = 5,
		AgentSessionCoordinator? sessionCoordinator = null,
		AgentTraceSink? trace = null,
		Func<LlmProvider, HttpClient, ILlmAdapter>? adapterFactory = null,
		Chat.LuoLiCore.LuoLiCoreConversation? luoLiCore = null,
		ReplyReactionService? replyReaction = null)
	{
		_http = http;
		_config = config;
		_chat = chat;
		_tools = tools;
		_skills = skills;
		_emotion = emotion;
		_memory = memory;
		_pet = pet;
		_motionNames = motionNames;
		_expressionNames = expressionNames;
		if (maxToolIterations <= 0) throw new ArgumentOutOfRangeException(nameof(maxToolIterations), "工具轮数上限必须为正数");
		_maxToolIterations = maxToolIterations;
		_sessionCoordinator = sessionCoordinator ?? new AgentSessionCoordinator();
		_trace = trace ?? AgentTraceSink.Noop;
		_adapterFactory = adapterFactory ?? LlmClient.CreateAdapter;
		_luoLiCore = luoLiCore;
		_replyReaction = replyReaction;
	}

	/// <summary>
	/// 执行一次 Agent 对话回路
	///
	/// 返回最终文本消息; 会话取消时抛出 OperationCanceledException。
	/// </summary>
	public async Task<ProtocolMessage> RunAsync(string userText, string sessionId, AgentCallbacks callbacks, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(userText)) throw new InvalidOperationException("消息内容不能为空");
		ArgumentNullException.ThrowIfNull(callbacks);
		using AgentSessionLease session = _sessionCoordinator.Start(sessionId, cancellationToken);
		CancellationToken runToken = session.CancellationToken;
		Stopwatch runClock = Stopwatch.StartNew();
		WriteTrace(sessionId, "run", 0, null, null, "started");

		void SetState(AgentRunState state) => callbacks.OnState?.Invoke(state);

		SetState(AgentRunState.Thinking);

		// 0. 对话是否交给 LuoLiCore。
		//
		// 判定放在读取 LLM 配置之前：那一段要求 BaseUrl / ApiKey / Model 三项齐全，
		// 而只配了 LuoLiCore 的实例本来就没有这三项，放在后面会先被那条检查挡掉。
		if (_luoLiCore is { IsActive: true })
		{
			return await RunViaLuoLiCoreAsync(userText, sessionId, callbacks, runToken, runClock, cancellationToken);
		}

		// 1. 读取 AI 与用户自定义人设配置 (秘密只在后端流转)
		Stopwatch configClock = Stopwatch.StartNew();
		AiChatSettings chatSettings = new AiSettingsStore(_config).Read().Chat;
		string provider = chatSettings.Provider.AsString();
		string baseUrl = chatSettings.BaseUrl;
		string apiKey = chatSettings.ApiKey;
		string model = chatSettings.Model;
		string userPersona = chatSettings.Persona;
		if (baseUrl.Length == 0 || apiKey.Length == 0 || model.Length == 0)
		{
			WriteTrace(sessionId, "config", configClock.ElapsedMilliseconds, null, null, "error", "invalid_config");
			throw new InvalidOperationException("尚未配置完整的 LLM 参数 (API Base, API Key 或 Model 缺失)");
		}
		LlmProvider providerKind = LlmProviderExtensions.ParseProvider(provider);
		WriteTrace(sessionId, "config", configClock.ElapsedMilliseconds, null, null, "completed");

		// 2. 组装静态上下文: 最近对话 / 分层记忆 / 情绪 / 动作 / 表情 / 技能 / 工具清单
		Stopwatch contextClock = Stopwatch.StartNew();
		IReadOnlyList<(string Role, string Content)> recent = AgentHistory.NormalizeRecent(_chat.GetHistory(MaxContextRounds * 2, 0));
		MemoryContext memoryContext;
		try
		{
			memoryContext = await _memory.BuildContextAsync(userText, recent, runToken);
		}
		catch (Exception exception)
		{
			WriteTrace(sessionId, "context", contextClock.ElapsedMilliseconds, null, null, "error", FailureCategory(exception));
			throw;
		}
		string currentEmotion = _emotion.CurrentType;
		IReadOnlyList<string> motions = _motionNames();
		IReadOnlyList<string> expressions = _expressionNames();
		HashSet<string> availableToolNames = _tools.ListEnabled().Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
		string skillsPrompt = _skills.BuildSkillsPrompt(availableToolNames);

		PromptBuildOptions promptOptions = new()
		{
			UserPersona = userPersona,
			Emotion = currentEmotion,
			PersonalMemories = memoryContext.Personal
				.Select(item => item.PersonaSummary ?? item.CanonicalSummary ?? item.Content)
				.Concat(memoryContext.Atoms.Select(atom => atom.Content))
				.Distinct(StringComparer.Ordinal)
				.Take(6)
				.ToList(),
			RelatedKnowledge = memoryContext.Knowledge
				.Where(item => item.Awareness != KnowledgeAwareness.Recovered)
				.Select(item => item.Content).ToList(),
			RecoveredKnowledge = memoryContext.Knowledge
				.Where(item => item.Awareness == KnowledgeAwareness.Recovered)
				.Select(item => item.Content).ToList(),
			MemoryEchoes = memoryContext.Echoes.Select(item => item.Content).ToList(),
			AvailableMotions = motions,
			AvailableExpressions = expressions,
			SkillsPrompt = skillsPrompt,
			ToolsJson = _tools.BuildToolsPrompt(),
		};
		ContextBudgetOptions budgetOptions = new()
		{
			MaxInputTokens = _config.GetClampedInt("agent_context_tokens", DefaultContextTokens, 512, 128_000),
			ReservedOutputTokens = _config.GetClampedInt("agent_reserved_output_tokens", DefaultReservedOutputTokens, 128, 64_000),
		};
		ContextBudgetResult initialBudget = ContextBudgeter.Build(
			promptOptions,
			recent.Append(("user", userText)).ToList(),
			userText,
			budgetOptions);
		string systemPrompt = initialBudget.SystemPrompt;
		WriteTrace(sessionId, "context", contextClock.ElapsedMilliseconds, null, null, "completed");

		// 3. 准备工作历史: 最近 N 条 + 当前输入 (滑动窗口截断)
		List<(string Role, string Content)> working = initialBudget.Messages
			.Select(message => (message.Role, message.Content)).ToList();
		ToolExecutionTracker executionTracker = new();
		ProtocolMessage finalMessage = new("", null, null, null);
		int currentIteration = -1;
		try
		{
			ILlmAdapter adapter = _adapterFactory(providerKind, _http);
			async Task<ToolResult> ExecuteToolAsync(string name, JsonNode? arguments, CancellationToken token, string? callId = null)
			{
				runToken.ThrowIfCancellationRequested();
				string executionKey = ToolExecutionTracker.Key(callId, name, arguments);
				if (executionTracker.TryGetCompleted(executionKey, out ToolResult? previous)) return previous;
				if (!executionTracker.TryStart(executionKey))
				{
					WriteTrace(sessionId, "tool", 0, currentIteration, name, "blocked", "duplicate");
					return new ToolResult(null, $"工具调用 {name} 已执行过，已阻止重复副作用");
				}

				Stopwatch toolClock = Stopwatch.StartNew();
				WriteTrace(sessionId, "tool", 0, currentIteration, name, "started");
				try
				{
					callbacks.OnToolExecuting?.Invoke(name, arguments);
					ToolResult result = await _tools.ExecuteAsync(name, arguments, new ToolContext
					{
						SessionId = sessionId,
						CancellationToken = token,
						Approve = callbacks.RequestApproval is { } approve
							? request => approve(request)
							: null,
					});
					executionTracker.Complete(executionKey, result);
					callbacks.OnToolExecuted?.Invoke(name, result.Result, result.Error);
					WriteTrace(sessionId, "tool", toolClock.ElapsedMilliseconds, currentIteration, name,
						result.Error is null ? "completed" : "error",
						result.Error is null ? null : "tool_error");
					return result;
				}
				catch (Exception exception)
				{
					WriteTrace(sessionId, "tool", toolClock.ElapsedMilliseconds, currentIteration, name, "error", FailureCategory(exception));
					throw;
				}
			}

			for (int iteration = 0; iteration < _maxToolIterations; iteration++)
			{
				currentIteration = iteration;
				runToken.ThrowIfCancellationRequested();
				ContextBudgetResult roundBudget = ContextBudgeter.Build(promptOptions, working, userText, budgetOptions);
				IReadOnlyList<ChatMessageInput> requestMessages = roundBudget.Messages;
				StreamingMessageTextProjector projector = new();
				TextChunkCoalescer coalescer = new();
				StringBuilder rawResponseText = new();
				bool emittedText = false;

				void EmitText(string text)
				{
					if (text.Length == 0) return;
					emittedText = true;
					callbacks.OnTextChunk?.Invoke(text);
				}

				using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(runToken);
				timeout.CancelAfter(TimeSpan.FromSeconds(CallTimeoutSeconds));

				SetState(AgentRunState.Streaming);
				Action<string> onChunk = chunk =>
				{
					rawResponseText.Append(chunk);
					StreamingTextProjection projection = projector.Push(chunk);
					if (projection.IsCorrection) callbacks.OnTextCorrection?.Invoke(projection.FullText);
					if (coalescer.Push(projection.Delta) is {Length: > 0} batch) EmitText(batch);
				};
				AgentTraceUsage? traceUsage = null;
				Action<LlmUsageInfo> onUsage = usage =>
				{
					traceUsage = new AgentTraceUsage(
						usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens, usage.CachedTokens,
						usage.CacheHitRate, usage.Model);
					callbacks.OnUsage?.Invoke(new AgentUsage(
						usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens, usage.CachedTokens,
						usage.CacheHitRate, usage.DurationMs, usage.Model));
				};
				IReadOnlyList<RegisteredTool> enabledTools = _tools.ListEnabled();
				Stopwatch llmClock = Stopwatch.StartNew();
				string raw;
				try
				{
					if (adapter is IToolCallingLlmAdapter toolAdapter)
					{
						try
						{
							raw = await toolAdapter.StreamWithToolsAsync(
								baseUrl.TrimEnd('/'), apiKey, model, systemPrompt,
								requestMessages,
								enabledTools,
								(name, arguments) => ExecuteToolAsync(name, arguments, timeout.Token),
								onChunk, onUsage, timeout.Token);
						}
						catch (ToolsUnsupportedException) when (!executionTracker.HasStarted && !projector.HasProjectedText && !emittedText)
						{
							// 只有明确的 typed capability error 才允许一次 portable fallback。
							rawResponseText.Clear();
							projector.Reset();
							coalescer.Reset();
							raw = await adapter.StreamAsync(
								baseUrl.TrimEnd('/'), apiKey, model, systemPrompt,
								requestMessages,
								onChunk, onUsage, timeout.Token);
						}
					}
					else
					{
						raw = await adapter.StreamAsync(
							baseUrl.TrimEnd('/'), apiKey, model, systemPrompt,
							requestMessages,
							onChunk, onUsage, timeout.Token);
					}
				}
				catch (Exception exception)
				{
					WriteTrace(sessionId, "llm", llmClock.ElapsedMilliseconds, iteration, null, "error", FailureCategory(exception), traceUsage);
					throw;
				}
				WriteTrace(sessionId, "llm", llmClock.ElapsedMilliseconds, iteration, null, "completed", null, traceUsage);

				if (await coalescer.FlushAsync(timeout.Token) is {Length: > 0} finalBatch) EmitText(finalBatch);
				runToken.ThrowIfCancellationRequested();
				SetState(AgentRunState.Streaming);

				// 剥离动作标记并触发伴侣播放, 再做完整协议解析
				(string stripped, IReadOnlyList<string> markerMotions) = MotionMarkers.Extract(raw.Length > 0 ? raw : rawResponseText.ToString());
				foreach (string motion in markerMotions)
				{
					try
					{
						_pet?.PlayMotionByName(motion);
					}
					catch
					{
						/* 伴侣未加载时忽略 */
					}
				}

				IReadOnlyList<AgentProtocolItem> items = StreamingJsonParser.ParseComplete(stripped);
				StreamingTextProjection correction = projector.Complete(items);
				if (correction.IsCorrection) callbacks.OnTextCorrection?.Invoke(correction.FullText);
				else if (await coalescer.FlushAsync(timeout.Token) is {Length: > 0} correctionBatch) EmitText(correctionBatch);
				bool hasToolCall = false;

				foreach (AgentProtocolItem item in items)
				{
					switch (item)
					{
						// A. 普通消息 (工具调用之后的消息需要等结果反馈，跳过提前定稿)
						case ProtocolMessage message when !hasToolCall:
							finalMessage = message;
							DispatchEffects(message);
							break;

						// B. 工具调用 (同一轮可执行多个工具，全部并入下一轮推理)
						case ProtocolToolCall call:
						{
							hasToolCall = true;
							SetState(AgentRunState.ToolExecuting);
							ToolResult result = await ExecuteToolAsync(call.Name, call.Arguments, timeout.Token, call.Id);

							working.Add(("assistant", SerializeToolCall(call)));
							working.Add(("user",
								$"【系统工具执行反馈 - {call.Name}】:\n" + JsonSerializer.Serialize(new
								{
									id = call.Id,
									name = call.Name,
									result = result.Result,
									error = result.Error,
								}, JsonOptions)));

							SetState(AgentRunState.Thinking);
							break;
						}
					}
				}

				// 若本轮没有触发新的工具调用，说明已产出最终回复，跳出循环
				if (!hasToolCall) break;
				if (iteration == _maxToolIterations - 1)
				{
					throw new AgentToolRoundsExceededException(_maxToolIterations);
				}
			}

			if (finalMessage.Text.Length == 0)
			{
				throw new InvalidOperationException("Agent 未产出最终回复");
			}
			SetState(AgentRunState.Idle);

			// 落库: 只保存用户可见的最终一轮对话 (纯文本, 不再存协议 JSON)
			_chat.SaveMessage("user", userText);
			_chat.SaveMessage("assistant", finalMessage.Text);
			callbacks.OnComplete?.Invoke(finalMessage);
			WriteTrace(sessionId, "run", runClock.ElapsedMilliseconds, null, null, "completed");
			return finalMessage;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// 非用户取消的超时: 转成可读错误而不是当作正常中止
			WriteTrace(sessionId, "run", runClock.ElapsedMilliseconds, null, null, "error", "timeout");
			throw new ChatException($"回复超时 ({CallTimeoutSeconds}s), 请稍后重试");
		}
		catch (OperationCanceledException)
		{
			SetState(AgentRunState.Idle);
			WriteTrace(sessionId, "run", runClock.ElapsedMilliseconds, null, null, "cancelled", "cancelled");
			throw;
		}
		catch (Exception exception)
		{
			SetState(AgentRunState.Error);
			WriteTrace(sessionId, "run", runClock.ElapsedMilliseconds, null, null, "error", FailureCategory(exception));
			throw;
		}
	}

	/// <summary>
	/// 把这一轮整个交给 LuoLiCore。
	///
	/// 不跑本机的工具循环 —— 对端一次「发消息」等于它自己跑完一整轮（含它那侧的工具、
	/// 记忆与审批）。两套 agent 同时持有工具循环的控制权是两套记忆、两套工具注册表、
	/// 两条审批路径打架的起点。
	///
	/// 收尾与本机那条保持一致：同样落库、同样回调 OnComplete、同样记 trace。区别只有
	/// 「谁生成了这段文本」，对上层与界面是透明的。
	/// </summary>
	private async Task<ProtocolMessage> RunViaLuoLiCoreAsync(
		string userText,
		string sessionId,
		AgentCallbacks callbacks,
		CancellationToken runToken,
		Stopwatch runClock,
		CancellationToken cancellationToken)
	{
		void SetState(AgentRunState state) => callbacks.OnState?.Invoke(state);

		try
		{
			// 状态跟本机那条一致：入口已经是 Thinking，见到第一个增量才算开始说话。
			//
			// 不在这里直接置 Streaming：对端的排队闸可能让轮次等上数秒，而那条流在轮次执行
			// 期间没有任何保活帧 —— 一上来就说「正在说」，界面会长时间停在一个不动的说话态。
			bool streaming = false;
			void EnterStreaming()
			{
				if (streaming) return;
				streaming = true;
				SetState(AgentRunState.Streaming);
			}

			ProtocolMessage message = await _luoLiCore!.RunAsync(
				userText,
				text =>
				{
					EnterStreaming();
					callbacks.OnTextChunk?.Invoke(text);
				},
				// 排了队就明确退回 Thinking，让界面知道这一轮还没轮到。
				() => SetState(AgentRunState.Thinking),
				// 对端开始跑一个工具。走本机那条路一样的两个回调，界面不必分辨这一轮是谁在执行。
				//
				// 只有开始没有结束：对端那条流上没有「工具跑完了」这个事件。补一个假的结束回调
				// 会让界面显示一个它并不知道的事实；下一个增量到来时状态自然回到 Streaming，
				// 而一直没有增量的话，状态停在 ToolExecuting 恰好是真的。
				name =>
				{
					streaming = false;
					SetState(AgentRunState.ToolExecuting);
					callbacks.OnToolExecuting?.Invoke(name, null);
				},
				runToken);

			if (message.Text.Length == 0) throw new InvalidOperationException("LuoLiCore 未产出最终回复");

			// 远端只给文本，表情动作在本地挑：合法名随当前 Live2D 模型变化，只有宿主知道。
			// 挑不出来（没配本机模型、超时、返回的不是 JSON）就保持空值 —— 退化成没有表情，
			// 不影响这一轮对话。
			message = await AttachReactionAsync(message, runToken);

			SetState(AgentRunState.Idle);
			_chat.SaveMessage("user", userText);
			_chat.SaveMessage("assistant", message.Text);
			DispatchEffects(message);
			callbacks.OnComplete?.Invoke(message);
			WriteTrace(sessionId, "run", runClock.ElapsedMilliseconds, null, null, "completed");
			return message;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			WriteTrace(sessionId, "run", runClock.ElapsedMilliseconds, null, null, "error", "timeout");
			throw new ChatException("LuoLiCore 这一轮超时了, 请稍后重试");
		}
		catch (OperationCanceledException)
		{
			SetState(AgentRunState.Idle);
			WriteTrace(sessionId, "run", runClock.ElapsedMilliseconds, null, null, "cancelled", "cancelled");
			throw;
		}
		catch (Exception exception)
		{
			SetState(AgentRunState.Error);
			WriteTrace(sessionId, "run", runClock.ElapsedMilliseconds, null, null, "error", FailureCategory(exception));
			throw;
		}
	}

	/// <summary>
	/// 对端这一侧能调哪些工具。没接这条路或未启用时返回空列表。
	/// </summary>
	public Task<IReadOnlyList<Chat.LuoLiCore.LuoLiCoreTool>> ListRemoteToolsAsync(CancellationToken cancellationToken) =>
		_luoLiCore is null
			? Task.FromResult<IReadOnlyList<Chat.LuoLiCore.LuoLiCoreTool>>([])
			: _luoLiCore.ListToolsAsync(cancellationToken);

	/// <summary>
	/// 清掉对端记着的这段对话。清空本地聊天记录时一起调用。
	/// </summary>
	/// <returns>远端确实被重置了返回 true；没启用或还没建过会话返回 false。</returns>
	public Task<bool> ResetRemoteContextAsync(CancellationToken cancellationToken) =>
		_luoLiCore is null ? Task.FromResult(false) : _luoLiCore.ResetAsync(cancellationToken);

	/// <summary>
	/// 给一条只有文本的回复补上表情与动作。
	///
	/// 挑选本身绝不允许影响这一轮：服务内部已经把超时与故障吞成空反应，这里再兜一层，
	/// 是因为「挑表情失败」和「她没话说」在用户那边看起来一模一样，而前者根本不该让
	/// 整轮失败。
	/// </summary>
	private async Task<ProtocolMessage> AttachReactionAsync(ProtocolMessage message, CancellationToken cancellationToken)
	{
		if (_replyReaction is null) return message;

		try
		{
			PetInteractionReaction reaction = await _replyReaction.ReactAsync(
				new ReplyReactionRequest
				{
					ReplyText = message.Text,
					CurrentEmotion = _emotion?.CurrentType,
					AvailableMotions = _motionNames(),
					AvailableExpressions = _expressionNames(),
				},
				cancellationToken);

			return message with
			{
				Emotion = reaction.Emotion,
				Expression = reaction.Expression,
				Action = reaction.Motion,
			};
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			return message;
		}
	}

	/// <summary>分发消息附加的情绪、表情、动作副作用</summary>
	private void DispatchEffects(ProtocolMessage message)
	{
		if (!string.IsNullOrEmpty(message.Emotion))
		{
			try
			{
				_emotion.SetEmotion(message.Emotion);
			}
			catch
			{
				/* 忽略未知情绪 */
			}
		}
		if (_pet is null) return;
		if (!string.IsNullOrEmpty(message.Expression))
		{
			try
			{
				_pet.PlayExpression(message.Expression);
			}
			catch
			{
				/* 表情未匹配时忽略 */
			}
		}
		if (!string.IsNullOrEmpty(message.Action))
		{
			try
			{
				_pet.PlayMotionByName(message.Action);
			}
			catch
			{
				/* 动作未匹配时忽略 */
			}
		}
	}

	/// <summary>序列化 tool_call 为 assistant 反馈上下文</summary>
	private static string SerializeToolCall(ProtocolToolCall call) =>
		JsonSerializer.Serialize(new
		{
			type = "tool_call",
			id = call.Id,
			name = call.Name,
			arguments = call.Arguments,
		}, JsonOptions);

	private void WriteTrace(
		string sessionId,
		string phase,
		long durationMs,
		int? iteration,
		string? toolName,
		string status,
		string? failureCategory = null,
		AgentTraceUsage? usage = null)
	{
		try
		{
			_trace.Record(new AgentTraceRecord(
				sessionId, phase, durationMs, iteration, toolName, status, failureCategory, usage));
		}
		catch
		{
			// Trace 不得影响 Agent 的业务输出、工具副作用或持久化。
		}
	}

	private static string FailureCategory(Exception exception)
	{
		return exception switch
		{
			OperationCanceledException => "cancelled",
			ToolsUnsupportedException => "tools_unsupported",
			AgentToolRoundsExceededException => "tool_rounds_exceeded",
			ChatException => "chat",
			_ => "exception",
		};
	}
}

/// <summary>工具调用轮数耗尽且没有最终回复时的明确错误。</summary>
public sealed class AgentToolRoundsExceededException(int maxRounds)
	: InvalidOperationException($"工具调用轮数已达到上限 ({maxRounds})，未产出最终回复");

/// <summary>
/// 聊天历史规范化
///
/// 兼容读取旧版前端落库的协议 JSON (assistant 行内嵌 ```json 包裹的协议对象),
/// 过滤历史工具反馈行 —— 前端不再解析历史业务内容。
/// </summary>
public static class AgentHistory
{
	/// <summary>工具反馈行前缀</summary>
	private const string FeedbackPrefix = "【系统工具执行反馈 -";

	/// <summary>
	/// 把存储行规整为 (role, 纯文本) 序列:
	/// assistant 行若为旧版协议 JSON 则提取全部 message 文本。
	/// </summary>
	public static IReadOnlyList<(string Role, string Content)> NormalizeRecent(IReadOnlyList<ChatMessage> rows)
	{
		List<(string, string)> result = [];
		foreach (ChatMessage row in rows)
		{
			if (row.Role == "assistant")
			{
				string text = ExtractDisplayText(row.Content);
				if (text.Length > 0) result.Add(("assistant", text));
				continue;
			}
			if (row.Role == "user" && row.Content.StartsWith(FeedbackPrefix, StringComparison.Ordinal))
			{
				continue;
			}
			result.Add((row.Role, row.Content));
		}
		return result;
	}

	/// <summary>提取一条存储内容的展示文本</summary>
	public static string ExtractDisplayText(string content)
	{
		string trimmed = content.TrimStart();
		if (!trimmed.StartsWith('{') && !trimmed.StartsWith('`'))
		{
			// 已经是纯文本 (新版引擎落库格式)
			return content;
		}
		try
		{
			var items = StreamingJsonParser.ParseComplete(content);
			var texts = items.OfType<ProtocolMessage>()
				.Where(message => message.Text.Length > 0)
				.Select(message => message.Text);
			string joined = string.Join("\n", texts);
			return joined.Length > 0 ? joined : content;
		}
		catch
		{
			return content;
		}
	}
}
