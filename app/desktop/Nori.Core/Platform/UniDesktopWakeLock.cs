namespace Nori.Core.Platform;

/// <summary>调用方须在同一后台线程获取和释放；并发或重复释放只调用一次原生接口。</summary>
internal sealed class UniDesktopWakeLock(Action release, bool requiresOwnerThread = false) : IDisposable
{
	private Action? _release = release;
	private readonly int _ownerThread = Environment.CurrentManagedThreadId;
	public void Dispose()
	{
		if (Volatile.Read(ref _release) is null) return;
		if (requiresOwnerThread && Environment.CurrentManagedThreadId != _ownerThread)
			throw new InvalidOperationException("Windows 常亮锁必须在获取它的线程上释放。");
		Interlocked.Exchange(ref _release, null)?.Invoke();
	}
}
