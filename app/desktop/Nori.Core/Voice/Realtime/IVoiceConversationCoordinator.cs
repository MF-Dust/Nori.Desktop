using Nori.Core.Agent;

namespace Nori.Core.Voice.Realtime;

/// <summary>
/// 表示当前已有未释放的 Agent 会话或语音轮次，新语音轮次严禁抢占。
/// </summary>
public sealed class VoiceConversationBusyException : InvalidOperationException
{
	/// <summary>创建会话占用异常。</summary>
	public VoiceConversationBusyException(string? activeSessionId = null)
		: base(string.IsNullOrWhiteSpace(activeSessionId)
			? "已有对话正在进行，请等待当前回复完成后再试"
			: $"已有对话正在进行（{activeSessionId}），请等待当前回复完成后再试")
	{
		ActiveSessionId = activeSessionId;
	}

	/// <summary>正在占用的活动会话标识（若有）。</summary>
	public string? ActiveSessionId { get; }
}

/// <summary>
/// 连续语音对话协调器：
/// 1. 统一单轮结束条件：Final STT → Agent 完成 → 可选 TTS 完成 → 恢复监听；
/// 2. 严禁抢占未释放的 Agent 会话，发现已有活动会话时立即拒绝并返回清晰中文提示。
/// </summary>
public interface IVoiceConversationCoordinator
{
	/// <summary>当前是否存在未释放的 Agent 会话或尚未完成的语音对话轮次。</summary>
	bool IsBusy { get; }

	/// <summary>
	/// 协调执行一轮已确认的语音输入：
	/// 先确认没有未释放的 Agent 会话，再执行 Agent 推理与可选 TTS 朗读；
	/// 只有 Agent 与可选 TTS 都真正完成后才返回，供连续模式安全恢复监听。
	/// </summary>
	Task ExecuteTurnAsync(
		RealtimeVoiceTurn turn,
		Func<RealtimeVoiceTurn, Task<string?>> runAgentAsync,
		Func<string, long, CancellationToken, Task>? speakAsync = null,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// 协调执行一轮已确认的语音输入，并将联动后的取消令牌传入工作回调：
	/// 先确认没有未释放的 Agent 会话，再执行 Agent 推理与可选 TTS 朗读；
	/// 只有 Agent 与可选 TTS 都真正完成后才返回，供连续模式安全恢复监听。
	/// </summary>
	Task ExecuteTurnAsync(
		RealtimeVoiceTurn turn,
		Func<RealtimeVoiceTurn, CancellationToken, Task<string?>> runAgentAsync,
		Func<string, long, CancellationToken, Task>? speakAsync = null,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// 默认连续语音对话协调器实现。
/// </summary>
public sealed class VoiceConversationCoordinator : IVoiceConversationCoordinator
{
	private readonly Func<bool> _isAgentBusy;
	private readonly Func<string?>? _activeSessionId;
	private int _inTurn;

	/// <summary>绑定核心层的 <see cref="AgentSessionCoordinator"/>。</summary>
	public VoiceConversationCoordinator(AgentSessionCoordinator agentCoordinator)
	{
		ArgumentNullException.ThrowIfNull(agentCoordinator);
		_isAgentBusy = () => agentCoordinator.ActiveSessionId is not null;
		_activeSessionId = () => agentCoordinator.ActiveSessionId;
	}

	/// <summary>绑定自定义的活动会话探测谓词。</summary>
	public VoiceConversationCoordinator(Func<bool> isAgentBusy, Func<string?>? activeSessionId = null)
	{
		_isAgentBusy = isAgentBusy ?? throw new ArgumentNullException(nameof(isAgentBusy));
		_activeSessionId = activeSessionId;
	}

	/// <inheritdoc />
	public bool IsBusy => Volatile.Read(ref _inTurn) != 0 || _isAgentBusy();

	/// <inheritdoc />
	public Task ExecuteTurnAsync(
		RealtimeVoiceTurn turn,
		Func<RealtimeVoiceTurn, Task<string?>> runAgentAsync,
		Func<string, long, CancellationToken, Task>? speakAsync = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(runAgentAsync);
		return ExecuteTurnAsync(turn, (activeTurn, _) => runAgentAsync(activeTurn), speakAsync, cancellationToken);
	}

	/// <inheritdoc />
	public async Task ExecuteTurnAsync(
		RealtimeVoiceTurn turn,
		Func<RealtimeVoiceTurn, CancellationToken, Task<string?>> runAgentAsync,
		Func<string, long, CancellationToken, Task>? speakAsync = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(turn);
		ArgumentNullException.ThrowIfNull(runAgentAsync);
		CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
			turn.CancellationToken, cancellationToken);
		if (linked.IsCancellationRequested)
		{
			CancellationToken cancelledToken = linked.Token;
			linked.Dispose();
			cancelledToken.ThrowIfCancellationRequested();
		}

		if (_isAgentBusy() || Interlocked.CompareExchange(ref _inTurn, 1, 0) != 0)
		{
			linked.Dispose();
			throw new VoiceConversationBusyException(_activeSessionId?.Invoke());
		}

		Task? activeWork = null;
		try
		{
			// 进入轮次锁后再检查一次，避免竞态窗口内已有手动对话抢先持有 Agent 闸门。
			if (_isAgentBusy())
				throw new VoiceConversationBusyException(_activeSessionId?.Invoke());

			Task<string?> agentTask = runAgentAsync(turn, linked.Token);
			activeWork = agentTask;
			string? reply = await agentTask.WaitAsync(linked.Token).ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(reply) && speakAsync is not null && !linked.Token.IsCancellationRequested)
			{
				Task speakTask = speakAsync(reply, turn.Generation, linked.Token);
				activeWork = speakTask;
				await speakTask.WaitAsync(linked.Token).ConfigureAwait(false);
			}
		}
		finally
		{
			if (activeWork is { IsCompleted: false })
			{
				// 取消等待时底层工作可能尚未退出；必须等其真正完成后再释放轮次闸门与联动令牌。
				_ = activeWork.ContinueWith(
					static (task, state) =>
					{
						_ = task.Exception;
						var (self, cts) = ((VoiceConversationCoordinator, CancellationTokenSource))state!;
						Volatile.Write(ref self._inTurn, 0);
						cts.Dispose();
					},
					(this, linked),
					CancellationToken.None,
					TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);
			}
			else
			{
				if (activeWork is { IsFaulted: true })
					_ = activeWork.Exception;
				Volatile.Write(ref _inTurn, 0);
				linked.Dispose();
			}
		}
	}
}
