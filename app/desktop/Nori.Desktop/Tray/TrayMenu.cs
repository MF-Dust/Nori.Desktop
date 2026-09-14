using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Nori.Core.Configuration;
using Nori.Core.Live2D;
using Nori.Core.Logging;
using Nori.Core.Resources;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tray;

/// <summary>
/// 系统托盘
///
/// 对应 Rust 版 tray.rs. 托盘是唯一常驻的入口: 左键开主界面, 菜单切换 Nori 与退出.
/// </summary>
public static class TrayMenu
{
	/// <summary>
	/// 挂上托盘图标与菜单
	/// </summary>
	/// <summary>
	/// 装载托盘图标
	///
	/// 返回是否成功: 部分 Linux 桌面环境没有 StatusNotifier/AppIndicator, 托盘会静默不出现,
	/// 此时把 SupportsTray 置 false, 由前端在主窗内提供常驻入口与退出按钮。
	/// </summary>
	/// <summary>
	/// 当前托盘图标；托盘不可用或尚未安装时为 null。
	///
	/// 留引用是为了让情绪表达通道能换图标 —— 安装之后就再也拿不到它的话，托盘就只能是
	/// 一张静态图。
	/// </summary>
	public static TrayIcon? Current { get; private set; }

	/// <summary>
	/// 菜单项要跟着状态走, 所以留住引用。
	///
	/// 原来四条标题是在 Install 里一次性拼好的字符串: 语言改了不动, Nori 藏起来了也不动。
	/// </summary>
	private static NativeMenuItem? _toggleItem;
	private static NativeMenuItem? _mainItem;
	private static NativeMenuItem? _settingsItem;
	private static NativeMenuItem? _quitItem;
	private static TrayIcon? _icon;
	private static AppServices? _services;

	/// <summary>界面语言是不是英文。</summary>
	private static bool IsEnglish(AppServices services) =>
		services.Config.GetStringOr("language", "zh-CN") == "en-US";

	/// <summary>
	/// 显示 / 隐藏那一条的标题。
	///
	/// **写此刻点下去会发生什么, 不写这一条管什么。** 原来是一句「显示/隐藏 Nori」——
	/// 它把两个互斥的结果并排摆着, 用户点之前不知道会得到哪一个, 只能点一下试试。
	/// 菜单项是动词, 不是分类名。
	/// </summary>
	internal static string ToggleLabel(bool petVisible, bool english) => english
		? (petVisible ? "Hide Nori" : "Show Nori")
		: (petVisible ? "隐藏 Nori" : "显示 Nori");

	/// <summary>打开主界面。左键点托盘也是这个, 菜单里仍然要有 —— 不是每个人都会去试左键。</summary>
	internal static string MainLabel(bool english) => english ? "Open main window" : "打开主界面";

	internal static string SettingsLabel(bool english) => english ? "Settings" : "设置";

	/// <summary>退出。写清楚退的是谁 —— 托盘上可能还蹲着别的程序。</summary>
	internal static string QuitLabel(bool english) => english ? "Quit Nori" : "退出 Nori";

	internal static string Tooltip(bool english) => english
		? "Nori — desktop companion (click to open)"
		: "Nori — 桌面伴侣（点击打开主界面）";

	/// <summary>
	/// 按当前语言与 Nori 的显示状态刷新菜单标题。
	///
	/// 托盘菜单没有"即将打开"的跨平台回调, 所以不能等到用户右键时再算 —— 改为在状态
	/// 变化时推过去: 可见性由 VisibilityChanged 触发, 语言由设置保存后调这里。
	/// </summary>
	public static void Refresh()
	{
		if (_services is not {} services) return;
		bool english = IsEnglish(services);
		bool petVisible = services.Windows.IsWindowVisible(WindowLabels.Pet);
		if (_toggleItem is {} toggle) toggle.Header = ToggleLabel(petVisible, english);
		if (_mainItem is {} main) main.Header = MainLabel(english);
		if (_settingsItem is {} settings) settings.Header = SettingsLabel(english);
		if (_quitItem is {} quit) quit.Header = QuitLabel(english);
		if (_icon is {} icon) icon.ToolTipText = Tooltip(english);
	}

	public static bool Install(Application application, AppServices services)
	{
		services.Logger.Write(LogSource.Backend, "info", "初始化托盘菜单");
		_services = services;
		bool english = IsEnglish(services);

		/* ── 顺序按「多久用一次」排, 不按功能分类排 ──────────────────────────
		 * 显示 / 隐藏排第一: 它是托盘菜单唯一不能被左键代替的动作, 也是桌宠用得最多的
		 * 一个。设置排到分隔线之后 —— 它一天用不到一次, 却原来压在切换的上面。
		 *
		 * 退出单独隔一条线。原来它紧贴着上一条, 而上一条是常点的 —— 手一滑就退了,
		 * 而退出没有确认也没有撤销。 */
		NativeMenuItem toggle = _toggleItem = new(ToggleLabel(services.Windows.IsWindowVisible(WindowLabels.Pet), english));
		toggle.Click += (_, _) =>
		{
			services.Logger.Write(LogSource.Backend, "info", "托盘菜单：切换 Nori 显示");
			if (!services.Windows.IsWindowVisible(WindowLabels.Pet) && !CanShowPet(services))
			{
				services.Logger.Write(LogSource.Backend, "warn", "当前 Live2D 模型不可用, 已打开主界面等待重新导入");
				ShowMain(services);
				return;
			}
			services.Windows.TogglePet();
		};

		NativeMenuItem openMain = _mainItem = new(MainLabel(english));
		openMain.Click += (_, _) => ShowMain(services);

		NativeMenuItem openSettings = _settingsItem = new(SettingsLabel(english));
		openSettings.Click += (_, _) => services.Windows.ShowSettings();

		NativeMenuItem quit = _quitItem = new(QuitLabel(english));
		quit.Click += (_, _) =>
		{
			services.Logger.Write(LogSource.Backend, "info", "托盘菜单：退出应用");
			services.Windows.Shutdown();
		};

		TrayIcon tray = Current = _icon = new()
		{
			Icon = LoadIcon(),
			ToolTipText = Tooltip(english),
			Menu =
			[
				toggle,
				openMain,
				new NativeMenuItemSeparator(),
				openSettings,
				new NativeMenuItemSeparator(),
				quit,
			],
		};
		// 左键点击直接开主界面, 不弹菜单
		tray.Clicked += (_, _) => ShowMain(services);

		// Nori 被藏起来 / 放出来之后, 菜单上那条要跟着改口
		services.Windows.VisibilityChanged += (label, _) =>
		{
			if (label == WindowLabels.Pet) Refresh();
		};

		try
		{
			TrayIcon.SetIcons(application, [tray]);
		}
		catch (Exception exception)
		{
			// 托盘不是必需品: 失败只记日志, 由前端补一个内建入口
			services.Logger.Write(LogSource.Backend, "warn", $"托盘不可用, 将由主界面提供入口: {exception.Message}");
			Current = null;
			_icon = null;
			return false;
		}
		services.Logger.Write(LogSource.Backend, "info", "托盘菜单初始化完成");
		return true;
	}

	private static bool CanShowPet(AppServices services)
	{
		try
		{
			string? modelId = SupportedModelIds.Normalize(
				services.Config.GetStringOr(ConfigStore.KeySelectedModel, ""));
			return modelId is not null && services.Resources.IsInstalled(ResourceType.Live2D, modelId);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ResourceException)
		{
			return false;
		}
	}

	/// <summary>
	/// 显示主窗口
	/// </summary>
	private static void ShowMain(AppServices services)
	{
		services.Logger.Write(LogSource.Backend, "info", "托盘操作：已显示主窗口");
		services.Windows.Show(WindowLabels.Main);
	}

	/// <summary>
	/// 托盘图标, 缺失时返回 null (托盘会退化成无图标但仍可用)
	/// </summary>
	private static WindowIcon? LoadIcon()
	{
		try
		{
			return new WindowIcon(AssetLoader.Open(new Uri("avares://Nori.Desktop/Assets/icon.ico")));
		}
		catch (Exception exception) when (exception is FileNotFoundException or ArgumentException)
		{
			return null;
		}
	}
}
