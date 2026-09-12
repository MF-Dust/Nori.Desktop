using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nori.Core.Data;

/// <summary>启动阶段根据 launcher、开发环境或安全的槽目录推断包根。</summary>
public static class AppStoragePathResolver
{
	private static readonly Regex SlotPattern = new("^app-[0-9]+\\.[0-9]+\\.[0-9]+-[0-9]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

	public static AppStoragePaths Resolve(string? launcherPath = null, string? baseDirectory = null)
	{
		string? trustedRoot = ResolveTrustedEnvironmentRoot();
		if (trustedRoot is not null) return new AppStoragePaths(trustedRoot);

		if (string.Equals(ProductVersion.Current, "Dev", StringComparison.Ordinal))
			return new AppStoragePaths(Environment.GetEnvironmentVariable("NORI_DEV_PACKAGE_ROOT") ?? Environment.CurrentDirectory);

		string supplied = launcherPath ?? baseDirectory ?? AppContext.BaseDirectory;
		string full = Path.GetFullPath(supplied);
		string directory = File.Exists(full) ? Path.GetDirectoryName(full)! : full;
		string? packageRoot = ValidatePublishedSlot(directory);
		if (packageRoot is not null) return new AppStoragePaths(packageRoot);

		throw new InvalidOperationException("无法安全推断包根目录，请通过 Nori 启动器启动应用");
	}

	private static string? ResolveTrustedEnvironmentRoot()
	{
		string? rootValue = Environment.GetEnvironmentVariable("NORI_PACKAGE_ROOT");
		if (string.IsNullOrWhiteSpace(rootValue)) return null;
		string root = Path.GetFullPath(rootValue);
		string deploymentValue = Environment.GetEnvironmentVariable("NORI_DEPLOYMENT_ROOT") ?? "";
		string launcherValue = Environment.GetEnvironmentVariable("NORI_LAUNCHER_PATH") ?? "";
		string executableValue = Environment.GetEnvironmentVariable("NORI_EXECUTABLE_PATH") ?? "";
		string deployment = Path.GetFullPath(deploymentValue);
		string launcher = Path.GetFullPath(launcherValue);
		string executable = Path.GetFullPath(executableValue);
		string process = Path.GetFullPath(Environment.ProcessPath ?? "");
		string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
		EnsureCanonical(root, directory: true);
		EnsureCanonical(deployment, directory: true);
		EnsureCanonical(launcher, directory: false);
		EnsureCanonical(executable, directory: false);
		EnsureCanonical(process, directory: false);
		EnsureCanonical(baseDirectory, directory: true);
		if (!IsContained(deployment, root) || !string.Equals(Path.GetDirectoryName(deployment)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison)
			|| !SlotPattern.IsMatch(Path.GetFileName(deployment)) || !IsContained(baseDirectory, deployment) || !IsContained(process, deployment))
			throw new InvalidOperationException("启动环境的发布包根、部署槽或宿主路径不可信");
		string expectedLauncher = OperatingSystem.IsMacOS()
			? Path.Combine(root, "Nori.app", "Contents", "MacOS", "Nori")
			: Path.Combine(root, OperatingSystem.IsWindows() ? "Nori.exe" : "Nori");
		if (!string.Equals(Path.GetFullPath(launcher), Path.GetFullPath(expectedLauncher), PathComparison)
			|| !string.Equals(executable, process, PathComparison))
			throw new InvalidOperationException("启动环境的 launcher 或宿主路径不可信");
		return root;
	}

	private static void EnsureCanonical(string path, bool directory)
	{
		if (string.IsNullOrWhiteSpace(path) || (directory ? !Directory.Exists(path) : !File.Exists(path)))
			throw new InvalidOperationException("启动环境路径不存在");
		string? current = Path.GetFullPath(path);
		while (current is not null && !string.IsNullOrEmpty(Path.GetPathRoot(current)))
		{
			if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
				throw new InvalidOperationException("启动环境路径包含 reparse point");
			string? parent = Path.GetDirectoryName(current);
			if (parent is null || string.Equals(parent, current, PathComparison)) break;
			current = parent;
		}
	}

	private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	private static bool IsContained(string path, string root)
	{
		string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(fullPath, fullRoot, PathComparison) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison);
	}

	private static string? ValidatePublishedSlot(string directory)
	{
		DirectoryInfo slot = new(directory);
		if (!SlotPattern.IsMatch(slot.Name) || !slot.Exists || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
		string manifestPath = Path.Combine(directory, "deployment.json");
		if (!File.Exists(manifestPath) || (File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0) return null;
		try
		{
			using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
			JsonElement root = document.RootElement;
			if (!root.TryGetProperty("schema_version", out JsonElement schema) || schema.GetInt32() != 1
				|| !root.TryGetProperty("rid", out JsonElement rid) || string.IsNullOrWhiteSpace(rid.GetString())
				|| !root.TryGetProperty("entrypoint", out JsonElement entry) || string.IsNullOrWhiteSpace(entry.GetString())) return null;
			string relative = entry.GetString()!;
			if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(part => part is "" or "." or "..")) return null;
			string? processPath = Environment.ProcessPath;
			if (processPath is null) return null;
			string expected = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
			if (!string.Equals(expected, Path.GetFullPath(processPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
			if (!File.Exists(expected) || (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0) return null;
			return slot.Parent?.FullName;
		}
		catch (JsonException) { return null; }
		catch (IOException) { return null; }
		catch (UnauthorizedAccessException) { return null; }
	}
}
