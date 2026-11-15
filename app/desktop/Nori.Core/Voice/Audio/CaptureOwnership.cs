namespace Nori.Core.Voice.Audio;

/// <summary>
/// 进程内唯一的麦克风占用。
///
/// 按键说话和实时监听都从这里领同一把锁，避免两路采集同时打开设备。
/// 释放是幂等的，采集任务的收尾可以安全地再调一次。
/// </summary>
public sealed class CaptureOwnership
{
	private int _held;

	/// <summary>当前是否有人持有麦克风。</summary>
	public bool IsHeld => Volatile.Read(ref _held) != 0;

	/// <summary>尝试占用。已经有人持有时返回 false，不会抢占。</summary>
	public bool TryAcquire() => Interlocked.CompareExchange(ref _held, 1, 0) == 0;

	/// <summary>放开占用。没有持有时什么也不做。</summary>
	public void Release() => Interlocked.Exchange(ref _held, 0);
}
