namespace Nori.Core.Platform;

/// <summary>UniDesktop 桌面集成；原生库可用不代表当前桌面会话支持所有操作。</summary>
public interface IDesktopIntegration
{
	bool IsAvailable { get; }
	DesktopTheme DetectTheme();
	DesktopAccent? GetAccentColor();
	string? GetWallpaper();
	void SetWallpaper(string path, WallpaperFillMode mode = WallpaperFillMode.Fill);
	uint Notify(string title, string body);
	IDisposable AcquireWakeLock(WakeLockType type, string reason);
	MediaPlaybackStatus GetMediaStatus();
	DesktopMediaInfo? GetMediaInfo();
	void SendMediaCommand(MediaCommand command);
	SessionCapabilities GetSessionCapabilities();
	/// <summary>非锁屏动作必须显式确认；能力位仅代表后端存在，不保证当前账户权限。</summary>
	void PerformSessionAction(SessionAction action, bool confirmed = false);
}

public enum DesktopTheme { Unknown, Dark, Light }
public enum WallpaperFillMode { Crop, Fill, Fit, Stretch }
public sealed record DesktopAccent(byte Red, byte Green, byte Blue, byte Alpha);
public enum WakeLockType { Display, System }
public enum MediaPlaybackStatus { Playing, Paused, Stopped, Unknown }
public enum MediaCommand { Play, Pause, Toggle, Next, Previous, Stop }
public sealed record DesktopMediaInfo(string? Title, string? Artist, string? Album, ulong DurationMilliseconds, ulong PositionMilliseconds);
public enum SessionAction { Lock, Logout, Suspend, Hibernate, Reboot, Shutdown }

[Flags]
public enum SessionCapabilities : uint
{
	None = 0,
	Management = 0x00010000,
	Lock = 0x00020000,
	Logout = 0x00040000,
	Suspend = 0x00080000,
	Hibernate = 0x00100000,
	Reboot = 0x00200000,
	Shutdown = 0x00400000,
}
