using Tmds.DBus.Protocol;

namespace Nori.Core.Platform;

/// <summary>
/// Linux 是否开启动画。门户优先。门户没有结果时，Plasma 先看 KDE，GNOME 先看 GTK。
/// 认不出桌面时，任一侧明确关闭即视为关闭。读不到保持未知：快捷聊天减少动效，装饰动效仍播放。
/// 属性本身不发起 IPC。
/// </summary>
internal static class LinuxMotionPreference
{
	private const int Unknown = 0;
	private const int Enabled = 1;
	private const int Disabled = 2;
	private static int _animations = Unknown;
	private static int _loopStarted;

	internal static bool PrefersReducedMotion
	{
		get
		{
			EnsureRefreshing();
			return PrefersReduced(Current);
		}
	}

	internal static bool AllowDecorativeAnimations
	{
		get
		{
			EnsureRefreshing();
			return AllowDecorative(Current);
		}
	}

	/// <summary>读到开启才不减少；未知与关闭都减少。</summary>
	internal static bool PrefersReduced(bool? animationsEnabled) => animationsEnabled != true;

	/// <summary>只有明确关闭才停装饰动效。</summary>
	internal static bool AllowDecorative(bool? animationsEnabled) => animationsEnabled != false;

	internal enum DesktopFamily
	{
		Unknown,
		Kde,
		Gnome,
	}

	/// <summary><c>XDG_CURRENT_DESKTOP</c> 优先，没有时再看 <c>XDG_SESSION_DESKTOP</c>。值按冒号分段。</summary>
	internal static DesktopFamily ClassifyDesktop(string? currentDesktop, string? sessionDesktop)
	{
		DesktopFamily current = Classify(currentDesktop);
		return current != DesktopFamily.Unknown ? current : Classify(sessionDesktop);
	}

	/// <summary>
	/// 门户优先。Plasma 先采用 KDE 动画时长，GNOME 先采用 GTK。
	/// 桌面不明时，明确关闭盖过另一侧的开启。
	/// </summary>
	internal static bool? SelectAnimations(bool? portal, bool? gtk, bool? kde, DesktopFamily desktop = DesktopFamily.Unknown)
	{
		if (portal is not null) return portal;
		return desktop switch
		{
			DesktopFamily.Kde => kde ?? gtk,
			DesktopFamily.Gnome => gtk ?? kde,
			_ => kde == false || gtk == false ? false : gtk ?? kde,
		};
	}

	private static DesktopFamily Classify(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return DesktopFamily.Unknown;
		foreach (string part in value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (part.Equals("KDE", StringComparison.OrdinalIgnoreCase) || part.Equals("plasma", StringComparison.OrdinalIgnoreCase))
				return DesktopFamily.Kde;
			if (part.Contains("GNOME", StringComparison.OrdinalIgnoreCase)) return DesktopFamily.Gnome;
		}
		return DesktopFamily.Unknown;
	}

	internal static string ResolveConfigHome(string? configured, string home)
	{
		if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured)) return configured;
		return Path.Combine(home, ".config");
	}

	internal static bool? ParseGtkEnableAnimations(string text) => ParseKey(text, null, "gtk-enable-animations") is { } raw
		? ParseBool(raw) : null;

	/// <summary>只认 <c>[KDE]</c> 里的 <c>AnimationDurationFactor</c>。0 与负数是关闭，正数是开启。</summary>
	internal static bool? ParseKdeAnimationFactor(string text)
	{
		string? raw = ParseKey(text, "KDE", "AnimationDurationFactor");
		if (raw is null || !double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double factor))
			return null;
		return factor > 0;
	}

	internal static bool? ReadGtkAnimations(Func<string, string?> read, string configHome)
	{
		foreach (string folder in new[] { "gtk-4.0", "gtk-3.0" })
		{
			string? text = read(Path.Combine(configHome, folder, "settings.ini"));
			if (text is null) continue;
			bool? value = ParseGtkEnableAnimations(text);
			if (value is not null) return value;
		}
		return null;
	}

	internal static bool? ReadKdeAnimations(Func<string, string?> read, string configHome)
	{
		string? text = read(Path.Combine(configHome, "kdeglobals"));
		return text is null ? null : ParseKdeAnimationFactor(text);
	}

	private static bool? Current => Volatile.Read(ref _animations) switch
	{
		Enabled => true,
		Disabled => false,
		_ => null,
	};

	private static void EnsureRefreshing()
	{
		if (!OperatingSystem.IsLinux()) return;
		if (Interlocked.Exchange(ref _loopStarted, 1) != 0) return;
		_ = Task.Run(RefreshLoopAsync);
	}

	private static async Task RefreshLoopAsync()
	{
		using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
		do
		{
			try
			{
				bool? portal = await TryPortalAsync().ConfigureAwait(false);
				string home = ResolveConfigHome(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
					Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
				bool? gtk = portal is null ? ReadGtkAnimations(ReadFile, home) : null;
				bool? kde = portal is null ? ReadKdeAnimations(ReadFile, home) : null;
				DesktopFamily desktop = ClassifyDesktop(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
					Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"));
				Publish(SelectAnimations(portal, gtk, kde, desktop));
			}
			catch (Exception)
			{
				// 这一轮失败就留着上次的值。
			}
		}
		while (await timer.WaitForNextTickAsync().ConfigureAwait(false));
	}

	private static void Publish(bool? animationsEnabled)
	{
		Volatile.Write(ref _animations, animationsEnabled switch
		{
			true => Enabled,
			false => Disabled,
			_ => Unknown,
		});
	}

	private static async Task<bool?> TryPortalAsync()
	{
		if (string.IsNullOrWhiteSpace(DBusAddress.Session)) return null;
		try
		{
			using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(2));
			using DBusConnection connection = new(DBusAddress.Session);
			await connection.ConnectAsync().AsTask().WaitObservedAsync(cancel.Token).ConfigureAwait(false);
			VariantValue value = await connection.CallMethodAsync(PortalRead(connection),
				static (reply, _) => reply.GetBodyReader().ReadVariantValue())
				.WaitObservedAsync(cancel.Token).ConfigureAwait(false);
			return LinuxIdleTime.UnwrapBool(value);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static MessageBuffer PortalRead(DBusConnection connection)
	{
		using MessageWriter writer = connection.GetMessageWriter();
		writer.WriteMethodCallHeader(destination: "org.freedesktop.portal.Desktop",
			path: "/org/freedesktop/portal/desktop", @interface: "org.freedesktop.portal.Settings",
			member: "Read", signature: "ss", flags: MessageFlags.NoAutoStart);
		writer.WriteString("org.gnome.desktop.interface");
		writer.WriteString("enable-animations");
		return writer.CreateMessage();
	}

	private static string? ReadFile(string path)
	{
		try
		{
			return File.Exists(path) ? File.ReadAllText(path) : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>只在指定节里找键；节名为空时全文都算。后出现的同名键覆盖前面的。</summary>
	private static string? ParseKey(string text, string? section, string key)
	{
		bool inSection = section is null;
		string? value = null;
		foreach (string raw in text.Split('\n'))
		{
			string line = raw.Trim().TrimEnd('\r');
			if (line.Length == 0 || line[0] is '#' or ';') continue;
			if (line[0] == '[')
			{
				if (section is null) continue;
				int end = line.IndexOf(']');
				inSection = end > 1 && line[1..end].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
				continue;
			}
			if (!inSection) continue;
			int split = line.IndexOf('=');
			if (split <= 0) continue;
			if (!line[..split].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
			value = line[(split + 1)..].Trim().Trim('"');
		}
		return value is { Length: > 0 } ? value : null;
	}

	private static bool? ParseBool(string raw) => raw.ToLowerInvariant() switch
	{
		"1" or "true" or "yes" or "on" => true,
		"0" or "false" or "no" or "off" => false,
		_ => null,
	};
}
