using Nori.Desktop.Startup;

namespace Nori.Desktop.Tests;

public sealed class SingleInstanceGuardTests
{
	[Fact]
	public void 第二实例退出并能重复唤起首实例()
	{
		using PrimaryInstance primary = new();
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			using SingleInstanceGuard? second = SingleInstanceGuard.TryAcquire(
				() => throw new InvalidOperationException("第二实例不应成为监听者"), true,
				primary.MutexName, primary.ActivationName);

			Assert.Null(second);
			Assert.True(primary.Activated.WaitOne(TimeSpan.FromSeconds(2)));
			Assert.Equal(attempt, primary.Activations);
		}
	}

	[Fact]
	public void 安全模式不唤起首实例且不破坏后续激活()
	{
		using PrimaryInstance primary = new();
		using SingleInstanceGuard? safe = SingleInstanceGuard.TryAcquire(
			() => { }, false, primary.MutexName, primary.ActivationName);

		Assert.Null(safe);
		Assert.False(primary.Activated.WaitOne(100));
		Assert.Equal(0, primary.Activations);

		using SingleInstanceGuard? normal = SingleInstanceGuard.TryAcquire(
			() => { }, true, primary.MutexName, primary.ActivationName);
		Assert.Null(normal);
		Assert.True(primary.Activated.WaitOne(TimeSpan.FromSeconds(2)));
	}

	[Fact]
	public void 空闲监听可及时释放且后续实例能重新获取同名资源()
	{
		using PrimaryInstance primary = new();
		primary.Dispose();

		using SingleInstanceGuard? replacement = SingleInstanceGuard.TryAcquire(
			() => { }, true, primary.MutexName, primary.ActivationName);

		Assert.NotNull(replacement);
	}

	/// <summary>Mutex 的获取和释放必须在同一个线程，测试首实例由专用线程持有。</summary>
	private sealed class PrimaryInstance : IDisposable
	{
		private readonly ManualResetEventSlim _stop = new(false);
		private readonly Thread _thread;
		private int _activations;
		private int _disposed;

		public string MutexName { get; } = $"Nori.Test.Mutex.{Guid.NewGuid():N}";
		public string ActivationName { get; } = $"Nori.Test.Activate.{Guid.NewGuid():N}";
		public AutoResetEvent Activated { get; } = new(false);
		public int Activations => Volatile.Read(ref _activations);

		public PrimaryInstance()
		{
			TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
			_thread = new Thread(() =>
			{
				try
				{
					using SingleInstanceGuard guard = SingleInstanceGuard.TryAcquire(() =>
					{
						Interlocked.Increment(ref _activations);
						Activated.Set();
					}, true, MutexName, ActivationName) ?? throw new InvalidOperationException("测试首实例获取失败");
					started.TrySetResult();
					_stop.Wait();
				}
				catch (Exception exception) { started.TrySetException(exception); }
			}) { IsBackground = true };
			_thread.Start();
			started.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
			_stop.Set();
			Assert.True(_thread.Join(TimeSpan.FromSeconds(3)), "实例唤起监听未及时退出");
			_stop.Dispose();
			Activated.Dispose();
		}
	}
}
