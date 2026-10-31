namespace Nori.Desktop.Live2D;

/// <summary>
/// GPU 故障后重建渲染表面的退避策略。
///
/// 连续故障逐级拉长等待，避免设备尚未恢复时连环创建交换链纹理；
/// 距上次故障超过稳定窗口后重新从最短等待开始。时间由调用方提供单调时钟读数。
/// </summary>
internal sealed class GpuRecoveryBackoff
{
	internal static readonly TimeSpan[] Delays =
	[
		TimeSpan.FromSeconds(1),
		TimeSpan.FromSeconds(2),
		TimeSpan.FromSeconds(5),
		TimeSpan.FromSeconds(10),
		TimeSpan.FromSeconds(30),
	];

	internal static readonly TimeSpan StableWindow = TimeSpan.FromMinutes(1);

	private int _streak;
	private TimeSpan? _lastFailure;

	/// <summary>记录一次故障并返回下一次重建前的等待时间。</summary>
	public TimeSpan NextDelay(TimeSpan now)
	{
		if (!IsRecovering(now)) _streak = 0;
		_lastFailure = now;
		TimeSpan delay = Delays[Math.Min(_streak, Delays.Length - 1)];
		_streak++;
		return delay;
	}

	/// <summary>最近一次故障仍在稳定窗口内，期间的初始化失败也按 GPU 故障重试。</summary>
	public bool IsRecovering(TimeSpan now) => _lastFailure is { } last && now - last < StableWindow;
}
