using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media;

namespace Nori.Desktop.Expression;

/// <summary>在 Plasma 5/6 与支持强调色的 GNOME 会话中读写系统强调色。</summary>
public sealed class LinuxDesktopAppearance : IDesktopAppearance
{
	private const string Kde = "kde";
	private const string Gnome = "gnome";
	private const string KdeConfig = "kdeglobals";
	private const string KdeAccentKey = "AccentColor";
	private const string KdeUnset = "unset";
	private const string GnomeSchema = "org.gnome.desktop.interface";
	private const string GnomeAccentKey = "accent-color";
	private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(1);

	// libadwaita 的 AdwAccentColor 背景色，按 sRGB 距离选最接近的枚举。
	private static readonly (string Name, Color Color)[] GnomePalette =
	[
		("blue", Color.FromRgb(0x35, 0x84, 0xE4)),
		("teal", Color.FromRgb(0x21, 0x90, 0xA4)),
		("green", Color.FromRgb(0x3A, 0x94, 0x4A)),
		("yellow", Color.FromRgb(0xC8, 0x88, 0x00)),
		("orange", Color.FromRgb(0xED, 0x5B, 0x00)),
		("red", Color.FromRgb(0xE6, 0x2D, 0x42)),
		("pink", Color.FromRgb(0xD5, 0x61, 0x99)),
		("purple", Color.FromRgb(0x91, 0x41, 0xAC)),
		("slate", Color.FromRgb(0x6F, 0x83, 0x96)),
	];

	internal delegate string? CommandRunner(string file, string[] arguments, bool userConfigOnly = false);

	private readonly string? _kdeVersion;
	private readonly CommandRunner _run;

	/// <summary>识别当前会话并在后台探测工具与配置，不写入系统设置。</summary>
	public LinuxDesktopAppearance() : this(
		Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
		Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"),
		Environment.GetEnvironmentVariable("KDE_SESSION_VERSION"),
		RunCommand)
	{
	}

	internal LinuxDesktopAppearance(string? currentDesktop, string? sessionDesktop, string? kdeVersion, CommandRunner run)
	{
		_kdeVersion = kdeVersion;
		_run = run;
		string? desktop = string.IsNullOrWhiteSpace(currentDesktop) ? sessionDesktop : currentDesktop;
		BackupScope = desktop?.Split(':', StringSplitOptions.TrimEntries)
			.Select(part => part.ToLowerInvariant())
			.FirstOrDefault(part => part is Kde or "plasma" or Gnome) switch
		{
			Kde or "plasma" when kdeVersion is "5" or "6" => Kde,
			Gnome => Gnome,
			_ => null,
		};
		Availability = BackupScope is null ? Task.FromResult(false) : Task.Run(Probe);
	}

	/// <inheritdoc />
	public string? BackupScope { get; }

	internal Task<bool> Availability { get; }

	/// <inheritdoc />
	public bool IsAvailable => Availability.IsCompletedSuccessfully && Availability.Result;

	/// <inheritdoc />
	public string? CaptureBackup() => !IsAvailable ? null : BackupScope switch
	{
		Kde => ReadKdeAccent(),
		Gnome => ReadGnomeAccent(),
		_ => null,
	};

	/// <inheritdoc />
	public void SetAccentColor(Color color)
	{
		if (!IsAvailable) return;
		if (BackupScope == Kde)
			WriteKdeAccent(FormattableString.Invariant($"{color.R},{color.G},{color.B}"));
		else
			WriteGnomeAccent(GnomePalette.MinBy(entry => DistanceSquared(color, entry.Color)).Name);
	}

	/// <inheritdoc />
	public void RestoreBackup(string backup)
	{
		if (!IsAvailable) return;
		if (BackupScope == Kde && (backup == KdeUnset || IsKdeRgb(backup)))
			WriteKdeAccent(backup);
		else if (BackupScope == Gnome && IsGnomeAccent(backup))
			WriteGnomeAccent(backup);
	}

	private bool Probe() => BackupScope switch
	{
		Kde => _run($"kwriteconfig{_kdeVersion}", ["--help"])?.Contains("--delete", StringComparison.Ordinal) == true
			&& ReadKdeAccent() is not null
			&& _run("gdbus", ["call", "--session", "--dest", "org.freedesktop.DBus", "--object-path",
				"/org/freedesktop/DBus", "--method", "org.freedesktop.DBus.GetId"]) is not null,
		Gnome => _run("gsettings", ["writable", GnomeSchema, GnomeAccentKey]) == "true" && ReadGnomeAccent() is not null,
		_ => false,
	};

	private string? ReadKdeAccent()
	{
		// 若未设用户项但存在级联默认值，--delete 会屏蔽默认值而非恢复继承。
		const string MissingKdeAccent = "nori-accent-not-set";
		string? Read(bool userConfigOnly) => _run($"kreadconfig{_kdeVersion}",
			["--file", KdeConfig, "--group", "General", "--key", KdeAccentKey, "--default", MissingKdeAccent], userConfigOnly);
		string? local = Read(userConfigOnly: true);
		if (local == MissingKdeAccent) return Read(userConfigOnly: false) == MissingKdeAccent ? KdeUnset : null;
		return local is not null && IsKdeRgb(local) ? local : null;
	}

	private void WriteKdeAccent(string accent)
	{
		if (_run($"kwriteconfig{_kdeVersion}",
			["--file", KdeConfig, "--group", "General", "--key", KdeAccentKey, accent == KdeUnset ? "--delete" : accent]) is null
			|| ReadKdeAccent() != accent)
			throw new IOException("写入 KDE 强调色失败");

		// 两个整数参数分别是 PaletteChanged (0) 与未使用的参数 (0)。
		if (_run("gdbus", ["emit", "--session", "--object-path", "/KGlobalSettings", "--signal",
			"org.kde.KGlobalSettings.notifyChange", "0", "0"]) is null)
			throw new IOException("通知 KDE 刷新强调色失败");
	}

	private static bool IsKdeRgb(string value)
	{
		string[] components = value.Split(',');
		return components.Length == 3 && components.All(component =>
			byte.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out _));
	}

	private string? ReadGnomeAccent()
	{
		string? output = _run("gsettings", ["get", GnomeSchema, GnomeAccentKey]);
		return output is { Length: > 2 } && output[0] == '\'' && output[^1] == '\'' && IsGnomeAccent(output[1..^1])
			? output[1..^1] : null;
	}

	private void WriteGnomeAccent(string accent)
	{
		if (_run("gsettings", ["set", GnomeSchema, GnomeAccentKey, accent]) is null || ReadGnomeAccent() != accent)
			throw new IOException("写入 GNOME 强调色失败");
	}

	private static bool IsGnomeAccent(string name) => GnomePalette.Any(entry => entry.Name == name);

	private static int DistanceSquared(Color left, Color right)
	{
		int dr = left.R - right.R, dg = left.G - right.G, db = left.B - right.B;
		return dr * dr + dg * dg + db * db;
	}

	internal static string? RunCommand(string file, string[] arguments, bool userConfigOnly = false)
	{
		if (file.Any(char.IsControl) || arguments.Any(argument => argument.Any(char.IsControl)))
			throw new ArgumentException("桌面强调色命令的参数不得包含控制字符", nameof(arguments));

		ProcessStartInfo start = new(file)
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach (string argument in arguments) start.ArgumentList.Add(argument);
		// KConfig 即使用绝对路径也会级联读取系统配置；只在读取用户项时排除它。
		if (userConfigOnly) start.Environment["XDG_CONFIG_DIRS"] = "/dev/null";

		using Process process = new() { StartInfo = start };
		try { process.Start(); }
		catch (Win32Exception) { return null; }

		using CancellationTokenSource timeout = new(CommandTimeout);
		Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
		Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
		try
		{
			Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).WaitAsync(timeout.Token).GetAwaiter().GetResult();
			return process.ExitCode == 0 ? output.Result.TrimEnd('\r', '\n') : null;
		}
		catch (OperationCanceledException) when (timeout.IsCancellationRequested)
		{
			try { process.Kill(); }
			catch (InvalidOperationException) { /* 进程已在超时边界退出。 */ }
			return null;
		}
	}
}
