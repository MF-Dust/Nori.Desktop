using Nori.Core.Data;
using Nori.Core.Live2D;

namespace Nori.Core.Resources;

/// <summary>
/// 本地 Live2D 资源管理.
///
/// 负责资源的本地检查、列表、删除与从本地 ZIP/目录导入, 不包含远程下载逻辑.
/// 导入始终经过同卷 staging、完整校验与原子交换.
/// </summary>
public sealed class ResourceManager
{
	private readonly string _resourcesRoot;

	public ResourceManager(string? dataDir = null)
	{
		_resourcesRoot = dataDir is null ? new AppStoragePaths(Environment.CurrentDirectory).ResourcesInstalledDirectory : Path.Combine(dataDir, AppPaths.ResourcesDirName);
	}

	public ResourceManager(AppStoragePaths paths)
	{
		ArgumentNullException.ThrowIfNull(paths);
		_resourcesRoot = paths.ResourcesInstalledDirectory;
	}

	private string ResourcesRoot => _resourcesRoot;

	/// <summary>指定资源的目录.</summary>
	public string ResourceDir(ResourceType type, string name) =>
		Path.Combine(ResourcesRoot, type.AsString(), ResourceName.Validate(name));

	/// <summary>
	/// 资源是否已安装. Live2D 必须是固定支持的模型 ID, 且真的包含 model3.json.
	/// 导入时的引用完整校验在 staging 阶段完成.
	/// </summary>
	public bool IsInstalled(ResourceType type, string name)
	{
		if (type == ResourceType.Live2D && !SupportedModelIds.IsSupported(name)) return false;
		string dir = ResourceDir(type, name);
		if (!Directory.Exists(dir)) return false;
		return IsInstalledAt(type, dir);
	}

	/// <summary>列出某类型下所有已安装资源 (按名称排序).</summary>
	public IReadOnlyList<ResourceInfo> List(ResourceType type)
	{
		string root = Path.Combine(ResourcesRoot, type.AsString());
		if (!Directory.Exists(root)) return [];
		List<ResourceInfo> result = [];
		try
		{
			foreach (DirectoryInfo directory in EnumerateDirectoriesSafely(root))
			{
				string name = directory.Name;
				if (type == ResourceType.Live2D && !SupportedModelIds.IsSupported(name)) continue;
				if (!IsInstalledAt(type, directory.FullName)) continue;
				result.Add(new ResourceInfo
				{
					Name = name,
					ResourceType = type,
					Path = directory.FullName,
					Size = DirectorySize(directory.FullName),
				});
			}
		}
		catch (IOException)
		{
			return [];
		}
		catch (UnauthorizedAccessException)
		{
			return [];
		}
		return [.. result.OrderBy(item => item.Name, StringComparer.Ordinal)];
	}

	/// <summary>删除资源.</summary>
	public void Delete(ResourceType type, string name)
	{
		string dir = ResourceDir(type, name);
		if (!Directory.Exists(dir)) throw new ResourceException($"资源不存在: {name}");
		Directory.Delete(dir, true);
	}

	/// <summary>
	/// 从本地 ZIP 压缩包或目录导入资源.
	/// 所有候选先在 resources 根目录同卷 staging 中复制并验证, 成功后才交换 target.
	/// </summary>
	public IReadOnlyList<string> Import(ResourceType type, string sourcePath) =>
		Import(type, sourcePath, CancellationToken.None);

	/// <summary>
	/// 从本地 ZIP 压缩包或目录导入资源, 支持在 staging、复制与交换阶段取消.
	/// </summary>
	public IReadOnlyList<string> Import(ResourceType type, string sourcePath, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
		{
			throw new ResourceException($"导入源不存在: {sourcePath}");
		}

		if (File.Exists(sourcePath) && sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			return ImportFromZip(type, sourcePath, cancellationToken);
		}

		if (Directory.Exists(sourcePath))
		{
			return ImportFromDirectory(type, sourcePath, cancellationToken);
		}

		throw new ResourceException("目前仅支持导入 .zip 压缩包或模型文件夹");
	}

	private IReadOnlyList<string> ImportFromZip(ResourceType type, string zipPath, CancellationToken cancellationToken)
	{
		string stagingRoot = CreateStagingRoot();
		try
		{
			string extractedRoot = Path.Combine(stagingRoot, "extracted");
			ZipExtractor.Extract(zipPath, extractedRoot, cancellationToken);
			IReadOnlyList<ModelCandidate> candidates = CollectCandidates(extractedRoot, cancellationToken);
			return PrepareAndSwap(type, candidates, stagingRoot, cancellationToken);
		}
		catch (ResourceException)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			throw new ResourceException($"导入资源失败: {exception.Message}", exception);
		}
		finally
		{
			TryDeleteDirectory(stagingRoot);
		}
	}

	private IReadOnlyList<string> ImportFromDirectory(ResourceType type, string sourceDir, CancellationToken cancellationToken)
	{
		string stagingRoot = CreateStagingRoot();
		try
		{
			string canonicalSource = ResourcePathSafety.FullPath(sourceDir);
			if (ResourcePathSafety.IsSameOrWithin(canonicalSource, stagingRoot))
			{
				throw new ResourceException("导入目录不能包含应用 staging 目录");
			}
			IReadOnlyList<ModelCandidate> candidates = CollectCandidates(canonicalSource, cancellationToken);
			return PrepareAndSwap(type, candidates, stagingRoot, cancellationToken);
		}
		catch (ResourceException)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new ResourceException($"导入资源失败: {exception.Message}", exception);
		}
		finally
		{
			TryDeleteDirectory(stagingRoot);
		}
	}

	/// <summary>创建与目标目录同卷、且不属于 live2d 枚举范围的 staging 根.</summary>
	private string CreateStagingRoot()
	{
		Directory.CreateDirectory(ResourcesRoot);
		ResourcePathSafety.EnsureNoReparsePointsAlongPath(ResourcesRoot, "资源根目录包含符号链接或 reparse point");
		string root = Path.Combine(ResourcesRoot, $".nori-import-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		ResourcePathSafety.EnsureNoReparsePointsAlongPath(root, "staging 目录包含符号链接或 reparse point");
		return root;
	}

	/// <summary>
	/// 安全遍历候选目录并拒绝任意符号链接或 reparse point.
	/// </summary>
	private static IReadOnlyList<ModelCandidate> CollectCandidates(string sourceRoot, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string canonicalRoot = ResourcePathSafety.FullPath(sourceRoot);
		if (!Directory.Exists(canonicalRoot)) throw new ResourceException($"导入目录不存在: {sourceRoot}");
		ResourcePathSafety.EnsureNoReparsePointsAlongPath(canonicalRoot, "导入目录包含符号链接或 reparse point");

		List<string> modelFiles = [];
		CollectFiles(canonicalRoot, canonicalRoot, modelFiles, cancellationToken);
		if (modelFiles.Count == 0)
		{
			throw new ResourceException("所选资源中未找到任何 .model3.json 模型定义文件");
		}

		List<ModelCandidate> candidates = [];
		HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
		foreach (string modelFile in modelFiles.OrderBy(path => path, StringComparer.Ordinal))
		{
			cancellationToken.ThrowIfCancellationRequested();
			string relativePath = Path.GetRelativePath(canonicalRoot, modelFile);
			string? resolvedId = SupportedModelIds.ResolveFromModelPath(relativePath);
			if (resolvedId is null)
			{
				throw new ResourceException($"不支持的 Live2D 模型 ID: {relativePath}");
			}
			string modelId = ResourceName.Validate(resolvedId);
			if (!ids.Add(modelId))
			{
				throw new ResourceException($"导入资源中存在重复模型 ID: {modelId}");
			}

			string modelDir = Path.GetDirectoryName(modelFile) ?? canonicalRoot;
			candidates.Add(new ModelCandidate(modelId, modelDir, Path.GetFileName(modelFile)));
		}
		return candidates;
	}

	private static void CollectFiles(
		string sourceRoot,
		string currentDir,
		List<string> modelFiles,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ResourcePathSafety.EnsureNoReparsePoints(sourceRoot, currentDir, "导入目录包含符号链接或 reparse point");
		DirectoryInfo directory = new(currentDir);
		EnumerationOptions options = new()
		{
			IgnoreInaccessible = false,
			RecurseSubdirectories = false,
			ReturnSpecialDirectories = false,
			AttributesToSkip = 0,
		};
		foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", options))
		{
			cancellationToken.ThrowIfCancellationRequested();
			ResourcePathSafety.EnsureNoReparsePoints(sourceRoot, entry.FullName, "导入目录包含符号链接或 reparse point");
			if (entry is DirectoryInfo)
			{
				CollectFiles(sourceRoot, entry.FullName, modelFiles, cancellationToken);
			}
			else if (entry is FileInfo file
				&& file.Name.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase))
			{
				modelFiles.Add(file.FullName);
			}
		}
	}

	/// <summary>将所有候选复制到 staging/models 并完整验证, 再批量交换 target.</summary>
	private IReadOnlyList<string> PrepareAndSwap(
		ResourceType type,
		IReadOnlyList<ModelCandidate> candidates,
		string stagingRoot,
		CancellationToken cancellationToken)
	{
		string preparedRoot = Path.Combine(stagingRoot, "prepared");
		Directory.CreateDirectory(preparedRoot);
		List<string> preparedIds = [];

		foreach (ModelCandidate candidate in candidates)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (type == ResourceType.Live2D && !SupportedModelIds.IsSupported(candidate.Id))
			{
				throw new ResourceException($"不支持的 Live2D 模型 ID: {candidate.Id}");
			}
			string preparedDir = Path.Combine(preparedRoot, candidate.Id);
			CopyDirectory(candidate.SourceDir, preparedDir, cancellationToken);
			string preparedModelPath = Path.Combine(preparedDir, candidate.Model3FileName);
			Model3ReferenceValidator.Validate(preparedDir, preparedModelPath, cancellationToken);
			if (!IsInstalledAt(type, preparedDir))
			{
				throw new ResourceException($"模型 staging 校验失败: {candidate.Id}");
			}
			preparedIds.Add(candidate.Id);
		}

		if (preparedIds.Count == 0) throw new ResourceException("未能成功解析和导入任何模型");
		return SwapPrepared(type, preparedIds, preparedRoot, cancellationToken);
	}

	/// <summary>
	/// 同卷目录交换. backup 在所有 target 完成替换前一直保留, 任一失败逆序回滚.
	/// </summary>
	private IReadOnlyList<string> SwapPrepared(
		ResourceType type,
		IReadOnlyList<string> modelIds,
		string preparedRoot,
		CancellationToken cancellationToken)
	{
		string targetRoot = Path.Combine(ResourcesRoot, type.AsString());
		string backupRoot = Path.Combine(ResourcesRoot, $".nori-backup-{Guid.NewGuid():N}");
		Directory.CreateDirectory(targetRoot);
		Directory.CreateDirectory(backupRoot);
		ResourcePathSafety.EnsureNoReparsePointsAlongPath(ResourcesRoot, "资源目标目录包含符号链接或 reparse point");
		ResourcePathSafety.EnsureNoReparsePoints(ResourcesRoot, targetRoot, "资源目标目录包含符号链接或 reparse point");
		ResourcePathSafety.EnsureNoReparsePoints(ResourcesRoot, backupRoot, "资源备份目录包含符号链接或 reparse point");

		List<(string Target, string Backup)> backups = [];
		List<string> installedTargets = [];
		bool preserveBackupOnFailure = false;
		try
		{
			foreach (string modelId in modelIds)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string targetDir = ResourceDir(type, modelId);
				string preparedDir = Path.Combine(preparedRoot, modelId);
				ResourcePathSafety.EnsureNoReparsePoints(ResourcesRoot, targetDir, "资源目标包含符号链接或 reparse point");
				if (Directory.Exists(targetDir))
				{
					string backupDir = Path.Combine(backupRoot, modelId);
					Directory.Move(targetDir, backupDir);
					backups.Add((targetDir, backupDir));
				}

				cancellationToken.ThrowIfCancellationRequested();
				Directory.Move(preparedDir, targetDir);
				installedTargets.Add(targetDir);
			}

			TryDeleteDirectory(backupRoot);
			return [.. modelIds];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or ResourceException)
		{
			// 先保留备份；只有回滚完整成功后才允许清理，避免回滚失败时丢掉唯一旧模型。
			preserveBackupOnFailure = true;
			try
			{
				RollbackSwap(installedTargets, backups);
				preserveBackupOnFailure = false;
			}
			catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
			{
				throw new ResourceException($"导入资源失败且回滚失败，旧模型备份保留在: {backupRoot}", rollbackException);
			}

			if (exception is OperationCanceledException or ResourceException) throw;
			throw new ResourceException($"导入资源失败: {exception.Message}", exception);
		}
		finally
		{
			if (!preserveBackupOnFailure) TryDeleteDirectory(backupRoot);
		}
	}

	/// <summary>删除新 target 并按逆序恢复旧 backup.</summary>
	private static void RollbackSwap(
		IReadOnlyList<string> installedTargets,
		IReadOnlyList<(string Target, string Backup)> backups)
	{
		for (int index = backups.Count - 1; index >= 0; index--)
		{
			if (!Directory.Exists(backups[index].Backup))
				throw new IOException($"资源回滚备份不存在: {backups[index].Backup}");
		}

		for (int index = installedTargets.Count - 1; index >= 0; index--)
		{
			string target = installedTargets[index];
			if (Directory.Exists(target)) Directory.Delete(target, true);
		}

		for (int index = backups.Count - 1; index >= 0; index--)
		{
			(string target, string backup) = backups[index];
			if (Directory.Exists(target)) Directory.Delete(target, true);
			if (!Directory.Exists(backup))
				throw new IOException($"资源回滚备份不存在: {backup}");
			Directory.Move(backup, target);
			if (!Directory.Exists(target))
				throw new IOException($"资源回滚后目标目录不存在: {target}");
		}
	}

	private static void CopyDirectory(string sourceDir, string targetDir, CancellationToken cancellationToken)
	{
		string canonicalSource = ResourcePathSafety.FullPath(sourceDir);
		string canonicalTarget = ResourcePathSafety.FullPath(targetDir);
		if (!Directory.Exists(canonicalSource)) throw new ResourceException($"模型目录不存在: {sourceDir}");
		ResourcePathSafety.EnsureNoReparsePointsAlongPath(canonicalSource, "模型目录包含符号链接或 reparse point");
		ResourcePathSafety.EnsureNoReparsePoints(canonicalSource, canonicalSource, "模型目录包含符号链接或 reparse point");
		Directory.CreateDirectory(canonicalTarget);
		ResourcePathSafety.EnsureNoReparsePointsAlongPath(canonicalTarget, "模型 staging 路径包含符号链接或 reparse point");
		CopyDirectoryContents(canonicalSource, canonicalSource, canonicalTarget, cancellationToken);
	}

	private static void CopyDirectoryContents(
		string sourceRoot,
		string sourceDir,
		string targetRoot,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnumerationOptions options = new()
		{
			IgnoreInaccessible = false,
			RecurseSubdirectories = false,
			ReturnSpecialDirectories = false,
			AttributesToSkip = 0,
		};
		foreach (FileSystemInfo entry in new DirectoryInfo(sourceDir).EnumerateFileSystemInfos("*", options))
		{
			cancellationToken.ThrowIfCancellationRequested();
			ResourcePathSafety.EnsureNoReparsePoints(sourceRoot, entry.FullName, "模型目录包含符号链接或 reparse point");
			string relative = Path.GetRelativePath(sourceRoot, entry.FullName);
			string destination = Path.GetFullPath(Path.Combine(targetRoot, relative));
			ResourcePathSafety.EnsureContained(targetRoot, destination, "模型 staging 路径超出目标目录");
			if (entry is DirectoryInfo)
			{
				Directory.CreateDirectory(destination);
				CopyDirectoryContents(sourceRoot, entry.FullName, targetRoot, cancellationToken);
			}
			else if (entry is FileInfo)
			{
				CopyFile(entry.FullName, destination, cancellationToken);
			}
		}
	}

	private static void CopyFile(string source, string destination, CancellationToken cancellationToken)
	{
		string parent = Path.GetDirectoryName(destination) ?? throw new ResourceException($"模型文件没有父目录: {destination}");
		Directory.CreateDirectory(parent);
		using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
		using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
		byte[] buffer = new byte[64 * 1024];
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int read = input.Read(buffer, 0, buffer.Length);
			if (read == 0) break;
			output.Write(buffer, 0, read);
		}
	}

	private static bool IsInstalledAt(ResourceType type, string dir)
	{
		if (type != ResourceType.Live2D) return Directory.Exists(dir);
		try
		{
			List<string> modelFiles = [];
			CollectFiles(dir, dir, modelFiles, CancellationToken.None);
			if (modelFiles.Count != 1) return false;
			Model3ReferenceValidator.Validate(dir, modelFiles[0]);
			return true;
		}
		catch (Exception exception) when (exception is ResourceException or IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>递归计算目录大小, 遇到链接或不可读目录时返回 0.</summary>
	private static long DirectorySize(string dir)
	{
		try
		{
			List<string> files = [];
			CollectFilesForSize(dir, dir, files);
			long size = 0;
			foreach (string file in files) size = checked(size + new FileInfo(file).Length);
			return size;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException or ResourceException)
		{
			return 0;
		}
	}

	private static void CollectFilesForSize(string root, string current, List<string> files)
	{
		ResourcePathSafety.EnsureNoReparsePoints(root, current, "资源目录包含符号链接或 reparse point");
		EnumerationOptions options = new()
		{
			IgnoreInaccessible = false,
			RecurseSubdirectories = false,
			ReturnSpecialDirectories = false,
			AttributesToSkip = 0,
		};
		foreach (FileSystemInfo entry in new DirectoryInfo(current).EnumerateFileSystemInfos("*", options))
		{
			ResourcePathSafety.EnsureNoReparsePoints(root, entry.FullName, "资源目录包含符号链接或 reparse point");
			if (entry is DirectoryInfo) CollectFilesForSize(root, entry.FullName, files);
			else if (entry is FileInfo file) files.Add(file.FullName);
		}
	}

	private static IEnumerable<DirectoryInfo> EnumerateDirectoriesSafely(string root)
	{
		ResourcePathSafety.EnsureNoReparsePoints(root, root, "资源目录包含符号链接或 reparse point");
		EnumerationOptions options = new()
		{
			IgnoreInaccessible = false,
			RecurseSubdirectories = false,
			ReturnSpecialDirectories = false,
			AttributesToSkip = 0,
		};
		foreach (FileSystemInfo entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
		{
			if (entry is not DirectoryInfo directory) continue;
			ResourcePathSafety.EnsureNoReparsePoints(root, entry.FullName, "资源目录包含符号链接或 reparse point");
			yield return directory;
		}
	}

	private static void TryDeleteDirectory(string path)
	{
		if (!Directory.Exists(path)) return;
		try
		{
			Directory.Delete(path, true);
		}
		catch (IOException)
		{
			// staging/backup 清理失败不改变已经完成的交换结果.
		}
		catch (UnauthorizedAccessException)
		{
			// staging/backup 清理失败不改变已经完成的交换结果.
		}
	}

	private sealed record ModelCandidate(string Id, string SourceDir, string Model3FileName);
}
