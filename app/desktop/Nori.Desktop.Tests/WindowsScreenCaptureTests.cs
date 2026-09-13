using Nori.Core.Vision;
using Nori.Desktop.Automation.Windows;
using Nori.Desktop.Vision;

namespace Nori.Desktop.Tests;

/// <summary>
/// Windows 读屏的目标选择。
///
/// 这一族只测「截哪个窗口」，不测截屏本身 —— 后者要真实窗口句柄，在无头环境里做不了。
/// 目标选择恰恰是最容易错的一环：用户在聊天窗口里说「看看我的屏幕」时，前台窗口正是 Nori。
/// </summary>
public sealed class WindowsScreenCaptureTests
{
	/// <summary>按给定窗口清单作答的假 Win32 层。</summary>
	private sealed class FakeWindows(params (nint Handle, string Title, int ProcessId)[] windows) : IWindowsWindowNativeApi
	{
		public bool TryEnumerateTopLevelWindows(Func<nint, bool> callback)
		{
			// EnumWindows 按 z 序从上到下回调，清单顺序即 z 序。
			foreach ((nint handle, _, _) in windows)
			{
				if (!callback(handle)) break;
			}

			return true;
		}

		public bool IsWindow(nint handle) => true;

		public bool IsWindowVisible(nint handle) => true;

		public nint GetRootWindow(nint handle) => handle;

		public string GetWindowTitle(nint handle) => Find(handle).Title;

		public int GetProcessId(nint handle) => Find(handle).ProcessId;

		public nint GetForegroundWindow() => windows.Length > 0 ? windows[0].Handle : 0;

		public bool TryGetWindowRect(nint handle, out WindowsNativeRect rect)
		{
			rect = new WindowsNativeRect(0, 0, 1920, 1080);
			return true;
		}

		public uint GetDpiForWindow(nint handle) => 96;

		public bool IsSecureDesktop() => false;

		public bool IsUipiAllowed(nint handle) => true;

		public nint SetThreadDpiAwarenessContext(nint context) => context;

		private (nint Handle, string Title, int ProcessId) Find(nint handle) =>
			windows.First(window => window.Handle == handle);
	}

	private static WindowsTopLevelWindow? Pick(params (nint, string, int)[] windows)
	{
		WindowsWindowService service = new(new FakeWindows(windows));
		return WindowsScreenCapture.PickTarget(service, Environment.ProcessId);
	}

	/// <summary>
	/// 不能截到 Nori 自己。
	///
	/// 用户在聊天窗口里问「看看我的屏幕」时，前台窗口正是 Nori。直接取前台会让她截到自己，
	/// 然后对着自己的聊天记录作答 —— 功能看起来能跑，答案完全没用。
	/// </summary>
	[Fact]
	public void 跳过本进程的窗口()
	{
		WindowsTopLevelWindow? picked = Pick(
			(1, "Nori", Environment.ProcessId),
			(2, "Visual Studio Code", 4242));

		Assert.Equal("Visual Studio Code", picked?.Title);
	}

	/// <summary>取 z 序最靠前的那个：那是被聊天窗口挡住、用户真正在看的。</summary>
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
	public void 只有自己的窗口时选不出目标()
	{
		Assert.Null(Pick((1, "Nori", Environment.ProcessId)));
	}

	[Fact]
	public void 一个窗口都没有时选不出目标()
	{
		Assert.Null(Pick());
	}

	/// <summary>非 Windows 上整条能力不可用，调用方据此不注册工具。</summary>
	[Fact]
	public void 平台不支持时报告不可用()
	{
		if (OperatingSystem.IsWindows()) return;

		Assert.False(new WindowsScreenCapture().IsAvailable);
	}
}
