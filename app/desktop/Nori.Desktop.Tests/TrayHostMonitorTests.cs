using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Core.Platform;
using Nori.Desktop.Tray;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private sealed class TrayProbePlatform(Func<CancellationToken, Task<bool>> probe) : IPlatformServices
	{
		public SessionType Session => SessionType.Wayland;
		public PlatformCapabilities Capabilities { get; } = new WaylandPlatformServices().Capabilities;
		public bool PrefersReducedMotion => true;
		public Task<bool> IsTrayAvailableAsync(CancellationToken cancellationToken = default) => probe(cancellationToken);
		public (double X, double Y) GetCursorPosition() => (0, 0);
		public void StartWindowDrag(nint windowHandle) { }
		public void SetClickThrough(nint windowHandle, bool through) { }
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

	[Theory]
	[InlineData("zh-CN", 720, 480)]
	[InlineData("en-US", 720, 480)]
	[InlineData("zh-CN", 1920, 1080)]
	[InlineData("en-US", 1920, 1080)]
	public Task 托盘消失后恢复任务栏入口且恢复后不藏起当前窗口(string language, int width, int height) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._config.Set("language", new ConfigValue.Text(language));
		WindowManager manager = new(_ => { });
		fixture._services.Windows = manager;
		MainWindow main = new(WindowDefinition.All.Single(item => item.Label == WindowLabels.Main), fixture._services)
		{
			Width = width, Height = height,
		};
		RegisterNativeTestWindow(manager, WindowLabels.Main, main);
		try
		{
			Assert.False(main.IsVisible);
			TrayHostMonitor.ApplyAvailability(fixture._services, false);
			Assert.True(main.IsVisible);
			Assert.True(main.ShowInTaskbar);
			Assert.Equal(WindowState.Minimized, main.WindowState);
			Assert.False(main.IsActive);
			Assert.True(main.ShowActivated);
			main.Close();
			Assert.True(main.IsVisible);
			manager.Show(WindowLabels.Main);
			main.WindowState = WindowState.Normal;
			main.UpdateLayout();
			string quit = language == "en-US" ? "Quit" : "退出程序";
			Button exit = Assert.Single(main.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, quit));
			await WaitUntilAsync(() => exit.IsVisible);
			Assert.DoesNotContain(main.GetVisualDescendants().OfType<TextBlock>(), text =>
				text.Text?.Contains("系统托盘不可用", StringComparison.Ordinal) == true
				|| text.Text?.Contains("System tray unavailable", StringComparison.Ordinal) == true);
			CaptureUi(main, $"tray-lost-{language}-{width}x{height}");
			TrayHostMonitor.ApplyAvailability(fixture._services, true);
			await WaitUntilAsync(() => !exit.IsVisible);
			Assert.True(main.IsVisible);
			CaptureUi(main, $"tray-restored-{language}-{width}x{height}");
			main.Close();
			Assert.False(main.IsVisible);
		}
		finally { main.AllowClose = true; main.Close(); }
	});

	[Theory]
	[InlineData(WindowLabels.FirstRun)]
	[InlineData(WindowLabels.Init)]
	public Task 托盘变化不打断首次运行或初始化(string activeWindow) => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._windows.Show(activeWindow);
		TrayHostMonitor.ApplyAvailability(fixture._services, false);
		Assert.False(fixture._runtime.TrayAvailable);
		Assert.True(fixture._windows.IsWindowVisible(activeWindow));
		Assert.False(fixture._windows.IsWindowVisible(WindowLabels.Main));
		return Task.CompletedTask;
	});

	[Fact]
	public Task 托盘复查在后台执行并跟随宿主消失和恢复() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using CancellationTokenSource cancellation = new();
		int desired = 0;
		int ranOnUi = 0;
		TrayProbePlatform platform = new(_ =>
		{
			if (Dispatcher.UIThread.CheckAccess()) Interlocked.Exchange(ref ranOnUi, 1);
			return Task.FromResult(Volatile.Read(ref desired) == 1);
		});
		Task monitor = TrayHostMonitor.RunAsync(fixture._services, platform, TimeSpan.FromMilliseconds(10), cancellation.Token);
		try
		{
			await WaitUntilAsync(() => !fixture._runtime.TrayAvailable);
			Volatile.Write(ref desired, 1);
			await WaitUntilAsync(() => fixture._runtime.TrayAvailable);
			Assert.Equal(0, Volatile.Read(ref ranOnUi));
		}
		finally { cancellation.Cancel(); await monitor.WaitAsync(TimeSpan.FromSeconds(3)); }
	});

	[Fact]
	public Task 托盘探测失败会回退且下一轮可以恢复() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using CancellationTokenSource cancellation = new();
		int fail = 1;
		TrayProbePlatform platform = new(_ => Volatile.Read(ref fail) == 1
			? Task.FromException<bool>(new IOException("模拟会话总线暂时不可用")) : Task.FromResult(true));
		Task monitor = TrayHostMonitor.RunAsync(fixture._services, platform, TimeSpan.FromMilliseconds(10), cancellation.Token);
		try
		{
			await WaitUntilAsync(() => !fixture._runtime.TrayAvailable);
			Volatile.Write(ref fail, 0);
			await WaitUntilAsync(() => fixture._runtime.TrayAvailable);
		}
		finally { cancellation.Cancel(); await monitor.WaitAsync(TimeSpan.FromSeconds(3)); }
	});

	[Fact]
	public Task 托盘复查退出时取消进行中的探测() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using CancellationTokenSource cancellation = new();
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TrayProbePlatform platform = new(async token =>
		{
			entered.TrySetResult();
			await Task.Delay(Timeout.Infinite, token);
			return false;
		});
		Task monitor = TrayHostMonitor.RunAsync(fixture._services, platform, TimeSpan.FromMilliseconds(10), cancellation.Token);
		try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
		finally { cancellation.Cancel(); await monitor.WaitAsync(TimeSpan.FromSeconds(3)); }
		Assert.True(fixture._runtime.TrayAvailable);
	});

	[Fact]
	public Task 托盘复查退出不等待被阻塞的界面线程() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using CancellationTokenSource cancellation = new();
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TrayProbePlatform platform = new(_ => { entered.TrySetResult(); return Task.FromResult(false); });
		Task monitor = TrayHostMonitor.RunAsync(fixture._services, platform, TimeSpan.FromMilliseconds(10), cancellation.Token);
		Task cancel = Task.Run(async () => { await entered.Task; cancellation.Cancel(); });
		try
		{
			// 模拟 Exit 处理器同步等待后台收拢，此时 UI 队列不能继续执行。
			monitor.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
			cancel.GetAwaiter().GetResult();
		}
		finally { cancellation.Cancel(); }
		return Task.CompletedTask;
	});
}
