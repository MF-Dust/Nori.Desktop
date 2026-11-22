namespace Nori.Core.Voice.Realtime;

/// <summary>交给对话处理的逻辑轮次；阶段记录最终文本交付时的状态。</summary>
public sealed class RealtimeVoiceTurn
{
	private enum Delivery
	{
		Pending,
		Delivered,
		PublishedBySession,
		SubmittedToAgent,
	}

	private int _delivery;

	/// <summary>创建一轮结果；整段识别没有服务端音频项标识。</summary>
	public RealtimeVoiceTurn(
		long generation,
		string text,
		CancellationToken cancellationToken,
		string? providerItemId = null,
		VoiceTurnOrigin origin = VoiceTurnOrigin.Voice,
		RealtimeVoicePhase state = RealtimeVoicePhase.Thinking)
	{
		Generation = generation;
		Text = text;
		CancellationToken = cancellationToken;
		ProviderItemId = providerItemId;
		Origin = origin;
		State = state;
	}

	public long Generation { get; }
	public string TurnId { get; } = Guid.NewGuid().ToString("N");
	public string? ProviderItemId { get; private set; }
	public CancellationToken CancellationToken { get; }
	public VoiceTurnOrigin Origin { get; }
	public RealtimeVoicePhase State { get; private set; }
	public string Text { get; private set; }
	public bool FinalDelivered => Volatile.Read(ref _delivery) != (int)Delivery.Pending;

	/// <summary>标记最终文本已交付；同一轮只能成功一次。</summary>
	public bool TryMarkFinalDelivered() => TryTransition(Delivery.Pending, Delivery.Delivered);

	/// <summary>会话交付与 Agent 接收是两个边界；同一份会话结果只允许 Agent 接收一次。</summary>
	public bool TrySubmitToAgent() =>
		TryTransition(Delivery.PublishedBySession, Delivery.SubmittedToAgent)
		|| TryTransition(Delivery.Pending, Delivery.SubmittedToAgent);

	internal bool TryPublishFinal(string text, string? providerItemId = null)
	{
		// 会话在锁内发布，成功之前该轮次不对外暴露。
		Text = text;
		ProviderItemId = providerItemId;
		State = RealtimeVoicePhase.Thinking;
		return TryTransition(Delivery.Pending, Delivery.PublishedBySession);
	}

	private bool TryTransition(Delivery expected, Delivery next) =>
		!CancellationToken.IsCancellationRequested
		&& Interlocked.CompareExchange(ref _delivery, (int)next, (int)expected) == (int)expected;
}
