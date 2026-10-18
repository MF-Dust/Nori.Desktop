using Nori.Core.Automation;
using Nori.Desktop.Automation.Windows;
using Nori.Desktop.Vision;

namespace Nori.Desktop.Tests;

/// <summary>
/// 读屏的目标选择。
///
/// 只测「截哪个窗口」，不测截屏本身 —— 后者要真实窗口句柄，在无头环境里做不了。
/// 选择逻辑与平台无关，因此这些用例在三个平台上都跑。
/// 目标选择恰恰是最容易错的一环：用户在聊天窗口里说「看看我的屏幕」时，前台窗口正是 Nori。
/// </summary>
public sealed class ScreenTargetPickerTests
{
	/// <summary>按 z 序（清单顺序即 `EnumWindows` 的回调顺序）构造候选窗口。</summary>
	private static WindowsTopLevelWindow? Pick(params (nint Handle, string Title, int ProcessId)[] windows) =>
		ScreenTargetPicker.Pick(
			[.. windows.Select(window => new WindowsTopLevelWindow(
				window.Handle, window.Title, window.ProcessId, new AutomationBounds(0, 0, 1920, 1080), 96, false))],
			Environment.ProcessId);

	/// <summary>
	/// 不能截到 Nori 自己。
	///
	/// 用户在聊天窗口里问「看看我的屏幕」时，前台窗口正是 Nori。直接取前台会让她截到自己，
	/// 然后对着自己的聊天记录作答 —— 功能看起来能跑，答案完全没用。
	/// 取 z 序最靠前的那个：那是被聊天窗口挡住、用户真正在看的。
	/// </summary>
	[Fact]
	public void 取z序上第一个别人的窗口()
	{
		WindowsTopLevelWindow? picked = Pick(
			(1, "Nori", Environment.ProcessId),
			(2, "Visual Studio Code", 4242),
			(3, "浏览器", 5252));

		Assert.Equal("Visual Studio Code", picked?.Title);
	}

	/// <summary>无标题窗口跳过：多是工具提示、输入法候选框一类的浮层，截了没有意义。</summary>
	[Fact]
	public void 跳过无标题的浮层()
	{
		WindowsTopLevelWindow? picked = Pick(
			(1, "", 4242),
			(2, "   ", 4242),
			(3, "Visual Studio Code", 4242));

		Assert.Equal("Visual Studio Code", picked?.Title);
	}

	[Fact]
	public void 显式扫描按物理坐标和遮挡顺序选择并排除自身()
	{
		WindowsTopLevelWindow[] windows =
		[
			new(1, "Nori", 100, new(-100, 0, 200, 200), 144, true),
			new(2, "前面的应用", 200, new(-100, 0, 100, 100), 144, false),
			new(3, "后面的应用", 300, new(-100, 0, 200, 200), 144, false),
		];
		Assert.Equal((nint)2, ScreenTargetPicker.PickAt(windows, -50, 50, 100)?.Handle);
		Assert.Equal((nint)3, ScreenTargetPicker.PickAt(windows, 0, 50, 100)?.Handle);
		Assert.Equal((nint)3, ScreenTargetPicker.PickAt(windows, -50, 100, 100)?.Handle);
		Assert.Null(ScreenTargetPicker.PickAt(windows, 100, 50, 100));
		Assert.Null(ScreenTargetPicker.PickAt(windows, -101, 50, 100));
	}

	[Fact]
	public void 没有可选窗口时选不出目标()
	{
		Assert.Null(Pick((1, "Nori", Environment.ProcessId)));
		Assert.Null(Pick());
	}

}
