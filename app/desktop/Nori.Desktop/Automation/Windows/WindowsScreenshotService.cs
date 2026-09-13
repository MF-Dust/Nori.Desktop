using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Nori.Core.Automation;

namespace Nori.Desktop.Automation.Windows;

/// <summary>Per-Monitor DPI 感知的前台窗口区域截图服务。</summary>
public sealed class WindowsScreenshotService
{
	private readonly WindowsWindowService _windows;
	private readonly IWindowsScreenCaptureNativeApi _native;
	public WindowsScreenshotService(WindowsWindowService? windows = null, IWindowsScreenCaptureNativeApi? native = null) { _windows = windows ?? new(); _native = native ?? CreateNative(); }
	private static IWindowsScreenCaptureNativeApi CreateNative() { if (OperatingSystem.IsWindows()) return new Win32ScreenCaptureNativeApi(); return new UnsupportedCaptureNativeApi(); }
	public WindowsAutomationAvailability Availability => WindowsAutomationAvailability.Current;

	/// <summary>捕获并编码为内存中的 PNG/JPEG，不写入文件。</summary>
	public bool TryCapture(nint target, WindowsScreenshotRequest request, out WindowsScreenshot? screenshot, out string? error)
	{
		screenshot = null; error = null;
		if (!Availability.IsAvailable) { error = Availability.Reason; return false; }
		if (request.Quality is < 1 or > 100) { error = "截图质量必须在 1 到 100 之间"; return false; }
		WindowsTargetValidationResult validation = _windows.ValidateTarget(target, request.RequireForeground);
		if (!validation.IsValid) { error = validation.Reason; return false; }
		try
		{
			WindowsScreenshot? captured = null; string? captureError = null;
			bool ok = _windows.WithPerMonitorDpi(() => CaptureCore(target, request, out captured, out captureError));
			screenshot = captured; error = captureError; return ok;
		}
		catch (LimitException) { error = "截图编码结果超出大小限制"; return false; }
		catch (Exception exception) when (exception is InvalidOperationException or ExternalException) { error = exception.Message; return false; }
	}

	private bool CaptureCore(nint target, WindowsScreenshotRequest request, out WindowsScreenshot? screenshot, out string? error)
	{
		screenshot = null; error = null;
		if (!_windows.TryGetBounds(target, out WindowsNativeRect rect) || !AutomationCaptureLimits.TryGetRawByteCount(rect.Width, rect.Height, out int expected, out error)) return false;
		if (!_native.TryCaptureWindow(target, rect, out byte[]? pixels, out error) || pixels is null || pixels.Length != expected) { error ??= "截图原始像素大小不匹配"; return false; }
		GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
		try
		{
			using WriteableBitmap bitmap = new(PixelFormat.Bgra8888, AlphaFormat.Opaque, pin.AddrOfPinnedObject(), new PixelSize(rect.Width, rect.Height), new Vector(_windows.GetDpi(target), _windows.GetDpi(target)), rect.Width * 4);
			using LimitedStream output = new(AutomationCaptureLimits.MaxEncodedBytes);
			if (request.Format == WindowsScreenshotFormat.Png) bitmap.Save(output, PngBitmapEncoderOptions.Default);
			else { JpegBitmapEncoderOptions options = new() { Quality = request.Quality }; bitmap.Save(output, options); }
			screenshot = new(output.ToArray(), rect.Width, rect.Height, _windows.GetDpi(target), request.Format); return true;
		}
		finally { pin.Free(); }
	}

	private sealed class LimitException : IOException { }
	private sealed class LimitedStream(int max) : Stream
	{
		private readonly MemoryStream _inner = new();
		public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true; public override long Length => _inner.Length; public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
		public override void Flush() => _inner.Flush(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) { if (_inner.Length > max - count) throw new LimitException(); _inner.Write(buffer, offset, count); }
		public override void Write(ReadOnlySpan<byte> buffer) { if (_inner.Length > max - buffer.Length) throw new LimitException(); _inner.Write(buffer); }
		public byte[] ToArray() => _inner.ToArray(); protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
	}
}
