using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia.Threading;
using Nori.Core.Platform;
using Nori.Desktop.Tray;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private sealed class TrayWatchPlatform(Func<CancellationToken, IAsyncEnumerable<bool>> watch) : IPlatformServices
	{
		public SessionType Session => SessionType.Wayland;
		public PlatformCapabilities Capabilities { get; } = new WaylandPlatformServices().Capabilities;
		public bool PrefersReducedMotion => true;
		public IAsyncEnumerable<bool> WatchTrayAvailabilityAsync(CancellationToken cancellationToken = default) => watch(cancellationToken);
		public (double X, double Y) GetCursorPosition() => (0, 0);
		public void StartWindowDrag(nint windowHandle) { }
		public void SetClickThrough(nint windowHandle, bool through) { }
	}

	/// <summary>不提供监听的平台，走接口默认的「只报告一次」。</summary>
	private sealed class TrayOncePlatform(bool available) : IPlatformServices
	{
		public SessionType Session => SessionType.Unknown;
		public PlatformCapabilities Capabilities { get; } = new UnsupportedPlatformServices().Capabilities;
		public bool PrefersReducedMotion => true;
		public Task<bool> IsTrayAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(available);
		public (double X, double Y) GetCursorPosition() => (0, 0);
		public void StartWindowDrag(nint windowHandle) { }
		public void SetClickThrough(nint windowHandle, bool through) { }
	}

	/// <summary>逐个读出测试写入的托盘状态，并记录是否在 UI 线程上枚举。</summary>
	private static async IAsyncEnumerable<bool> ReadTrayChanges(ChannelReader<bool> changes, StrongBox<int> ranOnUi,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		while (await changes.WaitToReadAsync(cancellationToken))
		{
			while (changes.TryRead(out bool available))
			{
				if (Dispatcher.UIThread.CheckAccess()) Interlocked.Exchange(ref ranOnUi.Value, 1);
				yield return available;
			}
		}
	}

	private static async IAsyncEnumerable<bool> FailTrayWatch(Exception failure)
	{
		await Task.Yield();
		if (failure is not null) throw failure;
		yield break;
	}

	/// <summary>进入等待后通知测试，然后一直等到取消，模拟没有任何变化的监听。</summary>
	private static async IAsyncEnumerable<bool> WaitTrayForever(TaskCompletionSource entered,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		entered.TrySetResult();
		await Task.Delay(Timeout.Infinite, cancellationToken);
		yield break;
	}

	/// <summary>门闸放开后报告一次，然后像真实监听一样一直等到取消。</summary>
	private static async IAsyncEnumerable<bool> ReportTrayAfter(Task gate, bool available, TaskCompletionSource? reported,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await gate.WaitAsync(cancellationToken);
		reported?.TrySetResult();
		yield return available;
		await Task.Delay(Timeout.Infinite, cancellationToken);
	}

	[Fact]
	public void 托盘状态变化才作废快照并通知窗口()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		var runtime = fixture._runtime;
		int notified = 0;
		runtime.StateChanged += () => notified++;
		int before = runtime.SnapshotVersion;
		runtime.TrayAvailable = true;
		Assert.Equal(before, runtime.SnapshotVersion);
		runtime.TrayAvailable = false;
		Assert.Equal(before + 1, runtime.SnapshotVersion);
		runtime.TrayAvailable = false;
		Assert.Equal(before + 1, runtime.SnapshotVersion);
		runtime.TrayAvailable = true;
		Assert.Equal(before + 2, runtime.SnapshotVersion);
		Assert.Equal(2, notified);
	}

	[Fact]
	public Task 托盘监听在后台枚举并跟随宿主消失和恢复() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using CancellationTokenSource cancellation = new();
		Channel<bool> changes = Channel.CreateUnbounded<bool>();
		StrongBox<int> ranOnUi = new();
		TrayWatchPlatform platform = new(token => ReadTrayChanges(changes.Reader, ranOnUi, token));
		Task monitor = TrayHostMonitor.RunAsync(fixture._services, platform, TimeSpan.FromMilliseconds(10), cancellation.Token);
		try
		{
			changes.Writer.TryWrite(false);
			await WaitUntilAsync(() => !fixture._runtime.TrayAvailable);
			changes.Writer.TryWrite(true);
			await WaitUntilAsync(() => fixture._runtime.TrayAvailable);
			Assert.Equal(0, Volatile.Read(ref ranOnUi.Value));
		}
		finally { cancellation.Cancel(); await monitor.WaitAsync(TimeSpan.FromSeconds(3)); }
	});

	[Fact]
	public Task 托盘监听失败会回退且稍后重新订阅() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using CancellationTokenSource cancellation = new();
		TaskCompletionSource recover = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int attempts = 0;
		TrayWatchPlatform platform = new(token => Interlocked.Increment(ref attempts) == 1
			? FailTrayWatch(new IOException("模拟会话总线断开"))
			: ReportTrayAfter(recover.Task, true, null, token));
		Task monitor = TrayHostMonitor.RunAsync(fixture._services, platform, TimeSpan.FromMilliseconds(10), cancellation.Token);
		try
		{
			// 监听失败时按无托盘处理，保证主窗口入口仍在。
			await WaitUntilAsync(() => !fixture._runtime.TrayAvailable && Volatile.Read(ref attempts) == 2);
			recover.SetResult();
			await WaitUntilAsync(() => fixture._runtime.TrayAvailable);
			Assert.Equal(2, Volatile.Read(ref attempts));
		}
		finally { cancellation.Cancel(); await monitor.WaitAsync(TimeSpan.FromSeconds(3)); }
	});

	[Fact]
	public Task 托盘监听退出时取消进行中的等待() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using CancellationTokenSource cancellation = new();
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TrayWatchPlatform platform = new(token => WaitTrayForever(entered, token));
		Task monitor = TrayHostMonitor.RunAsync(fixture._services, platform, TimeSpan.FromMilliseconds(10), cancellation.Token);
		try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
		finally { cancellation.Cancel(); await monitor.WaitAsync(TimeSpan.FromSeconds(3)); }
		Assert.True(fixture._runtime.TrayAvailable);
	});

	[Fact]
	public Task 托盘监听退出不等待被阻塞的界面线程且不再执行排队的调度() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using CancellationTokenSource cancellation = new();
		TaskCompletionSource reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TrayWatchPlatform platform = new(token => ReportTrayAfter(Task.CompletedTask, false, reported, token));
		Task monitor = TrayHostMonitor.RunAsync(fixture._services, platform, TimeSpan.FromMilliseconds(10), cancellation.Token);
		Task cancel = Task.Run(async () => { await reported.Task; cancellation.Cancel(); });
		try
		{
			// 模拟 Exit 处理器同步等待后台收拢，此时 UI 队列不能继续执行。
			monitor.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
			cancel.GetAwaiter().GetResult();
		}
		finally { cancellation.Cancel(); }
		Assert.True(fixture._runtime.TrayAvailable);
		return Task.CompletedTask;
	});

	[Fact]
	public Task 平台只报告一次时应用后结束监听() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		Task monitor = TrayHostMonitor.RunAsync(fixture._services, new TrayOncePlatform(false), TimeSpan.FromMilliseconds(10), CancellationToken.None);
		await WaitUntilAsync(() => monitor.IsCompleted);
		await monitor;
		Assert.False(fixture._runtime.TrayAvailable);
	});
}
