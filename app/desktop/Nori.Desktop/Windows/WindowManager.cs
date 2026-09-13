using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Nori.Core.Assets;
using Nori.Core.Data;
using Nori.Desktop.Bridge;

namespace Nori.Desktop.Windows;

/// <summary>
/// 窗口调度
///
/// 承接原来 Rust 侧 lib.rs setup / tray.rs 与前端 services/window/index.ts 的窗口调度职责.
/// 管理三个 WebView、原生伴侣视窗与按需创建的原生设置和记忆窗口。
/// </summary>
public sealed class WindowManager(AssetServer assetServer, IClassicDesktopStyleApplicationLifetime lifetime, AppStoragePaths storagePaths) : IWindowManager
{
	private readonly AssetServer _assetServer = assetServer;
	private readonly AppStoragePaths _storagePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));
	private readonly IClassicDesktopStyleApplicationLifetime _lifetime = lifetime;
	private readonly Dictionary<string, Window> _windows = [];
	private readonly ConcurrentDictionary<string, bool> _visible = new();
	private PetWindow? _petWindow;
	private AppServices? _services;
	private int _shutdownRequested;
	private Task? _memoryCloseTask;

	/// <inheritdoc />
	public event Action<string, bool>? VisibilityChanged;

	/// <summary>
	/// 建好全部窗口 (不显示)
	/// </summary>
	public void CreateAll(NoriBridge bridge, AppServices services)
	{
		_services = services;
		foreach (WindowDefinition definition in WindowDefinition.All)
		{
			if (definition.Label == WindowLabels.Pet)
			{
				PetWindow petWindow = new(definition, services);
				petWindow.Closing += (_, args) =>
				{
					if (petWindow.AllowClose) return;
					args.Cancel = true;
					petWindow.Hide();
				};
				_windows[definition.Label] = petWindow;
				_petWindow = petWindow;
			}
			else
			{
				NoriWindow window = new(definition, bridge, _assetServer.WindowUrl(definition.Label), _storagePaths);
				window.Closing += (_, args) =>
				{
					if (window.AllowClose) return;
					args.Cancel = true;
					window.Hide();
				};
				_windows[definition.Label] = window;
			}

			TrackVisibility(definition.Label, _windows[definition.Label]);
		}
	}

	/// <summary>
	/// 跟踪一个窗口的可见性
	///
	/// 直接监听 IsVisibleProperty, 无论是托盘切换、命令调用还是窗口自己 Hide,
	/// 缓存与事件都不会漏.
	/// </summary>
	private void TrackVisibility(string label, Window window)
	{
		_visible[label] = window.IsVisible;
		window.PropertyChanged += (_, args) =>
		{
			if (args.Property != Visual.IsVisibleProperty) return;
			bool visible = window.IsVisible;
			if (_visible.TryGetValue(label, out bool previous) && previous == visible) return;
			_visible[label] = visible;
			VisibilityChanged?.Invoke(label, visible);
		};
	}

	/// <inheritdoc />
	public bool IsWindowVisible(string label) => _visible.TryGetValue(label, out bool visible) && visible;

	/// <summary>
	/// 按标签取窗口, 不存在返回 null
	/// </summary>
	public Window? Get(string? label) => label is not null && _windows.TryGetValue(label, out Window? window) ? window : null;

	/// <summary>
	/// 按标签取 WebView2 窗口
	/// </summary>
	public NoriWindow? GetNoriWindow(string? label) => Get(label) as NoriWindow;

	/// <summary>
	/// 原生伴侣视窗引用
	/// </summary>
	public PetWindow? Pet => _petWindow;

	/// <summary>
	/// 全部窗口
	/// </summary>
	public IEnumerable<Window> All => _windows.Values;

	/// <summary>
	/// 显示窗口；伴侣视窗不抢焦点，其他窗口同时聚焦
	/// </summary>
	public void Show(string label)
	{
		if (label == WindowLabels.Settings)
		{
			ShowSettings();
			return;
		}
		if (label == WindowLabels.Memory)
		{
			ShowMemory();
			return;
		}
		if (Get(label) is not { } window) return;
		window.Show();
		if (window is PetWindow pet)
		{
			// 伴侣视窗不抢当前应用焦点；点击穿透由分层样式实现，窗口保持置顶。
			pet.ApplyWindowSize();
			pet.ReapplyInputState();
			return;
		}
		window.Activate();
	}

	/// <inheritdoc />
	public void ShowSettings(string? page = null)
	{
		Dispatcher.UIThread.VerifyAccess();
		if (Volatile.Read(ref _shutdownRequested) != 0) return;
		if (page is not null && page is not ("ai" or "voice" or "proactive" or "skills" or "mcp" or "automation" or "plugins" or "general" or "updates" or "debug" or "about"))
			throw new ArgumentException("未知的设置页面", nameof(page));
		if (Get(WindowLabels.Settings) is not SettingsWindow settings)
		{
			AppServices services = _services ?? throw new InvalidOperationException("应用窗口尚未就绪");
			settings = new SettingsWindow(services);
			_windows[WindowLabels.Settings] = settings;
			TrackVisibility(WindowLabels.Settings, settings);
		}
		settings.Navigate(page);
		if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
		settings.Show();
		settings.Activate();
	}

	/// <summary>检查原生记忆页面键，供窗口调度和桥接入口共同使用。</summary>
	internal static bool IsMemoryPage(string page) => page is "overview" or "memories" or "atoms" or "knowledge" or "archive" or "transfer" or "debugger" or "advanced";

	/// <inheritdoc />
	public void ShowMemory(string? page = null)
	{
		Dispatcher.UIThread.VerifyAccess();
		if (Volatile.Read(ref _shutdownRequested) != 0) return;
		if (_memoryCloseTask is {IsCompleted: false}) return;
		if (page is not null && !IsMemoryPage(page))
			throw new ArgumentException("未知的记忆页面", nameof(page));
		if (Get(WindowLabels.Memory) is not MemoryWindow memory)
		{
			AppServices services = _services ?? throw new InvalidOperationException("应用窗口尚未就绪");
			memory = new MemoryWindow(services);
			_windows[WindowLabels.Memory] = memory;
			TrackVisibility(WindowLabels.Memory, memory);
		}
		memory.Navigate(page);
		if (memory.WindowState == WindowState.Minimized) memory.WindowState = WindowState.Normal;
		memory.Show();
		memory.Activate();
		_ = RefreshMemoryAsync(memory);
	}

	private async Task RefreshMemoryAsync(MemoryWindow memory)
	{
		try { await memory.RefreshAsync(); }
		catch (OperationCanceledException) { }
		catch (Exception exception)
		{
			_services?.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", $"记忆窗口刷新失败: {exception.GetType().Name}");
		}
	}

	/// <summary>
	/// 隐藏窗口
	/// </summary>
	public void Hide(string label) => Get(label)?.Hide();

	/// <summary>
	/// 关闭窗口 (真正销毁, 不再复用)
	/// </summary>
	public void Close(string label)
	{
		if (Get(label) is not { } window) return;
		if (window is MemoryWindow memory)
		{
			if (_memoryCloseTask is null || _memoryCloseTask.IsCompleted)
				_memoryCloseTask = CloseMemoryAsync(memory);
			return;
		}
		_windows.Remove(label);
		if (window is NoriWindow nw) nw.AllowClose = true;
		else if (window is SettingsWindow settings) settings.AllowClose = true;
		else if (window is PetWindow pw)
		{
			pw.AllowClose = true;
			if (ReferenceEquals(_petWindow, pw)) _petWindow = null;
		}
		window.Close();
		if (_visible.TryUpdate(label, false, true)) VisibilityChanged?.Invoke(label, false);
	}

	private async Task CloseMemoryAsync(MemoryWindow memory)
	{
		try { await memory.PrepareShutdownAsync(); }
		catch (Exception exception)
		{
			_services?.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", $"记忆窗口关闭前保存失败: {exception.GetType().Name}");
			ShowMemoryFailure(memory, exception);
			return;
		}
		if (!ReferenceEquals(Get(WindowLabels.Memory), memory)) return;
		_windows.Remove(WindowLabels.Memory);
		memory.AllowClose = true;
		memory.Close();
		if (_visible.TryUpdate(WindowLabels.Memory, false, true)) VisibilityChanged?.Invoke(WindowLabels.Memory, false);
	}

	private static void ShowMemoryFailure(MemoryWindow memory, Exception exception)
	{
		memory.ReportHostFailure(exception);
		if (memory.WindowState == WindowState.Minimized) memory.WindowState = WindowState.Normal;
		memory.Show();
		memory.Activate();
	}

	/// <summary>
	/// 切换伴侣视窗显示状态
	/// </summary>
	public void TogglePet()
	{
		if (Get(WindowLabels.Pet) is not { } pet) return;
		if (pet.IsVisible) pet.Hide();
		else Show(WindowLabels.Pet);
	}

	/// <inheritdoc />
	public void ShowPetSpeech(string text) => _petWindow?.ShowSpeech(text);

	/// <inheritdoc />
	public void ClearPetSpeech() => _petWindow?.ClearSpeech();

	/// <summary>
	/// 向所有 WebView2 窗口广播事件
	/// </summary>
	public void Broadcast(string name, object? payload)
	{
		foreach (Window window in _windows.Values)
		{
			if (window is NoriWindow noriWindow)
			{
				noriWindow.PostEvent(name, payload);
			}
		}
	}

	/// <summary>
	/// 退出应用
	///
	/// 托盘菜单与桥接命令可能在关闭回调或后台线程中触发退出。统一延迟到 UI 线程执行,
	/// 并在真正关闭前放行所有受管窗口, 避免窗口关闭处理器把退出请求变成隐藏窗口。
	/// </summary>
	public void Shutdown()
	{
		if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0) return;

		Dispatcher.UIThread.Post(async () =>
		{
			try
			{
				if (_memoryCloseTask is { } closingMemory) await closingMemory;
				MemoryWindow? memory = Get(WindowLabels.Memory) as MemoryWindow;
				SettingsWindow? settings = Get(WindowLabels.Settings) as SettingsWindow;
				// 先验证所有编辑能够保存，再开始不可逆的页面释放，保留失败重试机会。
				if (settings is not null && !await settings.FlushPendingSavesAsync())
					throw new InvalidOperationException("设置保存失败，请检查后重试");
				if (memory is not null && !await memory.FlushPendingSavesAsync())
					throw new InvalidOperationException("记忆保存失败，请检查后重试");
				if (memory is not null) await memory.PrepareShutdownAsync();
				if (settings is not null) await settings.PrepareShutdownAsync();
			}
			catch (Exception exception)
			{
				Interlocked.Exchange(ref _shutdownRequested, 0);
				_services?.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", $"窗口关闭前保存失败，已取消退出: {exception.GetType().Name}");
				if (Get(WindowLabels.Memory) is MemoryWindow memory) ShowMemoryFailure(memory, exception);
				return;
			}
			foreach (Window window in _windows.Values)
			{
				if (window is NoriWindow noriWindow) noriWindow.AllowClose = true;
				else if (window is SettingsWindow settingsWindow) settingsWindow.AllowClose = true;
				else if (window is MemoryWindow memoryWindow) memoryWindow.AllowClose = true;
				else if (window is PetWindow petWindow) petWindow.AllowClose = true;
			}

			try
			{
				_lifetime.Shutdown(0);
			}
			catch (InvalidOperationException)
			{
				// 另一个退出请求已经进入 Avalonia 生命周期。
			}
		});
	}
}
