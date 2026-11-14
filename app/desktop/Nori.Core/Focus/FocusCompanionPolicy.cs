namespace Nori.Core.Focus;

/// <summary>一场专注是否挡住主动关怀，以及提醒是否保持静默。</summary>
/// <param name="Active">进行中或暂停都算还在专注。结束后不再挡住。</param>
/// <param name="SilentReminders">为真时，专注期间的提醒仍然触发，但不朗读、不播非必要动作。</param>
public readonly record struct FocusPresence(bool Active, bool SilentReminders);

/// <summary>专注期间哪些既有通道继续工作。用户自己发起的聊天始终放行。</summary>
public static class FocusCompanionPolicy
{
	/// <summary>问候和挂机关怀在专注期间不发出。结束后下一次调度可以照常判断，也不改写用户保存的开关。</summary>
	public static bool AllowsProactiveCare(FocusPresence presence) => !presence.Active;

	/// <summary>定时提醒照常触发。只有专注中且用户选择静默时才不朗读。</summary>
	public static bool AllowsReminderSpeech(FocusPresence presence) => !presence.Active || !presence.SilentReminders;

	/// <summary>静默提醒不播放挥手这类非必要动作。专注之外保持原来的动作。</summary>
	public static bool AllowsReminderAnimation(FocusPresence presence) => !presence.Active || !presence.SilentReminders;

	/// <summary>界面真正播出前再收紧一次。只关掉已经放行的朗读，不把静默提醒重新打开。</summary>
	public static bool StillAllowsSpeech(bool messageAllows, FocusPresence presence) =>
		messageAllows && AllowsReminderSpeech(presence);

	/// <summary>界面真正播出前再收紧一次。只关掉已经放行的动作，不把静默提醒重新打开。</summary>
	public static bool StillAllowsAnimation(bool messageAllows, FocusPresence presence) =>
		messageAllows && AllowsReminderAnimation(presence);

	/// <summary>用户自己打开的对话不受专注开关影响。</summary>
	public static bool AllowsUserChat() => true;

	/// <summary>用户回复是否自动朗读。只看自动朗读和安全模式，不看专注。</summary>
	public static bool ShouldSpeakUserReply(bool autoTts, bool safeMode) => AllowsUserChat() && autoTts && !safeMode;

	/// <summary>主动消息附带的动作。静默提醒会把这一项关掉。</summary>
	public static bool ShouldAnimateProactive(bool allowAnimation) => allowAnimation;

	/// <summary>主动消息是否朗读。没开自动朗读时，和专注之前一样不出声。</summary>
	public static bool ShouldSpeakProactive(bool allowSpeech, bool autoTts) => allowSpeech && autoTts;

	/// <summary>不能朗读时把文字留在伴侣气泡里，避免静默提醒被直接丢掉。</summary>
	public static bool ShouldShowProactiveText(bool allowSpeech) => !allowSpeech;

	/// <summary>氛围类动作只在用户原本开着、且当前不在专注时播放。不改写用户保存的开关。</summary>
	public static bool AllowsAmbientMotion(bool userEnabled, bool focusQuiet) => userEnabled && !focusQuiet;
}
