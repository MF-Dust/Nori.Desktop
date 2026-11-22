using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Nori.Core.Automation;
using Nori.Core.Platform;
using Nori.Core.Vision;
using static Nori.Core.Platform.LinuxX11WindowProvider;

namespace Nori.Desktop.Automation.Desktop;

internal sealed class LinuxX11DesktopVisionWindowCatalog : IDesktopVisionWindowCatalog
{
	public IReadOnlyList<DesktopVisionWindow> Enumerate() => WithDisplay(display =>
	{
		nint foreground = GetForegroundWindow(display);
		return LinuxX11WindowProvider.Enumerate(display)
			.Select(window => new DesktopVisionWindow(window.Handle, window.Title, window.ProcessId, window.Bounds, window.Handle == foreground))
			.ToArray();
	});
}

internal sealed class LinuxX11DesktopVisionScreenshotSource : IDesktopVisionScreenshotSource
{
	public Task<DesktopVisionScreenshotResult> CaptureAsync(nint targetWindow, CancellationToken cancellationToken = default) =>
		Task.Run(() => CaptureWindow(targetWindow, cancellationToken), cancellationToken);

	private static DesktopVisionScreenshotResult CaptureWindow(nint targetWindow, CancellationToken cancellationToken)
	{
		(Frame? Frame, TargetStatus Status) captured;
		try { captured = WithDisplay(display => CaptureFrame(display, targetWindow, cancellationToken)); }
		catch (InvalidOperationException) { return DesktopVisionScreenshotResult.Failed; }
		if (captured.Status == TargetStatus.NotForeground) return DesktopVisionScreenshotResult.TargetNotForeground;
		if (captured.Frame is not { } frame) return DesktopVisionScreenshotResult.Failed;
		cancellationToken.ThrowIfCancellationRequested();
		return EncodeFrame(frame);
	}

	private static (Frame? Frame, TargetStatus Status) CaptureFrame(nint display, nint targetWindow, CancellationToken cancellationToken)
	{
		if (GetWindow(display, targetWindow) is not { } target) return (null, TargetStatus.Invalid);
		if (targetWindow != GetForegroundWindow(display)) return (null, TargetStatus.NotForeground);
		cancellationToken.ThrowIfCancellationRequested();
		Frame? frame;
		try { frame = Capture(display, target, limitForAutomation: true); }
		catch (InvalidOperationException) { frame = null; }
		TargetStatus status = ValidateTarget(targetWindow, GetBounds(display, targetWindow), GetForegroundWindow(display));
		return (status == TargetStatus.Valid ? frame : null, status);
	}

	private static unsafe DesktopVisionScreenshotResult EncodeFrame(Frame frame)
	{
		// 与 Windows 一样保留原始像素坐标，不使用读屏专用的缩放 JPEG。
		fixed (byte* pixels = frame.Bgra)
		{
			using Bitmap bitmap = new(PixelFormat.Bgra8888, AlphaFormat.Opaque, (nint)pixels,
				new PixelSize(frame.Width, frame.Height), new Vector(96, 96), frame.Width * 4);
			using MemoryStream output = new();
			bitmap.Save(output, PngBitmapEncoderOptions.Default);
			return output.Length <= AutomationCaptureLimits.MaxEncodedBytes
				? DesktopVisionScreenshotResult.Succeeded(new(output.ToArray(), "image/png"))
				: DesktopVisionScreenshotResult.Failed;
		}
	}
}

internal sealed class LinuxX11DesktopVisionActionExecutor : IDesktopVisionActionExecutor
{
	public Task<DesktopVisionActionResult> ExecuteAsync(
		nint targetWindow,
		AutomationAction action,
		AutomationPolicy policy,
		CancellationToken cancellationToken = default) =>
		Task.Run(() => LinuxX11Input.Execute(targetWindow, action, policy, cancellationToken) switch
		{
			LinuxX11Input.Status.Succeeded => DesktopVisionActionResult.Succeeded,
			LinuxX11Input.Status.TargetNotForeground => DesktopVisionActionResult.TargetNotForeground,
			_ => DesktopVisionActionResult.Failed,
		}, cancellationToken);
}
