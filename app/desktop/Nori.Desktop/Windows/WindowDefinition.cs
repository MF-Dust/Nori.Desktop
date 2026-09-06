using Avalonia;
using Avalonia.Media;

namespace Nori.Desktop.Windows;

/// <summary>
/// 窗口定义
///
/// 逐条对应原 tauri.conf.json 的 app.windows 配置.
/// 新增窗口要同时改这里、前端 WINDOW_ROUTES 与 router, 少一处窗口就会渲染成 /init.
/// </summary>
public sealed record WindowDefinition
{
	/// <summary>窗口标签, 与前端 WindowLabel 联合类型一致</summary>
	public required string Label { get; init; }

	/// <summary>窗口标题</summary>
	public required string Title { get; init; }

	/// <summary>宽度 (DIP, 与 Tauri 的逻辑像素同义)</summary>
	public required double Width { get; init; }

	/// <summary>高度 (DIP)</summary>
	public required double Height { get; init; }

	/// <summary>最小宽度</summary>
	public double? MinWidth { get; init; }

	/// <summary>最小高度</summary>
	public double? MinHeight { get; init; }

	/// <summary>是否可缩放</summary>
	public bool CanResize { get; init; }

	/// <summary>是否置顶</summary>
	public bool Topmost { get; init; }

	/// <summary>是否显示在任务栏</summary>
	public bool ShowInTaskbar { get; init; } = true;

	/// <summary>
	/// 四个窗口的完整定义
	///
	/// 全部无边框 + 透明 + 启动隐藏, 由窗口调度决定谁先显示
	/// </summary>
	public static IReadOnlyList<WindowDefinition> All { get; } =
	[
		new()
		{
			Label = WindowLabels.FirstRun,
			Title = "Nori Desktop Pet",
			Width = 720,
			Height = 480,
			CanResize = false,
		},
		new()
		{
			Label = WindowLabels.Init,
			Title = "Nori Desktop Pet",
			Width = 480,
			Height = 320,
			CanResize = false,
		},
		new()
		{
			Label = WindowLabels.Main,
			Title = "Nori Desktop Pet",
			Width = 960,
			Height = 640,
			MinWidth = 720,
			MinHeight = 480,
			CanResize = true,
		},
		new()
		{
			Label = WindowLabels.Pet,
			Title = "Nori",
			Width = 400,
			Height = 520,
			CanResize = false,
			Topmost = true,
			ShowInTaskbar = false,
		},
	];
}

/// <summary>
/// 窗口标签常量
/// </summary>
public static class WindowLabels
{
	/// <summary>首次运行向导</summary>
	public const string FirstRun = "first-run";

	/// <summary>初始化 (资源下载) 中转窗口</summary>
	public const string Init = "init";

	/// <summary>主界面</summary>
	public const string Main = "main";

	/// <summary>伴侣视窗</summary>
	public const string Pet = "pet";
}
