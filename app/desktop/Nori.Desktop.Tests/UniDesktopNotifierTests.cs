using Nori.Core.Notifications;
using Nori.Core.Platform;
using Nori.Desktop.Notifications;

namespace Nori.Desktop.Tests;

public sealed class UniDesktopNotifierTests
{
	private sealed class FakeIntegration : IDesktopIntegration
	{
		public bool IsAvailable { get; set; } = true;
		public string? Body { get; private set; }
		public bool Fail { get; set; }
		public uint Notify(string title, string body)
		{
			if (Fail) throw new InvalidOperationException("通知服务不可用");
			Body = body;
			return 1;
		}
		public DesktopTheme DetectTheme() => DesktopTheme.Unknown;
		public DesktopAccent? GetAccentColor() => null;
		public string? GetWallpaper() => null;
		public void SetWallpaper(string path, WallpaperFillMode mode = WallpaperFillMode.Fill) => throw new NotSupportedException();
		public IDisposable AcquireWakeLock(WakeLockType type, string reason) => throw new NotSupportedException();
		public MediaPlaybackStatus GetMediaStatus() => MediaPlaybackStatus.Unknown;
		public DesktopMediaInfo? GetMediaInfo() => null;
		public void SendMediaCommand(MediaCommand command) => throw new NotSupportedException();
		public SessionCapabilities GetSessionCapabilities() => SessionCapabilities.None;
		public void PerformSessionAction(SessionAction action, bool confirmed = false) => throw new NotSupportedException();
	}

	private static ApprovalNotice Notice => new() { RequestId = "test", ToolName = "<b>writeFile</b>", ArgumentSummary = "敏感参数" };

	[Fact]
	public void SDK强调色读数保留原有Windows备份格式()
	{
		if (!System.OperatingSystem.IsWindows()) return;
		Assert.Equal(0xDDCCBBAAu, Expression.WindowsDesktopAppearance.PackAccent(new DesktopAccent(0xAA, 0xBB, 0xCC, 0xDD)));
	}

	[Fact]
	public async Task Linux提醒转义标记且不泄漏参数并引导应用内审批()
	{
		FakeIntegration integration = new();
		using UniDesktopNotifier notifier = new(integration, _ => Assert.Fail("不应报错"));
		await notifier.SendAsync(Notice);
		Assert.Contains("&lt;b&gt;writeFile&lt;/b&gt;", integration.Body);
		Assert.Contains("主界面确认", integration.Body);
		Assert.DoesNotContain("敏感参数", integration.Body);
	}

	[Fact]
	public async Task 不支持或已关闭时不发送通知()
	{
		FakeIntegration integration = new() { IsAvailable = false };
		using UniDesktopNotifier notifier = new(integration, _ => Assert.Fail("不应报错"));
		await notifier.SendAsync(Notice);
		Assert.Null(integration.Body);
		integration.IsAvailable = true;
		notifier.Dispose();
		await notifier.SendAsync(Notice);
		Assert.Null(integration.Body);
	}

	[Fact]
	public async Task 系统通知与日志失败不会影响授权()
	{
		FakeIntegration integration = new() { Fail = true };
		int failures = 0;
		using UniDesktopNotifier notifier = new(integration, _ => { failures++; throw new InvalidOperationException("日志不可用"); });
		await notifier.SendAsync(Notice);
		Assert.Equal(1, failures);
	}
}
