using System.IO.Compression;
using System.Text.Json;
using Nori.Core.Data;
using Nori.Core.Resources;
namespace Nori.PluginRuntime;

/// <summary>本地 .noripack 安装器与版本指针管理。</summary>
internal sealed class PluginPackageInstaller
{
	public const string PackageExtension = ".noripack";
	public const string ManifestFileName = "manifest.json";
	public const string CurrentFileName = "current.json";

	private readonly string _root;
	private readonly string _stagingRoot;
	private readonly string _inboxRoot;

	public PluginPackageInstaller(string root, string? inboxRoot = null, string? stagingRoot = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);
		_root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		_stagingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot ?? Path.Combine(_root, ".staging")));
		_inboxRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inboxRoot ?? Path.Combine(_root, "inbox")));
		Directory.CreateDirectory(_root);
		Directory.CreateDirectory(_stagingRoot);
		Directory.CreateDirectory(_inboxRoot);
		// 插件根本身不能是链接；管理边界取物理父目录，允许宿主位于 macOS /var 等系统级别名祖先之下。
		PluginPathSafety.EnsureNoReparsePoint(_root, PluginErrorCodes.PackagePathDenied, "插件根不能是符号链接或 reparse point");
		string managementRoot = AppStoragePaths.ResolvePhysicalPath(Directory.GetParent(_root)?.FullName ?? _root);
		PluginPathSafety.EnsureNoReparsePoints(managementRoot, AppStoragePaths.ResolvePhysicalPath(_stagingRoot), PluginErrorCodes.PackagePathDenied, "插件 staging 路径越过宿主边界");
		PluginPathSafety.EnsureNoReparsePoints(managementRoot, AppStoragePaths.ResolvePhysicalPath(_inboxRoot), PluginErrorCodes.PackagePathDenied, "插件 inbox 路径越过宿主边界");
	}

	public string RootDirectory => _root;
	public string InboxDirectory => _inboxRoot;
	public string CurrentDirectory(string id) => Path.Combine(_root, id);
	public string VersionDirectory(string id, string version) => Path.Combine(_root, id, version);
	public bool IsVersionInstalled(string id, string version) => Directory.Exists(VersionDirectory(id, version));
	public IEnumerable<string> InstalledIds => Directory.EnumerateDirectories(_root)
		.Where(path => !string.Equals(Path.GetFileName(path), ".staging", StringComparison.Ordinal))
		.Where(path => File.Exists(Path.Combine(path, CurrentFileName)))
		.Select(Path.GetFileName)
		.Where(id => id is not null)
		.Cast<string>();

	/// <summary>只读取并校验包内 manifest，不写入文件。</summary>
	public PluginManifest InspectPackage(string packagePath)
	{
		try
		{
			string fullPath = ValidatePackagePath(packagePath);
			using ZipArchive archive = ZipFile.OpenRead(fullPath);
			(IReadOnlyList<PackageEntry> entries, _) = ValidateEntries(archive);
			PackageEntry manifestEntry = entries.Single(entry => entry.Path.Equals(ManifestFileName, StringComparison.Ordinal));
			using StreamReader reader = new(manifestEntry.Entry.Open());
			return PluginManifestReader.ReadJson(reader.ReadToEnd());
		}
		catch (PluginException)
		{
			throw;
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ResourceException)
		{
			throw new PluginException(PluginErrorCodes.InvalidPackage, "插件包无效", exception);
		}
	}

	/// <summary>先 staging 校验，再把完整版本目录移动到插件目录并更新 current.json。</summary>
	public PluginManifest Install(string packagePath, CancellationToken cancellationToken = default)
	{
		PluginManifest manifest = InspectPackage(packagePath);
		string staging = Path.Combine(_stagingRoot, $"{manifest.Id}-{Guid.NewGuid():N}");
		string pluginDirectory = CurrentDirectory(manifest.Id);
		string versionDirectory = VersionDirectory(manifest.Id, manifest.Version);
		string pointerPath = Path.Combine(pluginDirectory, CurrentFileName);
		bool versionMoved = false;
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			Directory.CreateDirectory(staging);
			ZipExtractor.Extract(packagePath, staging, cancellationToken);
			PluginManifest extracted = PluginManifestReader.Read(Path.Combine(staging, ManifestFileName));
			if (!string.Equals(extracted.Id, manifest.Id, StringComparison.Ordinal) || !string.Equals(extracted.Version, manifest.Version, StringComparison.Ordinal))
				throw new PluginException(PluginErrorCodes.InvalidPackage, "解压前后的 manifest 身份不一致");
			string entryPath = Path.Combine(staging, extracted.Runtime.Assembly.Replace('/', Path.DirectorySeparatorChar));
			if (!File.Exists(entryPath)) throw new PluginException(PluginErrorCodes.EntryAssemblyMissing, "插件包缺少 manifest 指定的入口程序集");
			PluginLoadContext.EnsureReferencesAllowed(staging);
			EnsureNoReparsePoints(staging);
			cancellationToken.ThrowIfCancellationRequested();
			EnsureNoReparsePoints(pluginDirectory);
			Directory.CreateDirectory(pluginDirectory);
			EnsureNoReparsePoints(pluginDirectory);
			if (Directory.Exists(versionDirectory)) throw new PluginException(PluginErrorCodes.InvalidPackage, "插件版本已经安装");
			Directory.Move(staging, versionDirectory);
			versionMoved = true;
			WriteCurrentPointer(pointerPath, extracted.Version);
			return extracted;
		}
		catch (PluginException)
		{
			TryDelete(staging);
			if (versionMoved) TryDelete(versionDirectory);
			throw;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ResourceException)
		{
			TryDelete(staging);
			if (versionMoved) TryDelete(versionDirectory);
			throw new PluginException(PluginErrorCodes.InvalidPackage, "插件包安装失败", exception);
		}
	}

	/// <summary>
	/// 删除宿主管理根下指定 manifest ID 的全部已安装版本和 current.json。
	/// 只接受经过 manifest ID 规则校验的 ID，不接受任意路径。
	/// </summary>
	public void Uninstall(string id)
	{
		if (!PluginManifestReader.IsValidPluginId(id))
			throw new PluginException(PluginErrorCodes.InvalidManifest, "插件 ID 无效");

		string pluginDirectory = Path.GetFullPath(CurrentDirectory(id));
		PluginPathSafety.EnsureTreeNoReparsePoints(_root, pluginDirectory, PluginErrorCodes.PackagePathDenied, "插件卸载目录越过宿主管理边界或包含符号链接");
		if (!Directory.Exists(pluginDirectory)) return;
		try
		{
			Directory.Delete(pluginDirectory, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new PluginException(PluginErrorCodes.UnloadPendingRestart, "插件文件当前无法删除，需要重启后完成卸载", exception);
		}
	}

	/// <summary>读取插件的 current.json 并返回对应的版本目录。</summary>
	public string? ResolveCurrentDirectory(string id)
	{
		if (!PluginManifestReader.IsValidPluginId(id)) return null;
		string pluginDirectory = CurrentDirectory(id);
		string pointerPath = Path.Combine(pluginDirectory, CurrentFileName);
		if (!File.Exists(pointerPath)) return null;
		try
		{
			EnsureNoReparsePoints(pluginDirectory);
			EnsureNoReparsePoints(pointerPath);
			CurrentPointer? pointer = JsonSerializer.Deserialize<CurrentPointer>(File.ReadAllText(pointerPath));
			if (pointer is null || !PluginVersion.TryParse(pointer.Version, out _)) return null;
			string versionDirectory = VersionDirectory(id, pointer.Version);
			EnsureNoReparsePoints(versionDirectory);
			return Directory.Exists(versionDirectory) ? versionDirectory : null;
		}
		catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static (IReadOnlyList<PackageEntry> Entries, string? CommonTop) ValidateEntries(ZipArchive archive)
	{
		if (archive.Entries.Count == 0 || archive.Entries.Count > 4096)
			throw new PluginException(PluginErrorCodes.InvalidPackage, "插件包条目数量无效");

		List<(ZipArchiveEntry Entry, string Path)> sanitized = [];
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			ValidateRawEntryName(entry.FullName);
			string path;
			try { path = ZipExtractor.SanitizePath(entry.FullName); }
			catch (ResourceException exception) { throw new PluginException(PluginErrorCodes.PackagePathDenied, exception.Message, exception); }
			if (path.Length == 0) continue;
			if (!ZipExtractor.IsDirectoryEntry(entry) && (entry.Length < 0 || entry.Length > ZipExtractor.DefaultLimits.MaxSingleFileBytes))
				throw new PluginException(PluginErrorCodes.InvalidPackage, "插件包单文件过大");
			sanitized.Add((entry, path));
		}

		string? commonTop = ZipExtractor.FindCommonTopDirectory(sanitized.Where(item => !ZipExtractor.IsDirectoryEntry(item.Entry)).Select(item => item.Path));
		HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
		List<PackageEntry> entries = [];
		bool hasManifest = false;
		foreach ((ZipArchiveEntry entry, string rawPath) in sanitized)
		{
			string path = StripCommonTop(rawPath, commonTop);
			if (path.Length == 0) continue;
			if (!paths.Add(path)) throw new PluginException(PluginErrorCodes.InvalidPackage, $"插件包包含重复路径: {path}");
			if (path.Equals(ManifestFileName, StringComparison.Ordinal)) hasManifest = true;
			if (ZipExtractor.IsDirectoryEntry(entry)) continue;
			if (PluginAssemblyPolicy.IsContractAssemblyFile(path)) throw new PluginException(PluginErrorCodes.ContractAssemblyDenied, "插件包不得携带 contract DLL");
			bool allowed = path.Equals(ManifestFileName, StringComparison.Ordinal) ||
				path.Equals("README.md", StringComparison.OrdinalIgnoreCase) ||
				path.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) ||
				path.Equals("icon.png", StringComparison.OrdinalIgnoreCase) ||
				path.StartsWith("lib/", StringComparison.Ordinal) ||
				path.StartsWith("web/", StringComparison.Ordinal) ||
				path.StartsWith("assets/", StringComparison.Ordinal) ||
				path.StartsWith("locales/", StringComparison.Ordinal) ||
				path.StartsWith("runtimes/", StringComparison.Ordinal);
			if (!allowed) throw new PluginException(PluginErrorCodes.AssetDenied, $"插件包文件不允许: {path}");
			entries.Add(new PackageEntry(entry, path));
		}
		if (!hasManifest) throw new PluginException(PluginErrorCodes.InvalidPackage, "插件包缺少 manifest.json");
		return (entries, commonTop);
	}

	private static void ValidateRawEntryName(string raw)
	{
		if (raw.Contains('\\') || raw.Contains(':', StringComparison.Ordinal))
			throw new PluginException(PluginErrorCodes.PackagePathDenied, "插件包路径包含不允许的分隔符");
		string[] parts = raw.Split('/');
		for (int index = 0; index < parts.Length; index++)
		{
			if (parts[index] == ".") throw new PluginException(PluginErrorCodes.PackagePathDenied, "插件包路径包含 . 段");
			if (parts[index].Length == 0 && index < parts.Length - 1)
				throw new PluginException(PluginErrorCodes.PackagePathDenied, "插件包路径包含重复分隔符");
		}
	}

	private static string ValidatePackagePath(string packagePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
		string fullPath = Path.GetFullPath(packagePath);
		if (!fullPath.EndsWith(PluginPackageInstaller.PackageExtension, StringComparison.OrdinalIgnoreCase))
			throw new PluginException(PluginErrorCodes.InvalidPackage, "插件包扩展名必须为 .noripack");
		if (!File.Exists(fullPath)) throw new PluginException(PluginErrorCodes.InvalidPackage, "插件包不存在");
		EnsureNoReparsePoints(fullPath);
		return fullPath;
	}

	private static string StripCommonTop(string path, string? commonTop)
	{
		if (commonTop is null) return path;
		string prefix = commonTop + "/";
		if (path.Equals(commonTop, StringComparison.Ordinal)) return string.Empty;
		if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new PluginException(PluginErrorCodes.InvalidPackage, "插件包顶层目录不一致");
		return path[prefix.Length..];
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "临时安装包清理失败不能覆盖安装结果。")]
	private static void WriteCurrentPointer(string pointerPath, string version)
	{
		string temporary = pointerPath + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temporary, JsonSerializer.Serialize(new CurrentPointer(version)));
			File.Move(temporary, pointerPath, true);
		}
		finally
		{
			try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
		}
	}

	/// <summary>
	/// 只检查宿主管理的目标节点本身，不检查其系统级祖先目录。
	/// 插件目录树内部的链接由 PluginLoadContext 的锚定扫描负责拒绝。
	/// </summary>
	private static void EnsureNoReparsePoints(string path) =>
		PluginPathSafety.EnsureNoReparsePoint(path, PluginErrorCodes.PackagePathDenied, "插件包路径包含符号链接");

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "回滚清理只能尽力执行，必须保留原始安装错误。")]
	private static void TryDelete(string path)
	{
		try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
	}

	private sealed record PackageEntry(ZipArchiveEntry Entry, string Path);
	private sealed record CurrentPointer(string Version);
}
