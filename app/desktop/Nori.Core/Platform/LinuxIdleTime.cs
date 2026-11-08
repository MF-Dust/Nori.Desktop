using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace Nori.Core.Platform;

/// <summary>
/// Linux 键鼠空闲秒数。
///
/// X11 用单独的 Display 调 <c>XScreenSaverQueryInfo</c>，不复用点击穿透那条连接。
/// 原生 Wayland 以及屏保扩展不可用时，再试会话总线：ScreenSaver、Mutter IdleMonitor，最后是 logind。
/// 失败保持 null，并短时间不再重试，避免每次对话都等待不存在的总线。
/// </summary>
internal static class LinuxIdleTime
{
	private const int RetryMilliseconds = 30_000;
	private static readonly Lock Gate = new();
	private static Source _source = Source.None;
	private static long _retryAt;
	private static Task<double?>? _x11Task;

	private enum Source
	{
		None,
		Xss,
		ScreenSaver,
		Mutter,
		Logind,
	}

	internal static double? GetSeconds()
	{
		if (!OperatingSystem.IsLinux()) return null;
		lock (Gate)
		{
			long now = Environment.TickCount64;
			if (now < _retryAt) return null;
			try
			{
				double? value = ReadOnce();
				_retryAt = value is null ? now + RetryMilliseconds : 0;
				return value;
			}
			catch (Exception)
			{
				_retryAt = now + RetryMilliseconds;
				return null;
			}
		}
	}

	/// <summary>屏保或 Mutter 返回的毫秒。0 表示正在操作，不是查询失败。</summary>
	internal static double FromMilliseconds(ulong milliseconds) => milliseconds / 1000d;

	/// <summary>
	/// logind：<paramref name="idleHint"/> 为 false 时用户仍活跃，空闲为 0。
	/// 为 true 时，两个时间戳必须使用同一时钟；起点为 0 或晚于现在则视为读不到。
	/// </summary>
	internal static double? FromLogind(bool idleHint, ulong idleSinceMicroseconds, ulong nowMicroseconds)
	{
		if (!idleHint) return 0;
		if (idleSinceMicroseconds == 0 || nowMicroseconds < idleSinceMicroseconds) return null;
		return (nowMicroseconds - idleSinceMicroseconds) / 1_000_000d;
	}

	internal static bool? UnwrapBool(VariantValue value)
	{
		for (int depth = 0; depth < 4 && value.Type == VariantValueType.Variant; depth++)
			value = value.GetVariantValue();
		return value.Type == VariantValueType.Bool ? value.GetBool() : null;
	}

	internal static ulong? UnwrapUInt64(VariantValue value)
	{
		for (int depth = 0; depth < 4 && value.Type == VariantValueType.Variant; depth++)
			value = value.GetVariantValue();
		return value.Type == VariantValueType.UInt64 ? value.GetUInt64() : null;
	}

	private static double? ReadOnce()
	{
		bool wayland = PlatformServices.DetectSession() == SessionType.Wayland;
		if (!wayland && (_source is Source.None or Source.Xss))
		{
			if (TryX11(out double seconds))
			{
				_source = Source.Xss;
				return seconds;
			}
			if (_source == Source.Xss) _source = Source.None;
		}
		if (_source == Source.Xss || string.IsNullOrWhiteSpace(DBusAddress.Session)) return null;
		return QueryBus();
	}

	private static bool TryX11(out double seconds)
	{
		seconds = 0;
		if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess) return false;
		if (_x11Task is { IsCompleted: false }) return false;
		if (_x11Task is { IsCompletedSuccessfully: false }) _ = _x11Task.Exception;
		_x11Task = Task.Run(QueryX11);
		if (!_x11Task.Wait(TimeSpan.FromSeconds(1))) return false;
		if (!_x11Task.IsCompletedSuccessfully)
		{
			_ = _x11Task.Exception;
			return false;
		}
		if (_x11Task.Result is not double value) return false;
		seconds = value;
		return true;
	}

	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	private static double? QueryX11()
	{
		if (!OperatingSystem.IsLinux()) return null;
		nint display = OpenDisplay(0);
		if (display == 0) return null;
		try
		{
			XScreenSaverInfo info = new();
			if (XScreenSaverQueryInfo(display, DefaultRootWindow(display), ref info) == 0) return null;
			return FromMilliseconds((ulong)info.Idle);
		}
		catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
		{
			return null;
		}
		finally
		{
			CloseDisplay(display);
		}
	}

	private static double? QueryBus()
	{
		try
		{
			return QueryBusAsync().GetAwaiter().GetResult();
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static async Task<double?> QueryBusAsync()
	{
		using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(2));
		using DBusConnection connection = new(DBusAddress.Session!);
		await connection.ConnectAsync().AsTask().WaitObservedAsync(cancel.Token).ConfigureAwait(false);
		foreach (Source source in Order(_source))
		{
			double? value = await TrySourceAsync(connection, source, cancel.Token).ConfigureAwait(false);
			if (value is null) continue;
			_source = source;
			return value;
		}
		_source = Source.None;
		return null;
	}

	private static Source[] Order(Source cached) => cached switch
	{
		Source.Mutter => [Source.Mutter, Source.ScreenSaver, Source.Logind],
		Source.Logind => [Source.Logind, Source.ScreenSaver, Source.Mutter],
		_ => [Source.ScreenSaver, Source.Mutter, Source.Logind],
	};

	private static async Task<double?> TrySourceAsync(DBusConnection connection, Source source, CancellationToken cancellationToken)
	{
		try
		{
			return source switch
			{
				Source.ScreenSaver => await TryScreenSaverAsync(connection, cancellationToken).ConfigureAwait(false),
				Source.Mutter => await TryMutterAsync(connection, cancellationToken).ConfigureAwait(false),
				Source.Logind => await TryLogindAsync(connection, cancellationToken).ConfigureAwait(false),
				_ => null,
			};
		}
		catch (Exception exception) when (exception is DBusExceptionBase or OperationCanceledException or TimeoutException)
		{
			return null;
		}
	}

	private static async Task<double?> TryScreenSaverAsync(DBusConnection connection, CancellationToken cancellationToken)
	{
		foreach (string path in new[] { "/org/freedesktop/ScreenSaver", "/ScreenSaver" })
		{
			try
			{
				uint milliseconds = await connection.CallMethodAsync(Method(connection, "org.freedesktop.ScreenSaver", path,
						"org.freedesktop.ScreenSaver", "GetSessionIdleTime"),
					static (message, _) => message.GetBodyReader().ReadUInt32())
					.WaitObservedAsync(cancellationToken).ConfigureAwait(false);
				return FromMilliseconds(milliseconds);
			}
			catch (DBusExceptionBase)
			{
				// 下一条路径或下一种来源。
			}
		}
		return null;
	}

	private static async Task<double?> TryMutterAsync(DBusConnection connection, CancellationToken cancellationToken)
	{
		ulong milliseconds = await connection.CallMethodAsync(Method(connection, "org.gnome.Mutter.IdleMonitor",
				"/org/gnome/Mutter/IdleMonitor/Core", "org.gnome.Mutter.IdleMonitor", "GetIdletime"),
			static (message, _) => message.GetBodyReader().ReadUInt64())
			.WaitObservedAsync(cancellationToken).ConfigureAwait(false);
		return FromMilliseconds(milliseconds);
	}

	private static async Task<double?> TryLogindAsync(DBusConnection connection, CancellationToken cancellationToken)
	{
		string? session = await FindSessionAsync(connection, cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrEmpty(session)) return null;
		bool? idle = await ReadBoolPropertyAsync(connection, session, "IdleHint", cancellationToken).ConfigureAwait(false);
		if (idle is not true) return idle is false ? 0 : null;
		ulong? since = await ReadUInt64PropertyAsync(connection, session, "IdleSinceHintMonotonic", cancellationToken).ConfigureAwait(false);
		ulong? now = MonotonicMicroseconds();
		if (since is null || now is null)
		{
			since = await ReadUInt64PropertyAsync(connection, session, "IdleSinceHint", cancellationToken).ConfigureAwait(false);
			now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
		}
		return since is null || now is null ? null : FromLogind(true, since.Value, now.Value);
	}

	private static async Task<string?> FindSessionAsync(DBusConnection connection, CancellationToken cancellationToken)
	{
		string? id = Environment.GetEnvironmentVariable("XDG_SESSION_ID");
		if (!string.IsNullOrWhiteSpace(id))
		{
			try
			{
				return await connection.CallMethodAsync(SessionById(connection, id),
					static (message, _) => message.GetBodyReader().ReadObjectPathAsString())
					.WaitObservedAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (DBusExceptionBase)
			{
				// 再按进程号找。
			}
		}
		return await connection.CallMethodAsync(SessionByPid(connection),
			static (message, _) => message.GetBodyReader().ReadObjectPathAsString())
			.WaitObservedAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task<bool?> ReadBoolPropertyAsync(DBusConnection connection, string path, string name, CancellationToken cancellationToken)
	{
		VariantValue value = await connection.CallMethodAsync(PropertyGet(connection, path, name),
			static (message, _) => message.GetBodyReader().ReadVariantValue())
			.WaitObservedAsync(cancellationToken).ConfigureAwait(false);
		return UnwrapBool(value);
	}

	private static async Task<ulong?> ReadUInt64PropertyAsync(DBusConnection connection, string path, string name, CancellationToken cancellationToken)
	{
		VariantValue value = await connection.CallMethodAsync(PropertyGet(connection, path, name),
			static (message, _) => message.GetBodyReader().ReadVariantValue())
			.WaitObservedAsync(cancellationToken).ConfigureAwait(false);
		return UnwrapUInt64(value);
	}

	private static ulong? MonotonicMicroseconds()
	{
		if (!OperatingSystem.IsLinux()) return null;
		try
		{
			if (ClockGetTime(1, out TimeSpec time) != 0) return null;
			return (ulong)time.Seconds * 1_000_000 + (ulong)time.Nanoseconds / 1000;
		}
		catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
		{
			return null;
		}
	}

	private static MessageBuffer Method(DBusConnection connection, string destination, string path, string iface, string member)
	{
		using MessageWriter writer = connection.GetMessageWriter();
		writer.WriteMethodCallHeader(destination: destination, path: path, @interface: iface, member: member,
			flags: MessageFlags.NoAutoStart);
		return writer.CreateMessage();
	}

	private static MessageBuffer SessionById(DBusConnection connection, string id)
	{
		using MessageWriter writer = connection.GetMessageWriter();
		writer.WriteMethodCallHeader(destination: "org.freedesktop.login1", path: "/org/freedesktop/login1",
			@interface: "org.freedesktop.login1.Manager", member: "GetSession", signature: "s", flags: MessageFlags.NoAutoStart);
		writer.WriteString(id);
		return writer.CreateMessage();
	}

	private static MessageBuffer SessionByPid(DBusConnection connection)
	{
		using MessageWriter writer = connection.GetMessageWriter();
		writer.WriteMethodCallHeader(destination: "org.freedesktop.login1", path: "/org/freedesktop/login1",
			@interface: "org.freedesktop.login1.Manager", member: "GetSessionByPID", signature: "u", flags: MessageFlags.NoAutoStart);
		writer.WriteUInt32((uint)Environment.ProcessId);
		return writer.CreateMessage();
	}

	private static MessageBuffer PropertyGet(DBusConnection connection, string path, string name)
	{
		using MessageWriter writer = connection.GetMessageWriter();
		writer.WriteMethodCallHeader(destination: "org.freedesktop.login1", path: path,
			@interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss", flags: MessageFlags.NoAutoStart);
		writer.WriteString("org.freedesktop.login1.Session");
		writer.WriteString(name);
		return writer.CreateMessage();
	}

	[StructLayout(LayoutKind.Sequential)]
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3898", Justification = "原生 ABI 结构体仅用于互操作，不参与相等比较。")]
	private struct XScreenSaverInfo
	{
		public nuint Window;
		public int State;
		public int Kind;
		public nuint TilOrSince;
		public nuint Idle;
		public nuint EventMask;
	}

	[StructLayout(LayoutKind.Sequential)]
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3898", Justification = "原生 ABI 结构体仅用于互操作，不参与相等比较。")]
	private struct TimeSpec
	{
		public long Seconds;
		public long Nanoseconds;
	}

	[DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
	private static extern nint OpenDisplay(nint name);

	[DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
	private static extern int CloseDisplay(nint display);

	[DllImport("libX11.so.6", EntryPoint = "XDefaultRootWindow")]
	private static extern nint DefaultRootWindow(nint display);

	[DllImport("libXss.so.1", EntryPoint = "XScreenSaverQueryInfo")]
	private static extern int XScreenSaverQueryInfo(nint display, nint drawable, ref XScreenSaverInfo info);

	[DllImport("libc", EntryPoint = "clock_gettime")]
	private static extern int ClockGetTime(int clockId, out TimeSpec time);
}
