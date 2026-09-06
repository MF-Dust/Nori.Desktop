using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nori.Core.Data;

namespace Nori.Core.Update;

/// <summary>部署槽 deployment.json 元数据。</summary>
public sealed record SlotManifest(
	int SchemaVersion,
	string ProductVersion,
	string NumericVersion,
	int Revision,
	string Rid,
	string Entrypoint);

/// <summary>提交成功的部署槽结果。</summary>
public sealed record SlotCommitResult(
	string SlotName,
	string SlotPath,
	SlotManifest Manifest);

/// <summary>
/// 安全解压并原子提交部署槽。
///
/// 遵循规则：
/// 1. 不可直接覆盖 PackageRoot；先在 staging 隔离解压与校验；
/// 2. 解压拒绝路径穿越、绝对路径、UNC 和符号链接越界；
/// 3. 单文件 512MiB、总展开 1024MiB 有界流式复制与重复条目检测；
/// 4. 槽内 deployment.json 必须与 UPDATE 清单元数据完全一致；
/// 5. 提交前必须验证当前运行槽名，并原子固定 .current 指针，防止正式槽重命名与指针写入间产生空窗；
/// 6. 目标槽已存在时绝对禁止移动到 .destroy 或覆盖（保留所有旧槽并防崩溃），仅当校验完全一致时安全复用；
/// 7. 强制拒绝当前槽和降级版本。
/// </summary>
public static class UpdateExtractor
{
	private static readonly Regex SlotDirPattern = new("^app-(?<version>[0-9]+\\.[0-9]+\\.[0-9]+)-(?<revision>[0-9]+)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
	public const int MaxEntries = 8192;
	public const long MaxSingleFileBytes = 512L * 1024 * 1024; // 512 MiB
	public const long MaxTotalBytes = 1024L * 1024 * 1024; // 1024 MiB
	public const double MaxCompressionRatio = 200.0;

	/// <summary>安全解压更新包并原子提交到发布包根目录。</summary>
	public static SlotCommitResult ExtractAndCommitSlot(
		string archivePath,
		string packageRoot,
		string expectedRid,
		string currentRunningSlotName,
		UpdateManifest expectedManifest,
		string stagingDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedRid);
		ArgumentException.ThrowIfNullOrWhiteSpace(currentRunningSlotName);
		ArgumentNullException.ThrowIfNull(expectedManifest);
		ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);

		if (!SlotDirPattern.IsMatch(currentRunningSlotName))
		{
			throw new InvalidOperationException($"当前运行槽名格式无效: {currentRunningSlotName}");
		}

		string fullArchive = Path.GetFullPath(archivePath);
		string fullPackageRoot = Path.GetFullPath(packageRoot);
		string fullStaging = Path.GetFullPath(stagingDirectory);

		if (!File.Exists(fullArchive)) throw new FileNotFoundException($"更新包文件不存在: {fullArchive}");
		if (!Directory.Exists(fullPackageRoot)) throw new DirectoryNotFoundException($"发布包根目录不存在: {fullPackageRoot}");

		// 检查目标版本不得低于或等于当前运行版本
		Match curMatch = SlotDirPattern.Match(currentRunningSlotName);
		string curNumeric = curMatch.Groups["version"].Value;
		int curRevision = int.Parse(curMatch.Groups["revision"].Value, System.Globalization.CultureInfo.InvariantCulture);

		if (!UpdateManifest.IsStrictlyNewer(curNumeric, curRevision, expectedManifest.NumericVersion, expectedManifest.Revision))
		{
			throw new InvalidOperationException($"拒绝安装平级或降级版本: 当前 {currentRunningSlotName}，目标 {expectedManifest.NumericVersion}-{expectedManifest.Revision}");
		}

		EnsureContained(fullStaging, Path.Combine(fullPackageRoot, "data", "updates", "staging"));
		EnsureNoReparsePoints(fullArchive);
		EnsureNoReparsePoints(fullStaging, fullPackageRoot);
		EnsureNoReparsePoints(Path.Combine(fullPackageRoot, ".current"), fullPackageRoot);
		SlotManifest running = ReadAndValidateManifest(Path.Combine(fullPackageRoot, currentRunningSlotName), expectedRid);
		if (running.NumericVersion != curNumeric || running.Revision != curRevision)
			throw new InvalidOperationException("当前运行槽名称与部署清单不一致");
		if (Directory.Exists(fullStaging)) throw new InvalidOperationException("更新暂存目录已存在，拒绝覆盖");
		Directory.CreateDirectory(fullStaging);

		try
		{
			// 1. 解压到 staging 隔离目录（带限额、路径合法性与重复路径检测）
			if (expectedManifest.ArchiveType == "tar.gz")
			{
				ExtractTarGz(fullArchive, fullStaging, cancellationToken);
			}
			else if (expectedManifest.ArchiveType == "zip")
			{
				ExtractZip(fullArchive, fullStaging, cancellationToken);
			}
			else throw new InvalidOperationException("不支持的更新归档类型");

			// 2. 搜索并验证槽目录
			string slotDirectory = FindSlotDirectory(fullStaging);
			SlotManifest slotManifest = ReadAndValidateManifest(slotDirectory, expectedRid);

			// 3. 严格比对包内 manifest 与 UPDATE 清单的一致性
			ValidateManifestMatchesUpdate(slotManifest, expectedManifest);

			string slotName = $"app-{slotManifest.NumericVersion}-{slotManifest.Revision}";
			string dirName = Path.GetFileName(slotDirectory);
			if (!string.Equals(dirName, slotName, StringComparison.Ordinal))
			{
				throw new InvalidOperationException($"部署槽目录名与 manifest 不一致: 期望 {slotName}，实际 {dirName}");
			}

			string finalSlotPath = Path.Combine(fullPackageRoot, slotName);
			string partialSlotPath = Path.Combine(fullPackageRoot, slotName + $".{Guid.NewGuid():N}.partial");
			string currentPath = Path.Combine(fullPackageRoot, ".current");
			EnsureNoReparsePoints(finalSlotPath, fullPackageRoot);
			EnsureNoReparsePoints(currentPath, fullPackageRoot);

			// 4. 若最终槽已存在：禁止移入 .destroy 或直接覆盖（防止破坏正在运行的实例）
			if (Directory.Exists(finalSlotPath))
			{
				SlotManifest existing = ReadAndValidateManifest(finalSlotPath, expectedRid);
				ValidateManifestMatchesUpdate(existing, expectedManifest);

				// 元数据相同不足以证明内容完整，逐文件比较新包与已有槽。
				EnsureIdenticalContents(slotDirectory, finalSlotPath, cancellationToken);
				cancellationToken.ThrowIfCancellationRequested();
				WriteCurrent(currentPath, slotName);
				return new SlotCommitResult(slotName, finalSlotPath, existing);
			}

			// 5. 关键安全步骤：在重命名新槽前，必须先验证并原子固定当前运行槽指针
			// 防止新槽目录出现后与 .current 写入之间出现空窗而被意外选中
			cancellationToken.ThrowIfCancellationRequested();
			// 从此处起不可取消，保留旧槽指针直至新槽完整提交。
			WriteCurrent(currentPath, currentRunningSlotName);

			// 6. 同一包根内原子移动，不跨卷复制或覆盖已有目录。
			Directory.Move(slotDirectory, partialSlotPath);

			// 验证 partial 内 entrypoint 完整性
			ReadAndValidateManifest(partialSlotPath, expectedRid);

			// 7. 将 .partial 原子重命名为目标槽
			Directory.Move(partialSlotPath, finalSlotPath);

			// 8. 新槽完全提交后，最后原子写入 .current 指针指向新槽
			WriteCurrent(currentPath, slotName);

			return new SlotCommitResult(slotName, finalSlotPath, slotManifest);
		}
		finally
		{
			try
			{
				if (Directory.Exists(fullStaging)) Directory.Delete(fullStaging, recursive: true);
			}
			catch { }
		}
	}

	/// <summary>读取并验证槽内的 deployment.json 元数据与入口有效性。</summary>
	public static SlotManifest ReadAndValidateManifest(string slotDirectory, string expectedRid)
	{
		EnsureNoReparsePoints(slotDirectory);
		string manifestPath = Path.Combine(slotDirectory, "deployment.json");
		EnsureNoReparsePoints(manifestPath);
		if (File.Exists(manifestPath) && new FileInfo(manifestPath).Length > 1024 * 1024)
			throw new InvalidOperationException("部署清单大小超过限制");
		if (!File.Exists(manifestPath))
		{
			throw new InvalidOperationException($"部署槽内缺少 deployment.json: {manifestPath}");
		}

		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
		JsonElement root = document.RootElement;

		int schema = root.GetProperty("schema_version").GetInt32();
		if (schema != 1) throw new InvalidOperationException($"不支持的部署 schema: {schema}");

		string product = root.GetProperty("product_version").GetString()!;
		string numeric = root.GetProperty("numeric_version").GetString()!;
		int revision = root.GetProperty("revision").GetInt32();
		string rid = root.GetProperty("rid").GetString()!;
		string entrypoint = root.GetProperty("entrypoint").GetString()!;

		if (!Regex.IsMatch(numeric, "^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant))
		{
			throw new InvalidOperationException($"部署 manifest 中的数字版本无效: {numeric}");
		}

		if (!string.Equals(rid, expectedRid, StringComparison.Ordinal))
		{
			throw new InvalidOperationException($"部署槽架构不匹配: 期望 {expectedRid}，包内为 {rid}");
		}

		if (revision < 0 || string.IsNullOrWhiteSpace(product) || Path.IsPathRooted(entrypoint)
			|| entrypoint.Contains('\\') || entrypoint.Split('/').Any(part => part is "" or "." or "..")
			|| SanitizePath(entrypoint) != entrypoint)
			throw new InvalidOperationException("部署清单版本或入口无效");
		string fullSlot = Path.GetFullPath(slotDirectory);
		string entryPath = Path.GetFullPath(Path.Combine(fullSlot, entrypoint.Replace('/', Path.DirectorySeparatorChar)));
		if (!IsContained(entryPath, fullSlot) || !File.Exists(entryPath))
		{
			throw new InvalidOperationException($"部署入口不存在或超出槽目录: {entrypoint}");
		}

		EnsureNoReparsePoints(entryPath, fullSlot);
		return new SlotManifest(schema, product, numeric, revision, rid, entrypoint);
	}

	private static void ValidateManifestMatchesUpdate(SlotManifest slot, UpdateManifest update)
	{
		if (slot.SchemaVersion != update.SchemaVersion
			|| !string.Equals(slot.ProductVersion, update.ProductVersion, StringComparison.Ordinal)
			|| !string.Equals(slot.NumericVersion, update.NumericVersion, StringComparison.Ordinal)
			|| slot.Revision != update.Revision
			|| !string.Equals(slot.Rid, update.Rid, StringComparison.Ordinal)
			|| !string.Equals(slot.Entrypoint, update.Entrypoint, StringComparison.Ordinal))
		{
			throw new InvalidOperationException("部署槽内 deployment.json 与 UPDATE 清单元数据不一致，拒绝提交");
		}
	}

	private static string FindSlotDirectory(string searchRoot)
	{
		string? selected = null;
		foreach (string directory in Directory.EnumerateDirectories(searchRoot, "*", SearchOption.AllDirectories))
		{
			string dirName = Path.GetFileName(directory);
			if (SlotDirPattern.IsMatch(dirName) && File.Exists(Path.Combine(directory, "deployment.json")))
			{
				if (selected is not null) throw new InvalidOperationException("更新包包含多个部署槽");
				selected = Path.GetFullPath(directory);
			}
		}
		return selected ?? throw new InvalidOperationException("更新包中未包含有效的部署槽 (缺少匹配 app-<version>-<revision> 且包含 deployment.json 的目录)");
	}

	private static void ExtractZip(string zipPath, string targetDir, CancellationToken cancellationToken)
	{
		using ZipArchive archive = ZipFile.OpenRead(zipPath);
		if (archive.Entries.Count > MaxEntries) throw new InvalidOperationException($"ZIP 条目数超过上限 ({MaxEntries})");

		long totalBytes = 0;
		HashSet<string> seenPaths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string sanitized = SanitizePath(entry.FullName);
			if (sanitized.Length == 0) continue;

			string outPath = Path.GetFullPath(Path.Combine(targetDir, sanitized.Replace('/', Path.DirectorySeparatorChar)));
			EnsureContained(outPath, targetDir);

			if (!seenPaths.Add(outPath))
			{
				throw new InvalidOperationException($"归档包含重复条目路径: {sanitized}");
			}

			int unixMode = (entry.ExternalAttributes >> 16) & 0xffff;
			int fileType = unixMode & 0xf000;
			if ((fileType != 0 && fileType != 0x8000 && fileType != 0x4000) || (unixMode & 0xe00) != 0
				|| (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
				throw new InvalidOperationException($"ZIP 包含链接、特殊文件或特殊权限: {entry.FullName}");
			bool isDirectory = entry.Name.Length == 0;
			if (isDirectory)
			{
				Directory.CreateDirectory(outPath);
				continue;
			}

			string? parent = Path.GetDirectoryName(outPath);
			if (parent is not null) Directory.CreateDirectory(parent);

			if (entry.Length > MaxSingleFileBytes)
			{
				throw new InvalidOperationException($"条目展开大小超过单文件限制 ({MaxSingleFileBytes} 字节): {entry.FullName}");
			}
			if (entry.Length > 0 && (entry.CompressedLength <= 0 || entry.Length / (double)entry.CompressedLength > MaxCompressionRatio))
			{
				throw new InvalidOperationException($"条目压缩比异常: {entry.FullName}");
			}

			using Stream input = entry.Open();
			using FileStream output = new(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);

			byte[] buffer = new byte[64 * 1024];
			long entryCopied = 0;
			int read;
			while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				entryCopied = checked(entryCopied + read);
				if (entryCopied > MaxSingleFileBytes)
				{
					throw new InvalidOperationException($"条目实际展开大小超出上限: {entry.FullName}");
				}
				totalBytes = checked(totalBytes + read);
				if (totalBytes > MaxTotalBytes)
				{
					throw new InvalidOperationException($"展开总字节数超过上限 ({MaxTotalBytes} 字节)");
				}
				output.Write(buffer, 0, read);
			}

			if (entry.Length >= 0 && entryCopied != entry.Length)
			{
				throw new InvalidOperationException($"条目展开长度与元数据不匹配: {entry.FullName}");
			}
			if (!OperatingSystem.IsWindows() && unixMode != 0) File.SetUnixFileMode(outPath, (UnixFileMode)(unixMode & 0x1ff));
		}
	}

	private static void ExtractTarGz(string tarGzPath, string targetDir, CancellationToken cancellationToken)
	{
		using FileStream fs = File.OpenRead(tarGzPath);
		using GZipStream gz = new(fs, CompressionMode.Decompress);
		using TarReader reader = new(gz);

		int entryCount = 0;
		long totalBytes = 0;
		HashSet<string> seenPaths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

		while (reader.GetNextEntry() is { } entry)
		{
			cancellationToken.ThrowIfCancellationRequested();
			entryCount++;
			if (entryCount > MaxEntries) throw new InvalidOperationException($"TAR 条目数超过上限 ({MaxEntries})");

			string sanitized = SanitizePath(entry.Name);
			if (sanitized.Length == 0) continue;

			string outPath = Path.GetFullPath(Path.Combine(targetDir, sanitized.Replace('/', Path.DirectorySeparatorChar)));
			EnsureContained(outPath, targetDir);

			if (!seenPaths.Add(outPath))
			{
				throw new InvalidOperationException($"归档包含重复条目路径: {sanitized}");
			}

			if (((int)entry.Mode & ~0x1ff) != 0) throw new InvalidOperationException($"TAR 包含特殊权限: {entry.Name}");
			if (entry.EntryType == TarEntryType.Directory)
			{
				Directory.CreateDirectory(outPath);
			}
			else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
			{
				if (entry.Length > MaxSingleFileBytes) throw new InvalidOperationException($"TAR 文件大小超过单文件限制: {entry.Name}");

				string? parent = Path.GetDirectoryName(outPath);
				if (parent is not null) Directory.CreateDirectory(parent);

				if (entry.DataStream is null && entry.Length != 0) throw new InvalidOperationException("TAR 条目缺少内容");
				if (entry.DataStream is not null)
				{
					using FileStream output = new(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
					byte[] buffer = new byte[64 * 1024];
					long entryCopied = 0;
					int read;
					while ((read = entry.DataStream.Read(buffer, 0, buffer.Length)) > 0)
					{
						cancellationToken.ThrowIfCancellationRequested();
						entryCopied = checked(entryCopied + read);
						if (entryCopied > MaxSingleFileBytes)
						{
							throw new InvalidOperationException($"TAR 条目实际展开大小超出单文件限制: {entry.Name}");
						}
						totalBytes = checked(totalBytes + read);
						if (totalBytes > MaxTotalBytes)
						{
							throw new InvalidOperationException($"TAR 展开总字节数超过上限 ({MaxTotalBytes} 字节)");
						}
						output.Write(buffer, 0, read);
					}
					if (entry.Length >= 0 && entryCopied != entry.Length)
					{
						throw new InvalidOperationException($"TAR 条目展开长度不匹配: {entry.Name}");
					}
				}
				else File.WriteAllBytes(outPath, []);
				if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(outPath, entry.Mode);
			}
			else throw new InvalidOperationException($"TAR 包含链接或特殊文件: {entry.Name}");
		}
		if (totalBytes > new FileInfo(tarGzPath).Length * MaxCompressionRatio)
			throw new InvalidOperationException("TAR 压缩比异常");
	}

	private static string SanitizePath(string raw)
	{
		if (raw.Length == 0) return string.Empty;
		string normalized = raw.Replace('\\', '/');
		if (normalized.StartsWith("//", StringComparison.Ordinal) || normalized.StartsWith('/'))
		{
			throw new InvalidOperationException($"归档包含绝对或 UNC 路径: {raw}");
		}
		if (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':')
		{
			throw new InvalidOperationException($"归档包含盘符绝对路径: {raw}");
		}
		List<string> parts = [];
		foreach (string part in normalized.Split('/'))
		{
			if (part.Length == 0 || part == ".") continue;
			if (part == "..") throw new InvalidOperationException($"归档条目包含路径穿越: {raw}");
			if (part.Any(char.IsControl) || part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || part.EndsWith('.') || part.EndsWith(' ')
				|| Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
				throw new InvalidOperationException($"归档条目包含不安全文件名: {raw}");
			parts.Add(part);
		}
		return string.Join('/', parts);
	}

	private static void EnsureContained(string path, string root)
	{
		if (!IsContained(path, root))
		{
			throw new InvalidOperationException($"路径超出目标目录范围: {path}");
		}
	}

	private static bool IsContained(string path, string root)
	{
		StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(fullPath, fullRoot, cmp) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, cmp);
	}

	/// <summary>拒绝目标及任意父目录中的符号链接或重解析点。</summary>
	internal static void EnsureNoReparsePoints(string path, string? boundaryRoot = null)
	{
		if (boundaryRoot is not null)
		{
			AppStoragePaths.EnsureNoReparsePoints(path, boundaryRoot);
			return;
		}

		string fullPath = Path.GetFullPath(path);
		try
		{
			if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
				throw new InvalidOperationException($"更新路径包含符号链接或重解析点: {fullPath}");
		}
		catch (FileNotFoundException) { }
		catch (DirectoryNotFoundException) { }
	}

	private static void WriteCurrent(string path, string slot)
	{
		EnsureNoReparsePoints(path);
		string temp = path + $".{Guid.NewGuid():N}.tmp";
		try
		{
			using (FileStream file = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				file.Write(System.Text.Encoding.UTF8.GetBytes(slot + "\n"));
				file.Flush(true);
			}
			File.Move(temp, path, overwrite: true);
		}
		finally { if (File.Exists(temp)) File.Delete(temp); }
	}

	private static void EnsureIdenticalContents(string source, string existing, CancellationToken token)
	{
		string[] sourceEntries = Directory.GetFileSystemEntries(source).Order(StringComparer.Ordinal).ToArray();
		string[] existingEntries = Directory.GetFileSystemEntries(existing).Order(StringComparer.Ordinal).ToArray();
		if (sourceEntries.Length != existingEntries.Length) throw new InvalidOperationException("已有部署槽内容与更新包不一致");
		for (int i = 0; i < sourceEntries.Length; i++)
		{
			token.ThrowIfCancellationRequested();
			string left = sourceEntries[i];
			string right = existingEntries[i];
			EnsureNoReparsePoints(right);
			if (Path.GetFileName(left) != Path.GetFileName(right) || Directory.Exists(left) != Directory.Exists(right))
				throw new InvalidOperationException("已有部署槽内容与更新包不一致");
			if (Directory.Exists(left)) EnsureIdenticalContents(left, right, token);
			else
			{
				using FileStream first = File.OpenRead(left);
				using FileStream second = File.OpenRead(right);
				if (first.Length != second.Length || !SHA256.HashData(first).SequenceEqual(SHA256.HashData(second))
					|| (!OperatingSystem.IsWindows() && File.GetUnixFileMode(left) != File.GetUnixFileMode(right)))
					throw new InvalidOperationException("已有部署槽内容与更新包不一致");
			}
		}
	}
}
