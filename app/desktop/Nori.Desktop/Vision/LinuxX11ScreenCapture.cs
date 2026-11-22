using Nori.Core.Platform;
using Nori.Core.Vision;

namespace Nori.Desktop.Vision;

internal sealed class LinuxX11ScreenCapture : IScreenCapture
{
	public bool IsAvailable => OperatingSystem.IsLinux() && PlatformServices.Current.Session == SessionType.X11;

	public Task<ScreenCaptureResult> CaptureAsync(CancellationToken cancellationToken) => Task.Run(() =>
	{
		if (!IsAvailable) return ScreenCaptureResult.Fail("当前会话不支持 X11 截屏");
		try
		{
			VisionWindow? target = ScreenTargetPicker.Pick(LinuxX11WindowProvider.Enumerate(), Environment.ProcessId);
			cancellationToken.ThrowIfCancellationRequested();
			return target is null ? ScreenCaptureResult.Fail("没找到可以查看的窗口") : Capture(target);
		}
		catch (InvalidOperationException exception) { return ScreenCaptureResult.Fail(exception.Message); }
	}, cancellationToken);

	internal static ScreenCaptureResult Capture(VisionWindow target)
	{
		try
		{
			LinuxX11WindowProvider.Frame frame = LinuxX11WindowProvider.Capture(target);
			return ScreenCaptureResult.Ok(ScreenJpegEncoder.EncodeBgra(frame.Bgra, frame.Width, frame.Height, target.Title));
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException)
		{
			return ScreenCaptureResult.Fail($"X11 截屏失败：{exception.Message}");
		}
	}
}
