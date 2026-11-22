using Nori.Core.Platform;
using Nori.Core.Vision;

namespace Nori.Desktop.Vision;

internal sealed class LinuxWaylandScreenCapture : IScreenCapture
{
	public bool IsAvailable => OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(LinuxScreenshotPortal.SessionBusAddress);

	public async Task<ScreenCaptureResult> CaptureAsync(CancellationToken cancellationToken)
	{
		try
		{
			string path = await LinuxScreenshotPortal.RequestFileAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				byte[] png = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
				return ScreenCaptureResult.Ok(ScreenJpegEncoder.EncodePng(png, "系统截屏"));
			}
			catch (ArgumentException) { return ScreenCaptureResult.Fail("截屏 Portal 返回的图片无法解码"); }
			finally { File.Delete(path); }
		}
		catch (InvalidOperationException exception) { return ScreenCaptureResult.Fail(exception.Message); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return ScreenCaptureResult.Fail("无法读取或删除截屏 Portal 的临时文件");
		}
	}
}
