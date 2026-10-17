using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	/// <summary>只模拟窗口关闭，不终止共享的 Headless UI 会话。</summary>
	private sealed class NativeWindowLifetime
	{
		public int ShutdownCount { get; private set; }
		public int? ExitCode { get; private set; }
		public List<Window> ManagedWindows { get; } = [];

		public void Shutdown(int exitCode = 0)
		{
			ShutdownCount++;
			ExitCode = exitCode;
			foreach (Window window in ManagedWindows) window.Close();
		}
	}

	/// <summary>仅装配被测原生窗口，避免生命周期回归加载音频设备。</summary>
	private static void RegisterNativeTestWindow(WindowManager manager, string label, Window window)
	{
		const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
		Dictionary<string, Window> windows = Assert.IsType<Dictionary<string, Window>>(
			typeof(WindowManager).GetField("_windows", flags)!.GetValue(manager));
		windows[label] = window;
		typeof(WindowManager).GetMethod("TrackVisibility", flags)!.Invoke(manager, [label, window]);
	}

	[Theory]
	[InlineData(WindowLabels.FirstRun)]
	[InlineData(WindowLabels.Init)]
	[InlineData(WindowLabels.Main)]
	public Task 宿主关闭会真正释放新原生窗口并移除引用(string label) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		NativeWindowLifetime lifetime = new();
		WindowManager manager = new(lifetime.Shutdown);
		fixture._services.Windows = manager;
		WindowDefinition definition = WindowDefinition.All.Single(item => item.Label == label);
		Window window = label switch
		{
			WindowLabels.FirstRun => new FirstRunWindow(definition, fixture._services),
			WindowLabels.Init => new InitWindow(definition, fixture._services),
			_ => new MainWindow(definition, fixture._services),
		};
		RegisterNativeTestWindow(manager, label, window);
		int closed = 0;
		window.Closed += (_, _) => closed++;
		try
		{
			manager.Show(label);
			Assert.True(manager.IsWindowVisible(label));
			manager.Close(label);
			Assert.Equal(1, closed);
			Assert.Null(manager.Get(label));
			Assert.False(manager.IsWindowVisible(label));
			manager.Close(label);
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			Assert.Equal(0, lifetime.ShutdownCount);
			Assert.Null(lifetime.ExitCode);
		}
		finally
		{
			if (window is FirstRunWindow firstRun) firstRun.AllowClose = true;
			else if (window is InitWindow init) init.AllowClose = true;
			else if (window is MainWindow main) main.AllowClose = true;
			window.Close();
		}
	});

	[Fact]
	public Task 主窗口用户关闭后可由托盘显示路径重复唤起() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		WindowManager manager = new(_ => { });
		fixture._services.Windows = manager;
		MainWindow main = new(WindowDefinition.All.Single(item => item.Label == WindowLabels.Main), fixture._services);
		RegisterNativeTestWindow(manager, WindowLabels.Main, main);
		int closed = 0;
		main.Closed += (_, _) => closed++;
		try
		{
			manager.Show(WindowLabels.Main);
			for (int attempt = 0; attempt < 2; attempt++)
			{
				main.Close();
				Assert.False(manager.IsWindowVisible(WindowLabels.Main));
				Assert.Same(main, manager.Get(WindowLabels.Main));
				Assert.Equal(0, closed);

				manager.Show(WindowLabels.Main);
				Assert.True(manager.IsWindowVisible(WindowLabels.Main));
			}
		}
		finally
		{
			main.AllowClose = true;
			main.Close();
		}
		return Task.CompletedTask;
	});

	[Fact]
	public Task 退出统一放行新原生窗口且重复退出只执行一次() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		NativeWindowLifetime lifetime = new();
		WindowManager manager = new(lifetime.Shutdown);
		fixture._services.Windows = manager;
		FirstRunWindow firstRun = new(FirstRunDefinition(), fixture._services);
		InitWindow init = new(InitDefinition(), fixture._services);
		MainWindow main = new(WindowDefinition.All.Single(item => item.Label == WindowLabels.Main), fixture._services);
		int closed = 0;
		foreach ((string label, Window window) in new (string, Window)[]
		{
			(WindowLabels.FirstRun, firstRun), (WindowLabels.Init, init), (WindowLabels.Main, main),
		})
		{
			RegisterNativeTestWindow(manager, label, window);
			lifetime.ManagedWindows.Add(window);
			window.Closed += (_, _) => closed++;
			manager.Show(label);
		}
		try
		{
			manager.Shutdown();
			manager.Shutdown();
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			Assert.Equal(1, lifetime.ShutdownCount);
			Assert.Equal(0, lifetime.ExitCode);
			Assert.Equal(3, closed);
			Assert.True(firstRun.AllowClose && init.AllowClose && main.AllowClose);
			Assert.False(init.AnimationRunningForTests);
			Assert.False(init.WatchdogRunningForTests);
			Assert.False(manager.IsWindowVisible(WindowLabels.Main));
		}
		finally
		{
			firstRun.AllowClose = init.AllowClose = main.AllowClose = true;
			firstRun.Close(); init.Close(); main.Close();
		}
	});

	private static WindowDefinition FirstRunDefinition() =>
		WindowDefinition.All.Single(item => item.Label == WindowLabels.FirstRun);

	private static WindowDefinition InitDefinition() =>
		WindowDefinition.All.Single(item => item.Label == WindowLabels.Init);
}
