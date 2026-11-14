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
	public Task 已销毁的主窗口可被显示路径重新创建() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		WindowManager manager = new(_ => { });
		fixture._services.Windows = manager;
		const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
		typeof(WindowManager).GetField("_services", flags)!.SetValue(manager, fixture._services);
		MainWindow main = new(WindowDefinition.All.Single(item => item.Label == WindowLabels.Main), fixture._services);
		RegisterNativeTestWindow(manager, WindowLabels.Main, main);
		try
		{
			manager.Show(WindowLabels.Main);
			main.AllowClose = true;
			main.Close();
			Assert.Null(main.PlatformImpl);
			Assert.Same(main, manager.Get(WindowLabels.Main));

			manager.Show(WindowLabels.Main);

			MainWindow revived = Assert.IsType<MainWindow>(manager.Get(WindowLabels.Main));
			Assert.NotSame(main, revived);
			Assert.True(manager.IsWindowVisible(WindowLabels.Main));
			Assert.NotNull(revived.PlatformImpl);
		}
		finally
		{
			if (manager.Get(WindowLabels.Main) is MainWindow current)
			{
				current.AllowClose = true;
				current.Close();
			}
		}
		return Task.CompletedTask;
	});

	[Fact]
	public Task 关闭时隐藏失败不会销毁窗口() => WithSettingsUiAsync(() =>
	{
		HideFailsWindow window = new();
		int closed = 0;
		window.Closed += (_, _) => closed++;
		try
		{
			window.Show();
			window.Close();
			Assert.Equal(0, closed);
			Assert.NotNull(window.PlatformImpl);
		}
		finally
		{
			window.AllowClose = true;
			window.Close();
		}
		return Task.CompletedTask;
	});

	private sealed class HideFailsWindow : Window
	{
		public bool AllowClose { get; set; }

		public HideFailsWindow()
		{
			Closing += (_, args) => WindowClose.CancelAndHide(args, AllowClose, Hide);
		}

		public override void Hide() => throw new InvalidOperationException("隐藏失败");
	}

	[Theory]
	[InlineData(WindowState.Minimized, false, WindowState.Normal)]
	[InlineData(WindowState.Minimized, true, WindowState.Normal)]
	[InlineData(WindowState.Maximized, false, WindowState.Maximized)]
	public Task 主窗口唤起恢复最小化但保留最大化(WindowState initial, bool hidden, WindowState expected) => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		WindowManager manager = new(_ => { });
		fixture._services.Windows = manager;
		MainWindow main = new(WindowDefinition.All.Single(item => item.Label == WindowLabels.Main), fixture._services);
		RegisterNativeTestWindow(manager, WindowLabels.Main, main);
		try
		{
			main.Show();
			main.WindowState = initial;
			if (hidden) main.Hide();

			manager.Show(WindowLabels.Main);

			Assert.True(manager.IsWindowVisible(WindowLabels.Main));
			Assert.Equal(expected, main.WindowState);
		}
		finally { main.AllowClose = true; main.Close(); }
		return Task.CompletedTask;
	});

	[Fact]
	public Task 没有托盘时关闭主窗口会最小化而不隐藏入口() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture.ConfigureDesktop();
		fixture._services.Runtime!.TrayAvailable = false;
		MainWindow main = new(WindowDefinition.All.Single(item => item.Label == WindowLabels.Main), fixture._services);
		try
		{
			main.Show();
			main.Close();
			Assert.True(main.IsVisible);
			Assert.True(main.ShowInTaskbar);
			Assert.Equal(WindowState.Minimized, main.WindowState);
		}
		finally { main.AllowClose = true; main.Close(); }
		return Task.CompletedTask;
	});

	[Theory]
	[InlineData(1, 1, false)]
	[InlineData(3.9, 0, false)]
	[InlineData(4, 0, true)]
	[InlineData(3, 3, true)]
	public void Wayland拖动阈值使用局部坐标(double dx, double dy, bool dragged)
	{
		Assert.Equal(dragged, PetWindow.HasExceededLocalDragThreshold(new(10, 20), new(10 + dx, 20 + dy)));
	}

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
