using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nori.Desktop.Automation.Windows;

/// <summary>PrintWindow/GDI DIB 截图封装。</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32ScreenCaptureNativeApi : IWindowsScreenCaptureNativeApi
{
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3898", Justification = "原生 ABI 结构体仅用于互操作，不参与相等比较。")]
	[StructLayout(LayoutKind.Sequential)] private struct Header { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important; }
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3898", Justification = "原生 ABI 结构体仅用于互操作，不参与相等比较。")]
	[StructLayout(LayoutKind.Sequential)] private struct Info { public Header Header; public uint Color; }
	[DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
	[DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref Info info, uint usage, out nint bits, nint section, uint offset);
	[DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
	[DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
	[DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
	[DllImport("user32.dll")] private static extern nint GetWindowDC(nint handle);
	[DllImport("user32.dll")] private static extern int ReleaseDC(nint handle, nint dc);
	[DllImport("user32.dll")] private static extern bool PrintWindow(nint handle, nint dc, uint flags);
	[DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);

	public bool TryCaptureWindow(nint handle, WindowsNativeRect rect, out byte[]? bgra32, out string? error)
	{
		bgra32 = null; error = null; long bytes = (long)rect.Width * rect.Height * 4;
		if (rect.Width <= 0 || rect.Height <= 0 || bytes > int.MaxValue) { error = "目标窗口区域无效"; return false; }
		nint dc = CreateCompatibleDC(0); if (dc == 0) { error = "无法创建截图设备上下文"; return false; }
		nint bitmap = 0, previous = 0;
		try
		{
			Info info = new() { Header = new() { Size = (uint)Marshal.SizeOf<Header>(), Width = rect.Width, Height = -rect.Height, Planes = 1, Bits = 32, ImageSize = (uint)bytes } };
			bitmap = CreateDIBSection(0, ref info, 0, out nint bits, 0, 0); if (bitmap == 0 || bits == 0) { error = "无法创建截图像素缓冲区"; return false; }
			previous = SelectObject(dc, bitmap); if (previous == 0) { error = "无法绑定截图像素缓冲区"; return false; }
			bool captured = PrintWindow(handle, dc, 2);
			if (!captured)
			{
				nint windowDc = GetWindowDC(handle); if (windowDc == 0) { error = "目标窗口无法提供截图设备上下文"; return false; }
				try { captured = BitBlt(dc, 0, 0, rect.Width, rect.Height, windowDc, 0, 0, 0x40CC0020); }
				finally { ReleaseDC(handle, windowDc); }
			}
			if (!captured) { error = "Windows 拒绝读取目标窗口图像"; return false; }
			bgra32 = new byte[(int)bytes]; Marshal.Copy(bits, bgra32, 0, bgra32.Length); return true;
		}
		finally
		{
			if (previous != 0) SelectObject(dc, previous);
			if (bitmap != 0) DeleteObject(bitmap);
			DeleteDC(dc);
		}
	}
}
