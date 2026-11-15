namespace Nori.Core.Voice.Realtime;

/// <summary>
/// 一轮实时语音的阶段。
///
/// Error 可以再次开始。Interrupting 只在主动打断的瞬间出现，随后进入 Idle 或 Listening。
/// </summary>
public enum RealtimeVoicePhase
{
	/// <summary>没有在听，麦克风没有为实时语音打开。</summary>
	Idle,

	/// <summary>正在听。本地能量检测决定何时算一句话。</summary>
	Listening,

	/// <summary>一句话已经截下，正在识别。麦克风已放开。</summary>
	Transcribing,

	/// <summary>识别结果已经交给这一轮的处理，旧结果不能再覆盖它。</summary>
	Thinking,

	/// <summary>正在朗读。麦克风检测暂停，避免把播放声再录进去。</summary>
	Speaking,

	/// <summary>正在停掉朗读和这一轮的后续输出。</summary>
	Interrupting,

	/// <summary>这一轮失败了，麦克风已关闭。可以重新开始。</summary>
	Error,
}
