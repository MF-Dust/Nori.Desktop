using System.Runtime.Versioning;
using Avalonia.Media.Imaging;
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
	/// <summary>交给模型的图片最长边。</summary>
	///
	/// <remarks>
	/// 视觉模型在这个量级上已经能读清界面文字，再大只是线性推高 token 成本。原始窗口常在
	/// 2K 以上，不缩的话一张图就能占掉一轮的大半预算。
	/// </remarks>
	public const int MaxWidth = 1280;

	/// <summary>JPEG 质量。界面截图以文字为主，再低会使小字失真。</summary>
	public const int JpegQuality = 75;

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
	public ScreenCaptureResult Capture()
	{
		if (!IsAvailable) return ScreenCaptureResult.Fail(_windows.Availability.Reason);

		WindowsTopLevelWindow? target = ScreenTargetPicker.Pick(_windows.EnumerateTopLevelWindows(), Environment.ProcessId);
		if (target is null) return ScreenCaptureResult.Fail("没找到可以查看的窗口");

		// 目标是被 Nori 挡住的那个窗口，按定义不是前台，所以不能要求前台。
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
			return ScreenCaptureResult.Ok(Downscale(shot, target.Title));
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException)
		{
			return ScreenCaptureResult.Fail($"截图编码失败: {exception.Message}");
		}
	}


	/// <summary>
	/// 缩到 <see cref="MaxWidth"/> 之内并转成 JPEG。
	///
	/// 用 <see cref="Bitmap.DecodeToWidth"/> 直接按目标宽度解码，而不是先解全图再缩 —— 2K 以上
	/// 的窗口全图解出来是几十 MB 的位图。源图本来就窄于上限时它不会放大。
	/// </summary>
	private static CapturedScreen Downscale(WindowsScreenshot shot, string title)
	{
		using MemoryStream source = new(shot.Data);
		using Bitmap decoded = shot.Width > MaxWidth
			? Bitmap.DecodeToWidth(source, MaxWidth, BitmapInterpolationMode.HighQuality)
			: new Bitmap(source);

		using MemoryStream encoded = new();
		decoded.Save(encoded, new JpegBitmapEncoderOptions {Quality = JpegQuality});

		return new CapturedScreen
		{
			Bytes = encoded.ToArray(),
			MimeType = "image/jpeg",
			Width = (int)decoded.PixelSize.Width,
			Height = (int)decoded.PixelSize.Height,
			Window = title,
		};
	}
}
