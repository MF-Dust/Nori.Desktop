namespace Nori.Core.Platform;

/// <summary>
/// D-Bus 连接与调用本身不接受取消令牌，只能在外层限时等待。
/// 等待超时或取消后原任务仍在运行，随后会因连接释放而失败；这里补上观察，
/// 避免它们在 GC 时变成未观察的任务异常并被当作崩溃上报。
/// </summary>
internal static class DBusTaskExtensions
{
	internal static Task WaitObservedAsync(this Task task, CancellationToken cancellationToken) =>
		task.WaitObservedAsync(Timeout.InfiniteTimeSpan, cancellationToken);

	internal static async Task WaitObservedAsync(this Task task, TimeSpan timeout, CancellationToken cancellationToken)
	{
		try { await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
		catch
		{
			Observe(task);
			throw;
		}
	}

	internal static Task<T> WaitObservedAsync<T>(this Task<T> task, CancellationToken cancellationToken) =>
		task.WaitObservedAsync(Timeout.InfiniteTimeSpan, cancellationToken);

	internal static async Task<T> WaitObservedAsync<T>(this Task<T> task, TimeSpan timeout, CancellationToken cancellationToken)
	{
		try { return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
		catch
		{
			Observe(task);
			throw;
		}
	}

	/// <summary>读取一次 Exception 即视为已观察；原任务若已自行失败，这里同样无害。</summary>
	private static void Observe(Task task) => task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
		TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
