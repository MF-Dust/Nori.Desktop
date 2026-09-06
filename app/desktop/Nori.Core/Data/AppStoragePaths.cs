using System.Runtime.InteropServices;

namespace Nori.Core.Data;

/// <summary>应用运行时使用的不可变数据路径集合。</summary>
public sealed class AppStoragePaths
{
	public const string MarkerFileName = ".nori-storage.json";
	public const string CleanupReceiptFileName = ".legacy-cleanup-pending.json";

	public AppStoragePaths(string packageRoot)
	{
		if (string.IsNullOrWhiteSpace(packageRoot)) throw new ArgumentException("包根目录不能为空", nameof(packageRoot));
		PackageRoot = Normalize(packageRoot);
		if (Directory.Exists(PackageRoot) && (File.GetAttributes(PackageRoot) & FileAttributes.ReparsePoint) != 0)
			throw new ArgumentException("包根目录不能是符号链接或 reparse point", nameof(packageRoot));
		if (string.Equals(PackageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetPathRoot(PackageRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison))
			throw new ArgumentException("包根目录不能是文件系统根目录", nameof(packageRoot));

		DataRoot = Path.Combine(PackageRoot, "data");
		DatabaseDirectory = Path.Combine(DataRoot, "core", "database");
		DatabasePath = Path.Combine(DatabaseDirectory, AppPaths.DatabaseFileName);
		DatabaseWalPath = DatabasePath + "-wal";
		DatabaseShmPath = DatabasePath + "-shm";
		MigrationBackupDirectory = DatabaseDirectory;
		SecretDirectory = Path.Combine(DataRoot, "core", "security");
		SecretPath = Path.Combine(SecretDirectory, "secret.key");
		KnowledgeDirectory = Path.Combine(DataRoot, "knowledge", "documents");
		KnowledgePath = Path.Combine(KnowledgeDirectory, "Memory.md");
		ResourcesDirectory = Path.Combine(DataRoot, "resources");
		ResourcesInstalledDirectory = Path.Combine(ResourcesDirectory, "installed");
		Live2DDirectory = Path.Combine(ResourcesInstalledDirectory, "live2d");
		ResourcesCacheDirectory = Path.Combine(ResourcesDirectory, "cache");
		ResourcesImportDirectory = Path.Combine(ResourcesDirectory, "temp", "import");
		IndexTtsDirectory = Path.Combine(ResourcesDirectory, "indextts");
		IndexTtsVoicesDirectory = Path.Combine(IndexTtsDirectory, "voices");
		IndexTtsCachePath = Path.Combine(IndexTtsDirectory, "voice_cache.json");
		PluginsDirectory = Path.Combine(DataRoot, "plugins");
		PluginsInstalledDirectory = Path.Combine(PluginsDirectory, "installed");
		PluginsDataDirectory = Path.Combine(PluginsDirectory, "data");
		PluginsWebViewCacheDirectory = Path.Combine(PluginsDirectory, "cache", "webview");
		PluginsPackageInboxDirectory = Path.Combine(PluginsDirectory, "cache", "packages", "inbox");
		PluginsStagingDirectory = Path.Combine(PluginsDirectory, "temp", "staging");
		UpdatesDirectory = Path.Combine(DataRoot, "updates");
		UpdatesDownloadDirectory = Path.Combine(UpdatesDirectory, "download");
		UpdatesStagingDirectory = Path.Combine(UpdatesDirectory, "staging");
		WebViewHostCacheDirectory = Path.Combine(DataRoot, "webview", "cache", "host");
		AutomationBrowserTempDirectory = Path.Combine(DataRoot, "automation", "temp", "browser");
		LogsDirectory = Path.Combine(DataRoot, "diagnostics", "logs");
		DiagnosticsDirectory = Path.Combine(DataRoot, "diagnostics");
		LegacyUnclassifiedDirectory = Path.Combine(DataRoot, "legacy", "unclassified");
		MarkerPath = Path.Combine(DataRoot, MarkerFileName);
		CleanupReceiptPath = Path.Combine(DataRoot, CleanupReceiptFileName);
	}

	public string PackageRoot { get; }
	public string DataRoot { get; }
	public string DatabaseDirectory { get; }
	public string DatabasePath { get; }
	public string DatabaseWalPath { get; }
	public string DatabaseShmPath { get; }
	public string MigrationBackupDirectory { get; }
	public string SecretDirectory { get; }
	public string SecretPath { get; }
	public string KnowledgeDirectory { get; }
	public string KnowledgePath { get; }
	public string ResourcesDirectory { get; }
	public string ResourcesInstalledDirectory { get; }
	public string Live2DDirectory { get; }
	public string ResourcesCacheDirectory { get; }
	public string ResourcesImportDirectory { get; }
	public string IndexTtsDirectory { get; }
	public string IndexTtsVoicesDirectory { get; }
	public string IndexTtsCachePath { get; }
	public string PluginsDirectory { get; }
	public string PluginsInstalledDirectory { get; }
	public string PluginsDataDirectory { get; }
	public string PluginsWebViewCacheDirectory { get; }
	public string PluginsPackageInboxDirectory { get; }
	public string PluginsStagingDirectory { get; }
	public string UpdatesDirectory { get; }
	public string UpdatesDownloadDirectory { get; }
	public string UpdatesStagingDirectory { get; }
	public string WebViewHostCacheDirectory { get; }
	public string AutomationBrowserTempDirectory { get; }
	public string LogsDirectory { get; }
	public string DiagnosticsDirectory { get; }
	public string LegacyUnclassifiedDirectory { get; }
	public string MarkerPath { get; }
	public string CleanupReceiptPath { get; }

	/// <summary>创建固定目录并检查数据目录确实可写，不回退到系统目录。</summary>
	public void EnsureCreated()
	{
		EnsureDirectory(PackageRoot);
		EnsureDirectory(DataRoot);
		EnsureDirectory(DatabaseDirectory);
		EnsureDirectory(SecretDirectory);
		EnsureDirectory(KnowledgeDirectory);
		EnsureDirectory(Live2DDirectory);
		EnsureDirectory(ResourcesCacheDirectory);
		EnsureDirectory(ResourcesImportDirectory);
		EnsureDirectory(IndexTtsVoicesDirectory);
		EnsureDirectory(PluginsInstalledDirectory);
		EnsureDirectory(PluginsDataDirectory);
		EnsureDirectory(PluginsWebViewCacheDirectory);
		EnsureDirectory(PluginsPackageInboxDirectory);
		EnsureDirectory(PluginsStagingDirectory);
		EnsureDirectory(UpdatesDirectory);
		EnsureDirectory(UpdatesDownloadDirectory);
		EnsureDirectory(UpdatesStagingDirectory);
		EnsureDirectory(WebViewHostCacheDirectory);
		EnsureDirectory(AutomationBrowserTempDirectory);
		EnsureDirectory(LogsDirectory);
		EnsureDirectory(DiagnosticsDirectory);
		EnsureDirectory(LegacyUnclassifiedDirectory);
		string probe = Path.Combine(DataRoot, $".write-test-{Guid.NewGuid():N}");
		try
		{
			using FileStream stream = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
			stream.Flush(true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new IOException($"数据目录不可写: {DataRoot}", exception);
		}
		finally
		{
			try { if (File.Exists(probe)) File.Delete(probe); } catch { }
		}
	}

	/// <summary>判断路径是否位于指定目录内，边界按目录分隔符处理。</summary>
	public static bool IsContained(string path, string root)
	{
		string fullPath = Normalize(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string fullRoot = Normalize(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(fullPath, fullRoot, PathComparison)
			|| fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison);
	}

	/// <summary>解析现有路径段中的符号链接，缺失的尾部路径保持原样。</summary>
	public static string ResolvePhysicalPath(string path)
	{
		string fullPath = Normalize(path);
		for (int pass = 0; pass < 32; pass++)
		{
			string root = Path.GetPathRoot(fullPath) ?? throw new InvalidOperationException("路径缺少文件系统根目录");
			string current = root;
			bool resolvedLink = false;
			string relative = Path.GetRelativePath(root, fullPath);
			foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
			{
				if (part is "." or "") continue;
				string candidate = Path.Combine(current, part);
				if (!File.Exists(candidate) && !Directory.Exists(candidate))
				{
					current = candidate;
					continue;
				}
				if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) == 0)
				{
					current = candidate;
					continue;
				}
				FileSystemInfo entry = Directory.Exists(candidate)
					? new DirectoryInfo(candidate)
					: new FileInfo(candidate);
				FileSystemInfo target = entry.ResolveLinkTarget(returnFinalTarget: true)
					?? throw new InvalidOperationException($"无法解析符号链接或 reparse point: {candidate}");
				current = Path.GetFullPath(target.FullName);
				resolvedLink = true;
			}
			fullPath = Path.GetFullPath(current);
			if (!resolvedLink) return fullPath;
		}
		throw new InvalidOperationException("路径包含过深的符号链接链");
	}

	/// <summary>拒绝路径链中的符号链接、junction 和其他 reparse point。</summary>
	public static void EnsureNoReparsePoints(string path, string root)
	{
		string fullRoot = Normalize(root);
		string fullPath = Normalize(path);
		if (!IsContained(fullPath, fullRoot)) throw new InvalidOperationException("路径越出数据目录");
		if ((File.Exists(fullRoot) || Directory.Exists(fullRoot))
			&& (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
			throw new InvalidOperationException($"不允许使用符号链接或 reparse point: {fullRoot}");
		string relative = Path.GetRelativePath(fullRoot, fullPath);
		string current = fullRoot;
		foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
		{
			if (part is "." or "") continue;
			current = Path.Combine(current, part);
			if (File.Exists(current) || Directory.Exists(current))
			{
				if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
					throw new InvalidOperationException($"不允许使用符号链接或 reparse point: {current}");
			}
		}
	}

	private static StringComparison PathComparison => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
	private static string Normalize(string path) => Path.GetFullPath(path.Trim());

	private void EnsureDirectory(string path)
	{
		if (File.Exists(path)) throw new IOException($"目录位置被文件占用: {path}");
		Directory.CreateDirectory(path);
		// 包根的可信性由启动解析器负责；此处只拒绝包内路径被链接重定向。
		EnsureNoReparsePoints(path, PackageRoot);
	}
}
