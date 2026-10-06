using Nori.Core.Platform;

namespace Nori.Core.Tests;

public sealed class UniDesktopIntegrationTests
{
	[Theory]
	[InlineData("accent")]
	[InlineData("wallpaper")]
	[InlineData("mediaStatus")]
	[InlineData("mediaInfo")]
	public async Task 原生只读接口返回结果或明确的会话错误(string operation)
	{
		UniDesktopIntegration integration = UniDesktopIntegration.Current;
		if (!integration.IsAvailable) return;
		Exception? error = await Task.Run(() => Record.Exception(() =>
		{
			switch (operation)
			{
				case "accent": integration.GetAccentColor(); break;
				case "wallpaper": integration.GetWallpaper(); break;
				case "mediaStatus": Assert.True(Enum.IsDefined(integration.GetMediaStatus())); break;
				case "mediaInfo": integration.GetMediaInfo(); break;
				default: throw new ArgumentOutOfRangeException(nameof(operation));
			}
		}));
		if (error is null) return;
		Assert.True(error is InvalidOperationException or PlatformNotSupportedException, error.ToString());
		Assert.Contains("UniDesktop", error.Message);
	}

	[Fact]
	public async Task 已准备的原生库可以通过CSharp绑定读取主题()
	{
		string? name = OperatingSystem.IsLinux() ? "libuda_ffi.so" : OperatingSystem.IsWindows() ? "uda_ffi.dll" : null;
		if (name is null || !File.Exists(Path.Combine(AppContext.BaseDirectory, name))) return;
		UniDesktopIntegration integration = new(AppContext.BaseDirectory);
		Assert.True(integration.IsAvailable);
		DesktopTheme theme = await Task.Run(integration.DetectTheme);
		Assert.True(Enum.IsDefined(theme));
		SessionCapabilities capabilities = await Task.Run(integration.GetSessionCapabilities);
		Assert.True(capabilities.HasFlag(SessionCapabilities.Management));
		Assert.True(capabilities.HasFlag(SessionCapabilities.Lock));
	}

	[Fact]
	public void 缺少原生库时明确降级且不会搜索系统库()
	{
		UniDesktopIntegration integration = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
		Assert.False(integration.IsAvailable);
		Assert.Throws<PlatformNotSupportedException>(() => integration.DetectTheme());
		Assert.Throws<PlatformNotSupportedException>(() => integration.GetWallpaper());
		Assert.Throws<PlatformNotSupportedException>(() => integration.Notify("标题", "正文"));
		Assert.Throws<PlatformNotSupportedException>(() => integration.GetAccentColor());
		Assert.Throws<PlatformNotSupportedException>(() => integration.GetMediaInfo());
		Assert.Throws<PlatformNotSupportedException>(() => integration.GetMediaStatus());
		Assert.Throws<PlatformNotSupportedException>(() => integration.SendMediaCommand(MediaCommand.Pause));
		Assert.Throws<PlatformNotSupportedException>(() => integration.GetSessionCapabilities());
		Assert.Throws<PlatformNotSupportedException>(() => integration.AcquireWakeLock(WakeLockType.System, "测试"));
		Assert.Throws<PlatformNotSupportedException>(() => integration.PerformSessionAction(SessionAction.Lock));
	}

	[Fact]
	public void 原生调用前拒绝空字符和非法填充枚举()
	{
		UniDesktopIntegration integration = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
		Assert.Throws<ArgumentException>(() => integration.Notify("标题\0后缀", "正文"));
		Assert.Throws<ArgumentException>(() => integration.SetWallpaper("图片\0.png"));
		Assert.Throws<ArgumentOutOfRangeException>(() => integration.SetWallpaper("图片.png", (WallpaperFillMode)99));
		Assert.Throws<ArgumentOutOfRangeException>(() => integration.SendMediaCommand((MediaCommand)99));
		Assert.Throws<ArgumentOutOfRangeException>(() => integration.AcquireWakeLock((WakeLockType)99, "测试"));
		Assert.Throws<ArgumentException>(() => integration.AcquireWakeLock(WakeLockType.System, "测试\0"));
		Assert.Throws<ArgumentOutOfRangeException>(() => integration.PerformSessionAction((SessionAction)99, true));
	}

	[Theory]
	[InlineData(SessionAction.Logout)]
	[InlineData(SessionAction.Suspend)]
	[InlineData(SessionAction.Hibernate)]
	[InlineData(SessionAction.Reboot)]
	[InlineData(SessionAction.Shutdown)]
	public void 未确认的会话操作在探测和原生调用前被拒绝(SessionAction action)
	{
		UniDesktopIntegration integration = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
		InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => integration.PerformSessionAction(action));
		Assert.Contains("明确确认", error.Message);
	}

	[Fact]
	public void 强调色保持四通道字节序且无值不冒充黑色()
	{
		Assert.Null(UniDesktopIntegration.DecodeAccentColor(0));
		Assert.Equal(new DesktopAccent(0xAA, 0xBB, 0xCC, 0xDD), UniDesktopIntegration.DecodeAccentColor(0xDDCCBBAA));
		Assert.Equal(new DesktopAccent(0, 0, 0, 255), UniDesktopIntegration.DecodeAccentColor(0xFF000000));
	}

	[Fact]
	public void 常亮锁并发释放只调用一次原生接口()
	{
		int calls = 0;
		using UniDesktopWakeLock lease = new(() => Interlocked.Increment(ref calls));
		Parallel.For(0, 30, _ => lease.Dispose());
		Assert.Equal(1, calls);
	}

	[Fact]
	public void 线程绑定的常亮锁拒绝异线程释放后仍能由原线程释放()
	{
		int calls = 0;
		using UniDesktopWakeLock lease = new(() => calls++, requiresOwnerThread: true);
		Exception? error = null;
		Thread thread = new(() => error = Record.Exception(lease.Dispose));
		thread.Start();
		thread.Join();
		Assert.IsType<InvalidOperationException>(error);
		Assert.Equal(0, calls);
		lease.Dispose();
		Assert.Equal(1, calls);
	}
}
