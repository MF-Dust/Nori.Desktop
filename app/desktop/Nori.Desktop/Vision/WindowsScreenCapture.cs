using System.Runtime.Versioning;
using Nori.Core.Vision;
using Nori.Desktop.Automation.Windows;

namespace Nori.Desktop.Vision;

/// <summary>
/// Windows 下的读屏实现，复用桌面自动化已有的截屏链路。
///
/// 不截 Nori 自己的窗口：用户在聊天窗口里说「看看我的屏幕」时，前台窗口恰恰是 Nori，直接取
/// 前台会截到她自己。因此取 z 序上第一个不属于本进程的可见窗口 —— 那正是被聊天窗口挡住、
/// 用户真正想让她看的那个。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenCapture : IScreenCapture
{
	private readonly WindowsWindowService _windows;
	private readonly WindowsScreenshotService _screenshots;

	/// <summary>创建读屏实现。</summary>
	public WindowsScreenCapture(WindowsWindowService? windows = null, WindowsScreenshotService? screenshots = null)
	{
		_windows = windows ?? new WindowsWindowService();
		_screenshots = screenshots ?? new WindowsScreenshotService(_windows);
	}

	/// <inheritdoc />
	public bool IsAvailable => OperatingSystem.IsWindows() && _windows.Availability.IsAvailable;

	/// <inheritdoc />
	public Task<ScreenCaptureResult> CaptureAsync(CancellationToken cancellationToken) => Task.Run(() =>
	{
		if (!IsAvailable) return ScreenCaptureResult.Fail(_windows.Availability.Reason);

		VisionWindow? target = ScreenTargetPicker.Pick(EnumerateWindows(_windows), Environment.ProcessId);
		cancellationToken.ThrowIfCancellationRequested();
		if (target is null) return ScreenCaptureResult.Fail("没找到可以查看的窗口");

		return Capture(target);
	}, cancellationToken);

	internal static IReadOnlyList<VisionWindow> EnumerateWindows(WindowsWindowService windows, bool perMonitorDpi = false)
	{
		IReadOnlyList<WindowsTopLevelWindow> targets = perMonitorDpi
			? windows.WithPerMonitorDpi(windows.EnumerateTopLevelWindows)
			: windows.EnumerateTopLevelWindows();
		return [.. targets.Select(window => new VisionWindow(window.Handle, window.Title, window.ProcessId, window.Bounds))];
	}

	/// <summary>截取用户明确选定的窗口，不改变自动读屏的选窗规则。</summary>
	internal ScreenCaptureResult Capture(VisionWindow target)
	{
		if (!IsAvailable) return ScreenCaptureResult.Fail(_windows.Availability.Reason);
		// 用户选定后遮罩成为前台，截图必须允许目标处于后台。
		if (!_screenshots.TryCapture(
			target.Handle,
			new WindowsScreenshotRequest(WindowsScreenshotFormat.Png, RequireForeground: false),
			out WindowsScreenshot? shot,
			out string? error) || shot is null)
		{
			return ScreenCaptureResult.Fail(error ?? "截屏失败");
		}

		try
		{
			return ScreenCaptureResult.Ok(ScreenJpegEncoder.EncodePng(shot.Data, target.Title, shot.Width));
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException)
		{
			return ScreenCaptureResult.Fail($"截图编码失败: {exception.Message}");
		}
	}
}
