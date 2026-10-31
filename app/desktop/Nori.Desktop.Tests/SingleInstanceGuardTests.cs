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

	[Fact]
	public void 唤起通道被占用时保留互斥并降级()
	{
		string mutexName = $"Nori.Test.Mutex.{Guid.NewGuid():N}";
		using BlockedActivationChannel blocked = new();
		List<string> failures = [];

		using SingleInstanceGuard? guard = SingleInstanceGuard.TryAcquire(
			() => { }, true, mutexName, blocked.Name, (message, _) => failures.Add(message));

		Assert.NotNull(guard);
		Assert.False(guard.ActivationAvailable);
		Assert.Equal(["无法建立再次启动唤起通道"], failures);
		// Mutex 对持有线程可重入，必须从另一线程确认互斥仍然生效。
		SingleInstanceGuard? other = null;
		Thread probe = new(() => other = SingleInstanceGuard.TryAcquire(() => { }, false, mutexName, blocked.Name));
		probe.Start();
		Assert.True(probe.Join(TimeSpan.FromSeconds(3)));
		Assert.Null(other);
	}

	[Fact]
	public void 唤起路径优先使用用户运行目录并在缺失时按用户区分()
	{
		string runtime = Directory.CreateTempSubdirectory("nori-runtime-").FullName;
		try
		{
			string temp = Path.GetTempPath();
			string fallback = Path.Combine(temp, "NoriDesktopPet.Activate.alice");

			Assert.Equal(Path.Combine(runtime, "NoriDesktopPet.Activate"), SingleInstanceGuard.PortableActivationPath(runtime, temp, "alice"));
			Assert.Equal(fallback, SingleInstanceGuard.PortableActivationPath(null, temp, "alice"));
			Assert.Equal(fallback, SingleInstanceGuard.PortableActivationPath("relative/run", temp, "alice"));
			Assert.Equal(fallback, SingleInstanceGuard.PortableActivationPath(Path.Combine(runtime, "missing"), temp, "alice"));
			Assert.Equal(Path.Combine(temp, "NoriDesktopPet.Activate.CORP_bob_x"), SingleInstanceGuard.PortableActivationPath(null, temp, "CORP\\bob/x"));
			Assert.Equal(Path.Combine(temp, "NoriDesktopPet.Activate.user"), SingleInstanceGuard.PortableActivationPath("", temp, ""));
		}
		finally { Directory.Delete(runtime); }
	}

	/// <summary>Unix 用删不掉的目录占住 socket 路径；Windows 用同名的其他类型内核对象占住事件名。</summary>
	/// <remarks>测试工程的 OperatingSystem 别名恒为 Windows，占位方式必须看真实宿主平台。</remarks>
	private sealed class BlockedActivationChannel : IDisposable
	{
		private readonly Mutex? _squatter;

		public string Name { get; }

		public BlockedActivationChannel()
		{
			if (System.OperatingSystem.IsWindows())
			{
				Name = $@"Local\Nori.Test.Activate.{Guid.NewGuid():N}";
				_squatter = new Mutex(false, Name);
				return;
			}
			Name = Path.Combine(Path.GetTempPath(), $"nori-{Guid.NewGuid():N}");
			Directory.CreateDirectory(Name);
			File.WriteAllText(Path.Combine(Name, "occupied"), "");
		}

		public void Dispose()
		{
			_squatter?.Dispose();
			if (!System.OperatingSystem.IsWindows()) Directory.Delete(Name, recursive: true);
		}
	}

	/// <summary>Mutex 的获取和释放必须在同一个线程，测试首实例由专用线程持有。</summary>
	private sealed class PrimaryInstance : IDisposable
	{
		private readonly ManualResetEventSlim _stop = new(false);
		private readonly Thread _thread;
		private int _activations;
		private int _disposed;

		public string MutexName { get; } = $"Nori.Test.Mutex.{Guid.NewGuid():N}";
		// Unix 与正式路径一致使用绝对 socket 路径；平台判断同样要看真实宿主。
		public string ActivationName { get; } = System.OperatingSystem.IsWindows()
			? $"Nori.Test.Activate.{Guid.NewGuid():N}"
			: Path.Combine(Path.GetTempPath(), $"nori-{Guid.NewGuid():N}");
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
