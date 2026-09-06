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
/// 包含三个 WebView2 窗口 (first-run, init, main) 与一个原生 OpenGL 伴侣视窗 (pet)。
/// </summary>
public sealed class WindowManager(AssetServer assetServer, IClassicDesktopStyleApplicationLifetime lifetime, AppStoragePaths storagePaths) : IWindowManager
{
	private readonly AssetServer _assetServer = assetServer;
	private readonly AppStoragePaths _storagePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));
	private readonly IClassicDesktopStyleApplicationLifetime _lifetime = lifetime;
	private readonly Dictionary<string, Window> _windows = [];
	private readonly ConcurrentDictionary<string, bool> _visible = new();
	private PetWindow? _petWindow;
	private int _shutdownRequested;

	/// <inheritdoc />
	public event Action<string, bool>? VisibilityChanged;

	/// <summary>
	/// 建好全部窗口 (不显示)
	/// </summary>
	public void CreateAll(NoriBridge bridge, AppServices services)
	{
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
		_windows.Remove(label);
		if (window is NoriWindow nw) nw.AllowClose = true;
		else if (window is PetWindow pw)
		{
			pw.AllowClose = true;
			if (ReferenceEquals(_petWindow, pw)) _petWindow = null;
		}
		window.Close();
		if (_visible.TryUpdate(label, false, true)) VisibilityChanged?.Invoke(label, false);
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

		Dispatcher.UIThread.Post(() =>
		{
			foreach (Window window in _windows.Values)
			{
				if (window is NoriWindow noriWindow) noriWindow.AllowClose = true;
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
