using Nori.Core.Agent;

namespace Nori.Core.Chat.LuoLiCore;

/// <summary>把一轮对话交给 LuoLiCore，并把结果还原成宿主的协议消息。</summary>
public sealed class LuoLiCoreConversation(
	LuoLiCoreSettingsStore settingsStore,
	Func<LuoLiCoreSdkOptions, LuoLiCoreSdkClient> clientFactory)
{
	private readonly LuoLiCoreSettingsStore _settingsStore = settingsStore;
	private readonly Func<LuoLiCoreSdkOptions, LuoLiCoreSdkClient> _clientFactory = clientFactory;

	/// <summary>队列满时最多重试几次。再多只是让人干等，不如把失败说出来。</summary>
	private const int MaxQueueRetries = 2;

	/// <summary>当前配置是否应当接管对话。</summary>
	public bool IsActive => _settingsStore.Read().IsActive;

	/// <summary>
	/// 对端那边是否已经有一段会话在记着东西。
	///
	/// 供调用方区分「没接外部后端」与「接了但这次没去重置」—— 后者需要提示用户，前者不用。
	/// </summary>
	public bool HasSession
	{
		get
		{
			LuoLiCoreSettings settings = _settingsStore.Read();
			return settings.IsActive && settings.SessionId.Length > 0;
		}
	}

	/// <summary>
	/// 跑一轮。
	///
	/// 返回的协议消息**只带文本**：情绪、表情、动作三项留空，由宿主另行决定。合法的动作与
	/// 表情名随当前 Live2D 模型变化，只有宿主知道；而 LuoLiCore 的内核明确把用户正文里的
	/// 指令当作数据处理（提示注入防护），把可用动作列表塞进消息里既不会被采纳，也违背
	/// 那条防护的用意。
	/// </summary>
	public async Task<ProtocolMessage> RunAsync(
		string userText,
		Action<string>? onChunk,
		CancellationToken cancellationToken)
		=> await RunAsync(userText, onChunk, null, cancellationToken);

	/// <inheritdoc cref="RunAsync(string, Action{string}, CancellationToken)"/>
	/// <param name="onQueued">
	/// 轮次进了对端的排队闸时回调一次。排队可能长达数秒而流上没有任何保活帧，不把这件事
	/// 说出去的话，界面在这段时间里既没有文字也没有状态变化，看起来就是卡住了。
	/// </param>
	public async Task<ProtocolMessage> RunAsync(
		string userText,
		Action<string>? onChunk,
		Action? onQueued,
		CancellationToken cancellationToken)
		=> await RunAsync(userText, onChunk, onQueued, null, cancellationToken);

	/// <inheritdoc cref="RunAsync(string, Action{string}, Action, CancellationToken)"/>
	/// <param name="onTool">
	/// 对端开始执行一个工具时回调，参数是工具名。工具可能跑上几十秒，这段时间里流上没有文本 ——
	/// 不把它说出去，界面就只能显示一个不动的「正在想」。
	/// </param>
	public async Task<ProtocolMessage> RunAsync(
		string userText,
		Action<string>? onChunk,
		Action? onQueued,
		Action<string>? onTool,
		CancellationToken cancellationToken)
	{
		LuoLiCoreSettings settings = _settingsStore.Read();
		if (!settings.IsActive) throw new ChatException("LuoLiCore 未启用或配置不完整");

		LuoLiCoreSdkClient client = _clientFactory(settings.ToOptions());

		string sessionId = settings.SessionId;
		if (sessionId.Length == 0)
		{
			sessionId = await client.CreateSessionAsync("Nori Desktop", cancellationToken);
			// 立刻写回：会话建好却没落盘的话，下一轮又会建一个新的，等价于每轮失忆。
			_settingsStore.SaveSessionId(sessionId);
		}

		string text = await StreamWithQueueRetryAsync(client, sessionId, userText, onChunk, onQueued, onTool, cancellationToken);
		return new ProtocolMessage(text, null, null, null);
	}

	/// <summary>
	/// 本来源能调哪些工具。未启用时返回空列表，不去碰远端。
	/// </summary>
	public async Task<IReadOnlyList<LuoLiCoreTool>> ListToolsAsync(CancellationToken cancellationToken)
	{
		LuoLiCoreSettings settings = _settingsStore.Read();
		if (!settings.IsActive) return [];
		return await _clientFactory(settings.ToOptions()).ListToolsAsync(cancellationToken);
	}

	/// <summary>
	/// 清掉对端记着的这段对话。
	///
	/// 本地清空聊天记录时必须一起做：对端把上下文记在自己那边，只删本地表的话她下一句仍然
	/// 接得上前面聊过的内容，而界面上什么都没有了 —— 用户会以为清空没生效。
	///
	/// 还没建过会话（没有 id）就没有要清的东西，返回 false；未启用时同样不做任何事，
	/// 免得关掉开关之后清空记录还去碰远端。
	/// </summary>
	public async Task<bool> ResetAsync(CancellationToken cancellationToken)
	{
		LuoLiCoreSettings settings = _settingsStore.Read();
		if (!settings.IsActive || settings.SessionId.Length == 0)
		{
			// 够不到远端的时候，至少把本地记着的会话作废。
			//
			// 不这么做会留下一个很隐蔽的状态：用户聊过一阵、关掉 LuoLiCore、清空聊天记录，
			// 本地空了但会话 id 还在；等他再打开，那段「已经清掉」的上下文原样回来。
			//
			// 这条路径**不发任何请求** —— 关掉就该意味着不再产生外部调用。旧会话留在原服务端
			// 不动，下次启用时新建一个。
			ForgetSession();
			return false;
		}

		await _clientFactory(settings.ToOptions()).ResetSessionAsync(settings.SessionId, cancellationToken);
		return true;
	}

	/// <summary>
	/// 只作废本地记着的会话，不联网。
	///
	/// 给「不该去碰远端但必须忘掉这段对话」的调用方用：安全模式下的清空聊天记录走的就是这条。
	/// </summary>
	public void ForgetSession() => _settingsStore.ClearSession();

	/// <summary>
	/// 队列满时退避重试。
	///
	/// 队列满在流式端点上不是 HTTP 429 —— 响应头在配额判定通过、流开始建立时就锁定成了
	/// 200，之后只能以 SSE error 事件表达。所以这里只能读到终结事件才知道被拒，退避常量
    /// 与对端客户端一致。
	/// </summary>
	private static async Task<string> StreamWithQueueRetryAsync(
		LuoLiCoreSdkClient client,
		string sessionId,
		string userText,
		Action<string>? onChunk,
		Action? onQueued,
		Action<string>? onTool,
		CancellationToken cancellationToken)
	{
		for (int attempt = 0; ; attempt++)
		{
			(string? text, LuoLiCoreStreamEvent failure) = await StreamOnceAsync(client, sessionId, userText, onChunk, onQueued, onTool, cancellationToken);
			if (text is not null) return text;

			bool canRetry = failure.IsQueueFull && attempt < MaxQueueRetries;
			if (!canRetry)
			{
				string detail = string.IsNullOrWhiteSpace(failure.ErrorMessage)
					? failure.ErrorCode ?? "unknown"
					: $"{failure.ErrorCode}: {failure.ErrorMessage}";
				throw new ChatException($"LuoLiCore 这一轮没跑完, {detail}");
			}

			await Task.Delay(LuoLiCoreSdkProtocol.QueueFullBackoff, cancellationToken);
		}
	}

	/// <summary>跑一次流。成功返回文本，失败返回那条终结用的错误事件。</summary>
	private static async Task<(string? Text, LuoLiCoreStreamEvent Failure)> StreamOnceAsync(
		LuoLiCoreSdkClient client,
		string sessionId,
		string userText,
		Action<string>? onChunk,
		Action? onQueued,
		Action<string>? onTool,
		CancellationToken cancellationToken)
	{
		System.Text.StringBuilder buffer = new();

		await foreach (LuoLiCoreStreamEvent item in client.StreamAsync(sessionId, userText, cancellationToken))
		{
			switch (item.Kind)
			{
				case LuoLiCoreStreamEventKind.Delta:
					buffer.Append(item.Text);
					onChunk?.Invoke(item.Text);
					break;

				case LuoLiCoreStreamEventKind.Done:
					// done 带的是完整文本。以它为准而不是拼接增量：两者理论上一致，
					// 但只有它是对端认定的最终结果。
					return (item.Text.Length > 0 ? item.Text : buffer.ToString(), default);

				case LuoLiCoreStreamEventKind.Error:
					return (null, item);

				case LuoLiCoreStreamEventKind.Queued:
					onQueued?.Invoke();
					break;

				case LuoLiCoreStreamEventKind.Tool:
					onTool?.Invoke(item.Text);
					break;

				default:
					break;
			}
		}

		// 读取层保证流必定以 done 或 error 结束，走到这里说明那条不变量被破坏了。
		throw new ChatException("LuoLiCore 流没有终结事件");
	}
}
