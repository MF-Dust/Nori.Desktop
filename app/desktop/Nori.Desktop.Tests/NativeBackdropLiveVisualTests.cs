using System.Text.Json;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Nori.Desktop.Appearance;
using Nori.Desktop.Automation.Windows;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[AttributeUsage(AttributeTargets.Method)]
	private sealed class NativeBackdropLiveFactAttribute : FactAttribute
	{
		public NativeBackdropLiveFactAttribute()
		{
			if (!System.OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("NORI_CAPTURE_BACKDROP") != "1")
				Skip = "设置 NORI_CAPTURE_BACKDROP=1，在独立测试进程中验收 Windows 系统背景模糊。";
		}
	}

	/// <summary>使用隔离配置和高对比背景，真实截图覆盖模糊、关闭、普通透明回退及实际界面。</summary>
	[NativeBackdropLiveFact]
	public async Task NativeBackdropLiveCapture()
	{
		if (!System.OperatingSystem.IsWindows()) return;
		string output = Environment.GetEnvironmentVariable("NORI_BACKDROP_CAPTURE_DIR")
			?? throw new InvalidOperationException("请指定 NORI_BACKDROP_CAPTURE_DIR 截图目录。");
		Directory.CreateDirectory(output);
		using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(90));
		TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Thread thread = new(() =>
		{
			try
			{
				AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
				Dispatcher.UIThread.Post(async () =>
				{
					try { await CaptureBackdropAsync(output, cancellation.Token); completion.TrySetResult(); }
					catch (Exception exception) { completion.TrySetException(exception); }
					finally { cancellation.Cancel(); }
				});
				try { Dispatcher.UIThread.MainLoop(cancellation.Token); }
				catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
			}
			catch (Exception exception) { completion.TrySetException(exception); cancellation.Cancel(); }
		}) { IsBackground = true, Name = "Nori background capture" };
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		await completion.Task.WaitAsync(cancellation.Token);
		Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
	}

	private static async Task CaptureBackdropAsync(string output, CancellationToken cancellation)
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		using WindowBackdropController backdrop = new();
		List<object> manifest = [];
		DesktopBackdropCaptureNativeApi nativeCapture = new();
		BackdropFrame? patternFrame = null;
		Window pattern = new()
		{
			Title = "Nori visual verification background", Width = 1100, Height = 800,
			WindowDecorations = WindowDecorations.None, Position = new PixelPoint(20, 20),
			Content = new BackdropPattern(), ShowInTaskbar = false,
		};
		Window sample = new()
		{
			Title = "Nori backdrop verification", Width = 720, Height = 480,
			WindowDecorations = WindowDecorations.None, Position = new PixelPoint(80, 80),
		};
		MainWindow main = new(MainDefinition(), fixture._services) { Width = 720, Height = 480, WindowStartupLocation = WindowStartupLocation.Manual };
		SettingsWindow settings = new() { Width = 720, Height = 480, WindowStartupLocation = WindowStartupLocation.Manual };
		using SettingsService service = new(fixture._services, settings);
		using SettingsViewModel viewModel = new(service);
		settings.DataContext = viewModel;
		try
		{
			pattern.Show();
			await Capture(pattern, "pattern");
			backdrop.Register(sample);
			sample.Show();
			BackdropFrame enabledFrame = await Capture(sample, "surface-enabled");
			bool blurActive = WindowBackdropController.IsBlurActive(true, sample.ActualTransparencyLevel);
			if (Environment.GetEnvironmentVariable("NORI_REQUIRE_BLUR") == "1")
				Assert.True(WindowBackdropController.IsBlurActive(true, sample.ActualTransparencyLevel), "此桌面未提供原生背景模糊。");
			backdrop.SetEnabled(false);
			BackdropFrame disabledFrame = await Capture(sample, "surface-disabled");
			Assert.Equal(NoriThemeTokens.Brush("bg-base"), sample.Background);
			backdrop.SetEnabled(true);
			sample.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
			BackdropFrame unavailableFrame = await Capture(sample, "surface-unavailable");
			Assert.Equal(NoriThemeTokens.Brush("bg-base"), sample.Background);
			PixelPoint coralPoint = pattern.PointToScreen(new Point(220, 220));
			PixelPoint tealPoint = pattern.PointToScreen(new Point(600, 400));
			BackdropPixel enabledCoral = enabledFrame.Average(coralPoint);
			BackdropPixel enabledTeal = enabledFrame.Average(tealPoint);
			BackdropPixel disabledCoral = disabledFrame.Average(coralPoint);
			BackdropPixel disabledTeal = disabledFrame.Average(tealPoint);
			BackdropPixel unavailableCoral = unavailableFrame.Average(coralPoint);
			BackdropPixel unavailableTeal = unavailableFrame.Average(tealPoint);
			manifest.Add(new { pixelVerification = new { blurActive, enabledCoral, enabledTeal, disabledCoral, disabledTeal, unavailableCoral, unavailableTeal } });
			// 先保留证据，像素断言失败时仍可检查截图与实际 RGB。
			await WriteManifest();
			AssertSolidSurface(disabledCoral);
			AssertSolidSurface(disabledTeal);
			AssertSolidSurface(unavailableCoral);
			AssertSolidSurface(unavailableTeal);
			if (blurActive)
			{
				Assert.True(enabledCoral.Red - enabledTeal.Red >= 3,
					$"模糊状态未显示后方珊瑚色块的红色贡献: coral={enabledCoral}, teal={enabledTeal}");
				Assert.True(enabledTeal.Green - enabledTeal.Red > enabledCoral.Green - enabledCoral.Red + 3,
					$"模糊状态未显示后方青色色块的颜色差异: coral={enabledCoral}, teal={enabledTeal}");
			}
			else
			{
				AssertSolidSurface(enabledCoral);
				AssertSolidSurface(enabledTeal);
			}
			sample.Close();

			backdrop.Register(main);
			main.Position = new PixelPoint(80, 80);
			main.Show();
			await Capture(main, "main-enabled-720x480");
			backdrop.SetEnabled(false);
			await Capture(main, "main-disabled-720x480");
			Assert.Equal(WindowDecorations.None, main.WindowDecorations);
			Button zoom = main.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "WindowMaximize");
			zoom.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await Task.Delay(150, cancellation);
			Assert.Equal(WindowState.Maximized, main.WindowState);
			zoom.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await Task.Delay(150, cancellation);
			Assert.Equal(WindowState.Normal, main.WindowState);
			main.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "WindowMinimize").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await Task.Delay(150, cancellation);
			Assert.Equal(WindowState.Minimized, main.WindowState);
			main.WindowState = WindowState.Normal;
			main.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "WindowClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.False(main.IsVisible);
			backdrop.SetEnabled(true);
			backdrop.Register(settings);
			settings.Position = new PixelPoint(80, 80);
			settings.Show();
			viewModel.Navigate("general");
			await viewModel.RefreshSnapshotAsync();
			await Capture(settings, "settings-enabled-720x480");
			backdrop.SetEnabled(false);
			await Capture(settings, "settings-disabled-720x480");
			await WriteManifest();
		}
		finally
		{
			main.AllowClose = true;
			main.Close();
			settings.Close();
			sample.Close();
			pattern.Close();
		}

		Task WriteManifest() => File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), cancellation);

		async Task<BackdropFrame> Capture(Window window, string name)
		{
			window.Activate();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			await Task.Delay(700, cancellation);
			var capture = new WindowsScreenshotService(native: nativeCapture);
			Assert.True(capture.TryCapture(window.TryGetPlatformHandle()!.Handle,
				new WindowsScreenshotRequest(WindowsScreenshotFormat.Png, RequireForeground: true),
				out WindowsScreenshot? screenshot, out string? error), error);
			Assert.NotNull(screenshot);
			await File.WriteAllBytesAsync(Path.Combine(output, name + ".png"), screenshot.Data, cancellation);
			manifest.Add(new { name, actual = window.ActualTransparencyLevel.ToString(), screenshot.Width, screenshot.Height });
			await WriteManifest();
			BackdropFrame frame = Assert.IsType<BackdropFrame>(nativeCapture.Frame);
			if (ReferenceEquals(window, pattern)) patternFrame = frame;
			else
			{
				Assert.NotNull(patternFrame);
				Assert.True(frame.Bounds.Left >= patternFrame.Bounds.Left && frame.Bounds.Top >= patternFrame.Bounds.Top
					&& frame.Bounds.Right <= patternFrame.Bounds.Right && frame.Bounds.Bottom <= patternFrame.Bounds.Bottom,
					"测试窗口必须完整位于受控图案背景内。");
			}
			return frame;
		}
	}

	private static void AssertSolidSurface(BackdropPixel pixel)
	{
		Color expected = NoriThemeTokens.Color("bg-base");
		Assert.InRange(Math.Abs(pixel.Red - expected.R), 0, 2);
		Assert.InRange(Math.Abs(pixel.Green - expected.G), 0, 2);
		Assert.InRange(Math.Abs(pixel.Blue - expected.B), 0, 2);
	}

	private sealed record BackdropPixel(double Red, double Green, double Blue);

	private sealed record BackdropFrame(WindowsNativeRect Bounds, byte[] Pixels)
	{
		internal BackdropPixel Average(PixelPoint point)
		{
			const int radius = 8;
			int x = point.X - Bounds.Left, y = point.Y - Bounds.Top;
			Assert.InRange(x, radius, Bounds.Width - radius - 1);
			Assert.InRange(y, radius, Bounds.Height - radius - 1);
			long red = 0, green = 0, blue = 0;
			for (int dy = -radius; dy <= radius; dy++)
			for (int dx = -radius; dx <= radius; dx++)
			{
				int offset = ((y + dy) * Bounds.Width + x + dx) * 4;
				blue += Pixels[offset]; green += Pixels[offset + 1]; red += Pixels[offset + 2];
			}
			double count = (radius * 2 + 1) * (radius * 2 + 1);
			return new(red / count, green / count, blue / count);
		}
	}

	/// <summary>测试专用：从实际屏幕合成结果读取目标区域，避免 PrintWindow 遗漏桌面背景模糊。</summary>
	private sealed class DesktopBackdropCaptureNativeApi : IWindowsScreenCaptureNativeApi
	{
		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3898", Justification = "原生 ABI 结构体仅用于测试截图互操作。")]
		[StructLayout(LayoutKind.Sequential)]
		private struct Header
		{
			public uint Size;
			public int Width, Height;
			public ushort Planes, Bits;
			public uint Compression, ImageSize;
			public int XPels, YPels;
			public uint Used, Important;
		}
		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3898", Justification = "原生 ABI 结构体仅用于测试截图互操作。")]
		[StructLayout(LayoutKind.Sequential)] private struct Info { public Header Header; public uint Color; }
		[DllImport("user32.dll")] private static extern nint GetDC(nint window);
		[DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
		[DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
		[DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref Info info, uint usage, out nint bits, nint section, uint offset);
		[DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
		[DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
		[DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
		[DllImport("gdi32.dll")] private static extern bool GdiFlush();
		[DllImport("dwmapi.dll")] private static extern int DwmFlush();
		[DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

		internal BackdropFrame? Frame { get; private set; }

		public bool TryCaptureWindow(nint handle, WindowsNativeRect rect, out byte[]? bgra32, out string? error)
		{
			Frame = null; bgra32 = null; error = null;
			long bytes = (long)rect.Width * rect.Height * 4;
			if (rect.Width <= 0 || rect.Height <= 0 || bytes > int.MaxValue) { error = "测试截图区域无效"; return false; }
			nint screen = GetDC(0);
			if (screen == 0) { error = "无法读取实际桌面合成画面"; return false; }
			nint dc = 0, bitmap = 0, previous = 0;
			try
			{
				dc = CreateCompatibleDC(screen);
				if (dc == 0) { error = "无法创建测试截图设备上下文"; return false; }
				Info info = new() { Header = new() { Size = (uint)Marshal.SizeOf<Header>(), Width = rect.Width, Height = -rect.Height, Planes = 1, Bits = 32, ImageSize = (uint)bytes } };
				bitmap = CreateDIBSection(screen, ref info, 0, out nint bits, 0, 0);
				if (bitmap == 0 || bits == 0) { error = "无法创建测试截图像素缓冲区"; return false; }
				previous = SelectObject(dc, bitmap);
				if (previous == 0 || previous == new nint(-1)) { error = "无法绑定测试截图像素缓冲区"; return false; }
				DwmFlush();
				if (!BitBlt(dc, 0, 0, rect.Width, rect.Height, screen, rect.Left, rect.Top, 0x40CC0020))
				{
					error = $"读取实际屏幕区域失败: {Marshal.GetLastWin32Error()}";
					return false;
				}
				GdiFlush();
				bgra32 = new byte[(int)bytes];
				Marshal.Copy(bits, bgra32, 0, bgra32.Length);
				Frame = new(rect, bgra32);
				return true;
			}
			finally
			{
				if (previous != 0 && previous != new nint(-1)) SelectObject(dc, previous);
				if (bitmap != 0) DeleteObject(bitmap);
				if (dc != 0) DeleteDC(dc);
				ReleaseDC(0, screen);
			}
		}
	}

	/// <summary>合成背景仅含黑白棋盘与色块，不捕获用户桌面内容作为测试素材。</summary>
	private sealed class BackdropPattern : Control
	{
		public override void Render(DrawingContext context)
		{
			for (int y = 0; y < Bounds.Height; y += 16)
			for (int x = 0; x < Bounds.Width; x += 16)
				context.FillRectangle(((x + y) / 16) % 2 == 0 ? Brushes.White : Brushes.Black, new Rect(x, y, 16, 16));
			context.FillRectangle(Brushes.Coral, new Rect(120, 120, 200, 200));
			context.FillRectangle(Brushes.Teal, new Rect(500, 300, 200, 200));
		}
	}
}
