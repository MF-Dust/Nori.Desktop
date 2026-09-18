using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Nori.Core.Platform;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	/// <summary>只模拟窗口关闭，不终止共享的 Headless UI 会话。</summary>
	private sealed class NativeWindowLifetime : IClassicDesktopStyleApplicationLifetime
	{
		public int ShutdownCount { get; private set; }
		public string[]? Args => null;
		public ShutdownMode ShutdownMode { get; set; } = ShutdownMode.OnExplicitShutdown;
		public Window? MainWindow { get; set; }
		public List<Window> ManagedWindows { get; } = [];
		public IReadOnlyList<Window> Windows => ManagedWindows;
		public event EventHandler<ControlledApplicationLifetimeStartupEventArgs>? Startup { add { } remove { } }
		public event EventHandler<ControlledApplicationLifetimeExitEventArgs>? Exit { add { } remove { } }
		public event EventHandler<ShutdownRequestedEventArgs>? ShutdownRequested { add { } remove { } }

		public bool TryShutdown(int exitCode = 0)
		{
			Shutdown(exitCode);
			return true;
		}

		public void Shutdown(int exitCode = 0)
		{
			ShutdownCount++;
			foreach (Window window in ManagedWindows) window.Close();
		}
	}

	/// <summary>仅装配被测原生窗口，避免生命周期回归加载 WebView 或音频宿主。</summary>
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
		WindowManager manager = new(null!, lifetime, fixture._services.Paths);
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
	public Task 退出统一放行新原生窗口且重复退出只执行一次() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		NativeWindowLifetime lifetime = new();
		WindowManager manager = new(null!, lifetime, fixture._services.Paths);
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

	[Fact]
	public Task 首启与初始化拖动能力降级沿用系统边框() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		FirstRunWindow firstRun = new(FirstRunDefinition(), fixture._services);
		InitWindow init = new(InitDefinition(), fixture._services);
		try
		{
			WindowDecorations expected = PlatformServices.Current.Capabilities.SupportsWindowDrag
				? WindowDecorations.None : WindowDecorations.Full;
			Assert.Equal(expected, firstRun.WindowDecorations);
			Assert.Equal(expected, init.WindowDecorations);
			Assert.Equal(FirstRunDefinition().CanResize, firstRun.CanResize);
		}
		finally
		{
			firstRun.AllowClose = init.AllowClose = true;
			firstRun.Close(); init.Close();
		}
		return Task.CompletedTask;
	});
}
