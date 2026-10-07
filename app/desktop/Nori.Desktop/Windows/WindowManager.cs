using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nori.Desktop.QuickChat;
using Nori.Desktop.Bridge;
using Nori.Desktop.Appearance;
using Nori.Core.Configuration;

namespace Nori.Desktop.Windows;

/// <summary>
/// 窗口调度。负责管理与调度所有原生窗口的生命周期与显示状态。
/// </summary>
public sealed class WindowManager : IWindowManager
{
	private readonly Action<int> _shutdown;
	private readonly Dictionary<string, Window> _windows = [];
	private readonly ConcurrentDictionary<string, bool> _visible = new();
	private PetWindow? _petWindow;
	private QuickChatController? _quickChat;
	private PluginPageController? _pluginPages;
	private AppServices? _services;
	private WindowBackdropController? _backdrops;
	private WindowCorners? _corners;
	private int _shutdownRequested;
	private Task? _memoryCloseTask;
	private Task? _modelsCloseTask;
	private Task? _chatCloseTask;

	/// <summary>
	/// 账户窗口。
	///
	/// 不放进 <c>_windows</c>：那张表里的窗口关掉只是隐藏，而账户窗口是一次性的 ——
	/// 登录完就该消失，下次打开应当是一份干净的表单，不是上次那份还留着邮箱与错误提示
	/// 的。留这个引用只为不让它开出第二扇。
	/// </summary>
	private AccountWindow? _accountWindow;

	/// <summary>云端同步窗口。和账户窗口一样是按需建、关掉即销毁的一次性窗口。</summary>
	private Account.CloudSyncWindow? _cloudSyncWindow;

	/// <summary>生产入口仍由 Avalonia 生命周期执行最终退出。</summary>
	public WindowManager(IClassicDesktopStyleApplicationLifetime lifetime)
		: this(lifetime.Shutdown)
	{
	}

	/// <summary>隔离最终退出动作，生命周期测试不实现 Avalonia 私有接口，也不终止共享 UI 会话。</summary>
	internal WindowManager(Action<int> shutdown)
	{
		_shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
	}

	/// <inheritdoc />
	public event Action<string, bool>? VisibilityChanged;

	/// <summary>当前活跃插件的页面元数据。</summary>
	public IReadOnlyList<PluginPageInfo> PluginPages => _pluginPages?.Pages ?? [];

	/// <summary>在 UI 线程打开或激活插件页面。</summary>
	public void OpenPluginPage(string pluginId, string pageId) =>
		(_pluginPages ?? throw new InvalidOperationException("插件页面宿主尚未就绪")).Open(pluginId, pageId);

	/// <summary>
	/// 建好全部窗口 (不显示)
	/// </summary>
	public void CreateAll(AppServices services)
	{
		_services = services;
		if (services.PluginRuntime is { } runtime)
			_pluginPages = new PluginPageController(runtime, exception => services.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", "插件页面资源释放失败", exception: exception));
		_backdrops = new WindowBackdropController();
		_corners = new WindowCorners();
		_ = LoadBackdropPreferenceAsync(services);
		foreach (WindowDefinition definition in WindowDefinition.All)
		{
			if (definition.Label == WindowLabels.Main)
			{
				// 主界面是原生窗口。
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
				// 初始化窗口自足，不碰音频也不碰插件。
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
			services.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", "读取窗口外观设置失败", exception: exception);
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
		if (page is not null && page is not ("ai" or "voice" or "proactive" or "skills" or "mcp" or "automation" or "plugins" or "general" or "account" or "updates" or "debug" or "about"))
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

	/// <inheritdoc />
	public void ShowAccount()
	{
		Dispatcher.UIThread.VerifyAccess();
		if (Volatile.Read(ref _shutdownRequested) != 0) return;

		if (_accountWindow is {} existing)
		{
			// 已经开着就把它带到前面。再开一扇会有两份表单各自持有一个验证码状态，
			// 而服务端那边只有一个。
			if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
			existing.Show();
			existing.Activate();
			return;
		}

		AppServices services = _services ?? throw new InvalidOperationException("应用窗口尚未就绪");
		Account.SignInCoordinator coordinator = services.SignIn;

		AccountWindow window = new(
			state => coordinator.SubmitAsync(state, services.ShutdownToken),
			_ =>
			{
				// 只负责把引用放掉。登录成功后的界面刷新（快照失效 + 托盘标题）挂在
				// SignInCoordinator.SignedIn 上 —— 见 AppServices.CreateSignIn，
				// 那里覆盖全部登录入口，不只这一扇窗。
				_accountWindow = null;
			},
			coordinator.Session.LastMethod);
		_accountWindow = window;
		window.Show();
		window.Activate();
	}

	/// <inheritdoc />
	public void ShowCloudSync()
	{
		Dispatcher.UIThread.VerifyAccess();
		if (Volatile.Read(ref _shutdownRequested) != 0) return;

		if (_cloudSyncWindow is {} existing)
		{
			if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
			existing.Show();
			existing.Activate();
			return;
		}

		AppServices services = _services ?? throw new InvalidOperationException("应用窗口尚未就绪");
		Account.CloudSyncWindow window = new(services.CloudSync, services.ShutdownToken);
		window.Closed += (_, _) => _cloudSyncWindow = null;
		_cloudSyncWindow = window;
		window.Show();
		window.Activate();
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
			_services?.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", "记忆窗口刷新失败", exception: exception);
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
			_services?.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", "模型窗口刷新失败", exception: exception);
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
		if (window is InitWindow init) init.AllowClose = true;
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
			_services?.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", "对话窗口关闭失败", exception: exception);
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
			_services?.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", "记忆窗口关闭前保存失败", exception: exception);
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
			_services?.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", "模型窗口关闭前保存失败", exception: exception);
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

	/// <summary>退出事件阻塞等待服务清理前，先解除插件页面的 UI 清理屏障。</summary>
	internal void ReleasePluginPages()
	{
		Dispatcher.UIThread.VerifyAccess();
		_pluginPages?.Dispose();
		_pluginPages = null;
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
				_services?.Logger.Write(Nori.Core.Logging.LogSource.Backend, "warn", "窗口关闭前保存失败，已取消退出", exception: exception);
				if (failureOwner is ChatWindow chat) chat.ReportHostFailure(exception);
				else if (failureOwner is ModelsWindow models) ShowModelsFailure(models, exception);
				else if (Get(WindowLabels.Memory) is MemoryWindow memory) ShowMemoryFailure(memory, exception);
				return;
			}
			foreach (Window window in _windows.Values)
			{
				if (window is InitWindow initWindow) initWindow.AllowClose = true;
				else if (window is FirstRunWindow firstRunWindow) firstRunWindow.AllowClose = true;
				else if (window is MainWindow mainWindow) mainWindow.AllowClose = true;
				else if (window is SettingsWindow settingsWindow) settingsWindow.AllowClose = true;
				else if (window is MemoryWindow memoryWindow) memoryWindow.AllowClose = true;
				else if (window is ModelsWindow modelsWindow) modelsWindow.AllowClose = true;
				else if (window is ChatWindow chatWindow) chatWindow.AllowClose = true;
				else if (window is PetWindow petWindow) petWindow.AllowClose = true;
			}

			ReleasePluginPages();
			_backdrops?.Dispose();
			_backdrops = null;
			_corners?.Dispose();
			_corners = null;

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
