using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nori.Desktop.QuickChat;
using Nori.Core.Assets;
using Nori.Core.Data;
using Nori.Desktop.Bridge;
using Nori.Desktop.Appearance;
using Nori.Core.Configuration;

namespace Nori.Desktop.Windows;

/// <summary>
/// 窗口调度
///
/// 承接原来 Rust 侧 lib.rs setup / tray.rs 与前端 services/window/index.ts 的窗口调度职责.
/// 用户窗口都是原生的。兼容音频宿主另行创建，不进入这个列表。
/// </summary>
public sealed class WindowManager : IWindowManager
{
	private readonly AssetServer _assetServer;
	private readonly AppStoragePaths _storagePaths;
	private readonly Action<int> _shutdown;
	private readonly Dictionary<string, Window> _windows = [];
	private readonly ConcurrentDictionary<string, bool> _visible = new();
	private PetWindow? _petWindow;
	private QuickChatController? _quickChat;
	private NoriWindow? _audioHost;
	private AppServices? _services;
	private WindowBackdropController? _backdrops;
	private int _shutdownRequested;
	private Task? _memoryCloseTask;
	private Task? _modelsCloseTask;
	private Task? _chatCloseTask;

	/// <summary>生产入口仍由 Avalonia 生命周期执行最终退出。</summary>
	public WindowManager(AssetServer assetServer, IClassicDesktopStyleApplicationLifetime lifetime, AppStoragePaths storagePaths)
		: this(assetServer, lifetime.Shutdown, storagePaths)
	{
	}

	/// <summary>隔离最终退出动作，生命周期测试不实现 Avalonia 私有接口，也不终止共享 UI 会话。</summary>
	internal WindowManager(AssetServer assetServer, Action<int> shutdown, AppStoragePaths storagePaths)
	{
		_assetServer = assetServer;
		_storagePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));
		_shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
	}

	/// <inheritdoc />
	public event Action<string, bool>? VisibilityChanged;

	/// <summary>
	/// 建好全部窗口 (不显示)
	/// </summary>
	public void CreateAll(NoriBridge bridge, AppServices services)
	{
		_services = services;
		_backdrops = new WindowBackdropController();
		_ = LoadBackdropPreferenceAsync(services);
		foreach (WindowDefinition definition in WindowDefinition.All)
		{
			if (definition.Label == WindowLabels.Main)
			{
				// 主界面也原生了：迁移的最后一块，WebView 在主路径上就此退出。
				MainWindow mainWindow = new(definition, services);
				_windows[definition.Label] = mainWindow;
			}
			else if (definition.Label == WindowLabels.FirstRun)
			{
				// 首次运行向导也已经是原生的：和初始化窗口一样不碰音频。
				_windows[definition.Label] = new FirstRunWindow(definition, services);
			}
			else if (definition.Label == WindowLabels.Init)
			{
				// 初始化窗口已经是原生的：它自足，不碰音频也不碰插件，迁过来之后
				// 启动路径上少一次 WebView 冷启动。
				InitWindow initWindow = new(definition, services);
				_windows[definition.Label] = initWindow;
			}
			else if (definition.Label == WindowLabels.Pet)
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
				throw new InvalidOperationException($"窗口 {definition.Label} 没有原生实现");
			}

			TrackVisibility(definition.Label, _windows[definition.Label]);
		}
		CreateAudioHost(bridge, services);
	}

	/// <summary>仅兼容后端创建专用宿主，不进入用户窗口列表或导航。</summary>
	internal void CreateAudioHost(NoriBridge bridge, AppServices services, Func<WindowDefinition, NoriWindow>? createWindow = null)
	{
		if (Nori.Core.Voice.Audio.AudioBackend.PrefersNative(
			services.Config.GetStringOr(Nori.Core.Configuration.ConfigStore.KeyAudioBackend, Nori.Core.Voice.Audio.AudioBackend.Auto),
			OperatingSystem.IsWindows()) || _audioHost is not null) return;
		WindowDefinition definition = new()
		{
			Label = WindowLabels.AudioHost,
			Title = "Nori Audio",
			Width = 1,
			Height = 1,
			ShowInTaskbar = false,
		};
		// 允许测试替换原生控件挂接边界，宿主选择、显示与通道生命周期仍走真实实现。
		_audioHost = createWindow is null
			? new NoriWindow(definition, bridge, _assetServer.WindowUrl(WindowLabels.AudioHost), _storagePaths)
			: createWindow(definition);
		_audioHost.StartAudioHost();
	}

	/// <summary>
	/// 跟踪一个窗口的可见性
	///
	/// 直接监听 IsVisibleProperty, 无论是托盘切换、命令调用还是窗口自己 Hide,
	/// 缓存与事件都不会漏.
	/// </summary>
	private void TrackVisibility(string label, Window window)
	{
		_backdrops?.Register(window);
		window.AddHandler(InputElement.KeyDownEvent, OnQuickChatShortcut, RoutingStrategies.Tunnel);
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
	public NoriWindow? GetNoriWindow(string? label) => label == WindowLabels.AudioHost ? _audioHost : Get(label) as NoriWindow;

	/// <summary>
	/// 原生伴侣视窗引用
	/// </summary>
	public PetWindow? Pet => _petWindow;

	/// <summary>
	/// 全部窗口
	/// </summary>
	public IEnumerable<Window> All => _windows.Values;

	/// <inheritdoc />
	public void UpdateBackgroundBlurEnabled(bool enabled)
	{
		Dispatcher.UIThread.VerifyAccess();
		_backdrops?.SetEnabled(enabled);
	}

	private async Task LoadBackdropPreferenceAsync(AppServices services)
	{
		try
		{
			if (_backdrops is { } backdrops)
				await backdrops.InitializeAsync(() => services.Config.GetBoolOr(ConfigStore.KeyBackgroundBlurEnabled, true));
		}
		catch (Exception exception)
		{
			services.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", $"读取窗口外观设置失败: {exception.GetType().Name}");
		}
	}

	/// <summary>
	/// 显示窗口；伴侣视窗不抢焦点，其他窗口同时聚焦
	/// </summary>
	public void Show(string label)
	{
		if (label == WindowLabels.Pet) EnsureQuickChat();
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
		if (label == WindowLabels.Models)
		{
			ShowModels();
			return;
		}
		if (label == WindowLabels.Chat)
		{
			ShowChat();
			return;
		}
		if (Get(label) is not { } window) return;
		window.Show();
		if (window is QuickChatWindow) return;
		if (window is PetWindow pet)
		{
			// 伴侣视窗不抢当前应用焦点；点击穿透由分层样式实现，窗口保持置顶。
			pet.ApplyWindowSize();
			pet.ReapplyInputState();
			return;
		}
		window.Activate();
	}

	private void EnsureQuickChat()
	{
		if (_quickChat is not null || _petWindow is null || _services?.Runtime is null) return;
		_quickChat = new QuickChatController(_services, _petWindow, window =>
		{
			_windows[WindowLabels.QuickChat] = window;
			TrackVisibility(WindowLabels.QuickChat, window);
		}, window =>
		{
			if (ReferenceEquals(Get(WindowLabels.QuickChat), window)) _windows.Remove(WindowLabels.QuickChat);
		});
	}

	private void OnQuickChatShortcut(object? sender, KeyEventArgs args)
	{
		KeyModifiers modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
		if (args.Key == Key.K && args.KeyModifiers == modifier && _quickChat?.FocusComposer() == true) args.Handled = true;
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

	/// <inheritdoc />
	public void ShowModels()
	{
		Dispatcher.UIThread.VerifyAccess();
		if (Volatile.Read(ref _shutdownRequested) != 0) return;
		if (_modelsCloseTask is {IsCompleted: false}) return;
		if (Get(WindowLabels.Models) is not ModelsWindow models)
		{
			AppServices services = _services ?? throw new InvalidOperationException("应用窗口尚未就绪");
			models = new ModelsWindow(services);
			_windows[WindowLabels.Models] = models;
			TrackVisibility(WindowLabels.Models, models);
		}
		if (models.WindowState == WindowState.Minimized) models.WindowState = WindowState.Normal;
		models.Show();
		models.Activate();
		_ = RefreshModelsAsync(models);
	}

	/// <inheritdoc />
	public void ShowChat()
	{
		Dispatcher.UIThread.VerifyAccess();
		if (Volatile.Read(ref _shutdownRequested) != 0 || _chatCloseTask is { IsCompleted: false }) return;
		if (Get(WindowLabels.Chat) is not ChatWindow chat)
		{
			AppServices services = _services ?? throw new InvalidOperationException("应用窗口尚未就绪");
			chat = new ChatWindow(services);
			_windows[WindowLabels.Chat] = chat;
			TrackVisibility(WindowLabels.Chat, chat);
		}
		if (chat.WindowState == WindowState.Minimized) chat.WindowState = WindowState.Normal;
		chat.Show();
		chat.Activate();
	}

	private async Task RefreshModelsAsync(ModelsWindow models)
	{
		try { await models.RefreshAsync(); }
		catch (OperationCanceledException) { }
		catch (Exception exception)
		{
			_services?.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", $"模型窗口刷新失败: {exception.GetType().Name}");
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
		if (window is ModelsWindow models)
		{
			if (_modelsCloseTask is null || _modelsCloseTask.IsCompleted)
				_modelsCloseTask = CloseModelsAsync(models);
			return;
		}
		if (window is ChatWindow chat)
		{
			if (_chatCloseTask is null || _chatCloseTask.IsCompleted)
				_chatCloseTask = CloseChatAsync(chat);
			return;
		}
		_windows.Remove(label);
		if (window is NoriWindow nw) nw.AllowClose = true;
		else if (window is InitWindow init) init.AllowClose = true;
		else if (window is FirstRunWindow firstRun) firstRun.AllowClose = true;
		else if (window is MainWindow main) main.AllowClose = true;
		else if (window is SettingsWindow settings) settings.AllowClose = true;
		else if (window is PetWindow pw)
		{
			pw.AllowClose = true;
			if (ReferenceEquals(_petWindow, pw)) _petWindow = null;
		}
		window.Close();
		if (_visible.TryUpdate(label, false, true)) VisibilityChanged?.Invoke(label, false);
	}

	private async Task CloseChatAsync(ChatWindow chat)
	{
		try { await chat.PrepareShutdownAsync(); }
		catch (Exception exception)
		{
			_services?.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", $"对话窗口关闭失败: {exception.GetType().Name}");
			chat.ReportHostFailure(exception);
			return;
		}
		if (!ReferenceEquals(Get(WindowLabels.Chat), chat)) return;
		_windows.Remove(WindowLabels.Chat);
		chat.AllowClose = true;
		chat.Close();
		if (_visible.TryUpdate(WindowLabels.Chat, false, true)) VisibilityChanged?.Invoke(WindowLabels.Chat, false);
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

	private async Task CloseModelsAsync(ModelsWindow models)
	{
		try { await models.PrepareShutdownAsync(); }
		catch (Exception exception)
		{
			_services?.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", $"模型窗口关闭前保存失败: {exception.GetType().Name}");
			ShowModelsFailure(models, exception);
			return;
		}
		if (!ReferenceEquals(Get(WindowLabels.Models), models)) return;
		_windows.Remove(WindowLabels.Models);
		models.AllowClose = true;
		models.Close();
		if (_visible.TryUpdate(WindowLabels.Models, false, true)) VisibilityChanged?.Invoke(WindowLabels.Models, false);
	}

	private static void ShowModelsFailure(ModelsWindow models, Exception exception)
	{
		models.ReportHostFailure(exception);
		if (models.WindowState == WindowState.Minimized) models.WindowState = WindowState.Normal;
		models.Show();
		models.Activate();
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
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1854", Justification = "关闭流程使用受控异步任务并显式处理生命周期结果。")]
	public void Shutdown()
	{
		if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0) return;

		Dispatcher.UIThread.Post(async () =>
		{
			Window? failureOwner = null;
			try
			{
				if (_memoryCloseTask is { } closingMemory) await closingMemory;
				if (_modelsCloseTask is { } closingModels) await closingModels;
				if (_chatCloseTask is { } closingChat) await closingChat;
				MemoryWindow? memory = Get(WindowLabels.Memory) as MemoryWindow;
				ModelsWindow? models = Get(WindowLabels.Models) as ModelsWindow;
				SettingsWindow? settings = Get(WindowLabels.Settings) as SettingsWindow;
				// 先验证所有编辑能够保存，再开始不可逆的页面释放，保留失败重试机会。
				failureOwner = settings;
				if (settings is not null && !await settings.FlushPendingSavesAsync())
					throw new InvalidOperationException("设置保存失败，请检查后重试");
				failureOwner = memory;
				if (memory is not null && !await memory.FlushPendingSavesAsync())
					throw new InvalidOperationException("记忆保存失败，请检查后重试");
				failureOwner = models;
				if (models is not null && !await models.FlushPendingSavesAsync())
					throw new InvalidOperationException("模型保存失败，请检查后重试");
				// 录音停止可能失败；先预检，避免失败时其他原生页面已被释放。
				failureOwner = Get(WindowLabels.Chat);
				if (failureOwner is ChatWindow recordingChat) await recordingChat.Body.PrepareHideAsync();
				failureOwner = memory;
				if (memory is not null) await memory.PrepareShutdownAsync();
				failureOwner = settings;
				if (settings is not null) await settings.PrepareShutdownAsync();
				// 模型窗口最后释放；此前任一窗口失败时，模型编辑上下文仍可复用。
				failureOwner = models;
				if (models is not null) await models.PrepareShutdownAsync();
				failureOwner = Get(WindowLabels.Chat);
				if (failureOwner is ChatWindow chat) await chat.PrepareShutdownAsync();
				if (_quickChat is not null) await _quickChat.ShutdownAsync();
			}
			catch (Exception exception)
			{
				Interlocked.Exchange(ref _shutdownRequested, 0);
				_services?.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", $"窗口关闭前保存失败，已取消退出: {exception.GetType().Name}");
				if (failureOwner is ChatWindow chat) chat.ReportHostFailure(exception);
				else if (failureOwner is ModelsWindow models) ShowModelsFailure(models, exception);
				else if (Get(WindowLabels.Memory) is MemoryWindow memory) ShowMemoryFailure(memory, exception);
				return;
			}
			foreach (Window window in _windows.Values)
			{
				if (window is NoriWindow noriWindow) noriWindow.AllowClose = true;
				else if (window is InitWindow initWindow) initWindow.AllowClose = true;
				else if (window is FirstRunWindow firstRunWindow) firstRunWindow.AllowClose = true;
				else if (window is MainWindow mainWindow) mainWindow.AllowClose = true;
				else if (window is SettingsWindow settingsWindow) settingsWindow.AllowClose = true;
				else if (window is MemoryWindow memoryWindow) memoryWindow.AllowClose = true;
				else if (window is ModelsWindow modelsWindow) modelsWindow.AllowClose = true;
				else if (window is ChatWindow chatWindow) chatWindow.AllowClose = true;
				else if (window is PetWindow petWindow) petWindow.AllowClose = true;
			}

			_backdrops?.Dispose();
			_backdrops = null;
			if (_audioHost is not null)
			{
				_audioHost.AllowClose = true;
				_audioHost.Close();
				_audioHost = null;
			}

			try
			{
				_shutdown(0);
			}
			catch (InvalidOperationException)
			{
				// 另一个退出请求已经进入 Avalonia 生命周期。
			}
		});
	}
}
