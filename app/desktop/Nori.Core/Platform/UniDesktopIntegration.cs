using System.Runtime.InteropServices;

namespace Nori.Core.Platform;

/// <summary>绑定 UDA v0.2.0 C ABI，只加载应用目录内的原生库。调用涉及 IPC，须在后台线程执行。</summary>
public sealed class UniDesktopIntegration : IDesktopIntegration
{
	private const CallingConvention Convention = CallingConvention.Cdecl;
	[UnmanagedFunctionPointer(Convention)] private delegate int DetectThemeCall(out int theme);
	[UnmanagedFunctionPointer(Convention)] private delegate int GetWallpaperCall(out nint path);
	[UnmanagedFunctionPointer(Convention)] private delegate int SetWallpaperCall([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
	[UnmanagedFunctionPointer(Convention)] private delegate int NotifyCall(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string app,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string title,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string body,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string icon,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string actions, out uint id);
	[UnmanagedFunctionPointer(Convention)] private delegate nint LastErrorCall();
	[UnmanagedFunctionPointer(Convention)] private delegate void FreeStringCall(nint value);
	[UnmanagedFunctionPointer(Convention)] private delegate int AccentColorCall(out uint rgba);
	[UnmanagedFunctionPointer(Convention)] private delegate int WakeLockAcquireCall(int type, [MarshalAs(UnmanagedType.LPUTF8Str)] string reason, out ulong handle);
	[UnmanagedFunctionPointer(Convention)] private delegate int WakeLockReleaseCall(ulong handle);
	[UnmanagedFunctionPointer(Convention)] private delegate int MediaInfoCall(out nint title, out nint artist, out nint album, out ulong duration, out ulong position);
	[UnmanagedFunctionPointer(Convention)] private delegate int MediaStatusCall(out int status);
	[UnmanagedFunctionPointer(Convention)] private delegate int MediaCommandCall(int command);
	[UnmanagedFunctionPointer(Convention)] private delegate int SessionCapabilitiesCall(out uint capabilities);
	[UnmanagedFunctionPointer(Convention)] private delegate int SessionActionCall();

	private readonly DetectThemeCall? _detectTheme;
	private readonly GetWallpaperCall? _getWallpaper;
	private readonly SetWallpaperCall? _setWallpaper;
	private readonly NotifyCall? _notify;
	private readonly LastErrorCall? _lastError;
	private readonly FreeStringCall? _freeString;
	private readonly AccentColorCall? _accentColor;
	private readonly WakeLockAcquireCall? _wakeLockAcquire;
	private readonly WakeLockReleaseCall? _wakeLockRelease;
	private readonly MediaInfoCall? _mediaInfo;
	private readonly MediaStatusCall? _mediaStatus;
	private readonly MediaCommandCall? _mediaCommand;
	private readonly SessionCapabilitiesCall? _sessionCapabilities;
	private readonly SessionActionCall[] _sessionActions = [];
	private static readonly Lazy<UniDesktopIntegration> Instance = new(() => new(AppContext.BaseDirectory));
	[ThreadStatic] private static bool _windowsWakeLockHeld;
	public static UniDesktopIntegration Current => Instance.Value;
	public bool IsAvailable { get; }

	internal UniDesktopIntegration(string directory)
	{
		if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
		string? name = OperatingSystem.IsWindows() ? "uda_ffi.dll" : OperatingSystem.IsLinux() ? "libuda_ffi.so" : null;
		if (name is null || !NativeLibrary.TryLoad(Path.Combine(directory, name), out nint library)) return;
		try
		{
			_detectTheme = Bind<DetectThemeCall>(library, "uda_detect_theme");
			_getWallpaper = Bind<GetWallpaperCall>(library, "uda_get_wallpaper");
			_setWallpaper = Bind<SetWallpaperCall>(library, "uda_set_wallpaper");
			_notify = Bind<NotifyCall>(library, "uda_notify");
			_lastError = Bind<LastErrorCall>(library, "uda_last_error_message");
			_freeString = Bind<FreeStringCall>(library, "uda_free_string");
			_accentColor = Bind<AccentColorCall>(library, "uda_get_accent_color");
			_wakeLockAcquire = Bind<WakeLockAcquireCall>(library, "uda_wakelock_acquire");
			_wakeLockRelease = Bind<WakeLockReleaseCall>(library, "uda_wakelock_release");
			_mediaInfo = Bind<MediaInfoCall>(library, "uda_media_get_metadata");
			_mediaStatus = Bind<MediaStatusCall>(library, "uda_media_get_status");
			_mediaCommand = Bind<MediaCommandCall>(library, "uda_media_send_command");
			_sessionCapabilities = Bind<SessionCapabilitiesCall>(library, "uda_session_capabilities");
			_sessionActions = [
				Bind<SessionActionCall>(library, "uda_session_lock"),
				Bind<SessionActionCall>(library, "uda_session_logout"),
				Bind<SessionActionCall>(library, "uda_session_suspend"),
				Bind<SessionActionCall>(library, "uda_session_hibernate"),
				Bind<SessionActionCall>(library, "uda_session_reboot"),
				Bind<SessionActionCall>(library, "uda_session_shutdown"),
			];
			// 委托在整个进程生命周期内使用这些符号，成功后不卸载原生库。
			IsAvailable = true;
		}
		catch (EntryPointNotFoundException)
		{
			NativeLibrary.Free(library);
		}
	}

	private static T Bind<T>(nint library, string name) where T : Delegate =>
		Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

	private void RequireAvailable()
	{
		if (!IsAvailable) throw new PlatformNotSupportedException("UniDesktop 原生库不可用，请检查目标平台与构建依赖。");
	}

	private void Check(int status)
	{
		if (status == 0) return;
		string message = TakeString(_lastError!()) ?? "桌面操作失败";
		if (status == -2) throw new PlatformNotSupportedException($"UniDesktop：{message}");
		throw new InvalidOperationException($"UniDesktop 调用失败（{status}）：{message}");
	}

	private string? TakeString(nint value)
	{
		try { return Marshal.PtrToStringUTF8(value); }
		finally { _freeString!(value); }
	}

	public DesktopTheme DetectTheme()
	{
		RequireAvailable();
		Check(_detectTheme!(out int theme));
		return theme is 1 or 2 ? (DesktopTheme)theme : DesktopTheme.Unknown;
	}

	public string? GetWallpaper()
	{
		RequireAvailable();
		int status = _getWallpaper!(out nint path);
		// 先复制线程局部错误，再释放结果字符串。
		try { Check(status); return Marshal.PtrToStringUTF8(path); }
		finally { _freeString!(path); }
	}

	public void SetWallpaper(string path, WallpaperFillMode mode = WallpaperFillMode.Fill)
	{
		ValidateText(path);
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
		RequireAvailable();
		Check(_setWallpaper!(Path.GetFullPath(path), (int)mode));
	}

	public uint Notify(string title, string body)
	{
		ValidateText(title);
		ValidateText(body);
		RequireAvailable();
		Check(_notify!("Nori Desktop", title, body, "", "", out uint id));
		return id;
	}

	public DesktopAccent? GetAccentColor()
	{
		RequireAvailable();
		Check(_accentColor!(out uint rgba));
		return DecodeAccentColor(rgba);
	}

	// 当前支持的平台均为小端序；UDA 依次写入 R/G/B/A 四个字节，全零表示没有强调色。
	internal static DesktopAccent? DecodeAccentColor(uint rgba) => rgba == 0 ? null :
		new((byte)rgba, (byte)(rgba >> 8), (byte)(rgba >> 16), (byte)(rgba >> 24));

	public IDisposable AcquireWakeLock(WakeLockType type, string reason)
	{
		if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
		ValidateText(reason);
		RequireAvailable();
		if (OperatingSystem.IsWindows() && _windowsWakeLockHeld)
			throw new InvalidOperationException("Windows 同一线程不能重复获取常亮锁，请先释放已有锁。");
		Check(_wakeLockAcquire!((int)type, reason, out ulong handle));
		if (handle == 0) throw new InvalidOperationException("UniDesktop 返回了无效的常亮锁句柄。");
		SetWindowsWakeLockHeld(true);
		return new UniDesktopWakeLock(() =>
		{
			try { Check(_wakeLockRelease!(handle)); }
			finally { SetWindowsWakeLockHeld(false); }
		}, OperatingSystem.IsWindows());
	}

	private static void SetWindowsWakeLockHeld(bool held)
	{
		if (OperatingSystem.IsWindows()) _windowsWakeLockHeld = held;
	}

	public MediaPlaybackStatus GetMediaStatus()
	{
		RequireAvailable();
		Check(_mediaStatus!(out int status));
		return status is >= 0 and <= 2 ? (MediaPlaybackStatus)status : MediaPlaybackStatus.Unknown;
	}

	public DesktopMediaInfo? GetMediaInfo()
	{
		RequireAvailable();
		int status = _mediaInfo!(out nint title, out nint artist, out nint album, out ulong duration, out ulong position);
		try
		{
			Check(status);
			if (title == 0 && artist == 0 && album == 0 && duration == 0 && position == 0) return null;
			return new(Marshal.PtrToStringUTF8(title), Marshal.PtrToStringUTF8(artist), Marshal.PtrToStringUTF8(album), duration, position);
		}
		finally
		{
			_freeString!(title);
			_freeString!(artist);
			_freeString!(album);
		}
	}

	public void SendMediaCommand(MediaCommand command)
	{
		if (!Enum.IsDefined(command)) throw new ArgumentOutOfRangeException(nameof(command));
		RequireAvailable();
		Check(_mediaCommand!((int)command));
	}

	public SessionCapabilities GetSessionCapabilities()
	{
		RequireAvailable();
		Check(_sessionCapabilities!(out uint capabilities));
		return (SessionCapabilities)capabilities;
	}

	public void PerformSessionAction(SessionAction action, bool confirmed = false)
	{
		if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
		if (action != SessionAction.Lock && !confirmed) throw new InvalidOperationException("更改会话或电源状态前必须由用户明确确认。");
		SessionCapabilities capability = (SessionCapabilities)(0x00020000u << (int)action);
		if (!GetSessionCapabilities().HasFlag(capability)) throw new PlatformNotSupportedException("当前平台没有所请求的会话操作后端。");
		Check(_sessionActions[(int)action]());
	}

	private static void ValidateText(string value)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (value.Contains('\0')) throw new ArgumentException("文本不能包含空字符。", nameof(value));
	}
}
