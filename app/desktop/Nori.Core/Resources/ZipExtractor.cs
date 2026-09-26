using System.IO.Compression;

namespace Nori.Core.Resources;

/// <summary>
/// ZIP 安全解压.
///
/// 除了路径与符号链接校验, 还限制条目数量、单文件展开大小、总展开大小与压缩比.
/// 这是把不可信本地压缩包落盘的唯一入口, 拒绝规则不要放宽.
/// </summary>
public static class ZipExtractor
{
	/// <summary>默认 ZIP 解压上限.</summary>
	public static ZipExtractionLimits DefaultLimits { get; } = new();

	/// <summary>
	/// 清理并校验 ZIP 内部路径.
	/// ZIP 标准通常用 `/`, 但 Windows 打的包也可能出现 `\`.
	/// </summary>
	public static string SanitizePath(string raw)
	{
		if (raw.Length == 0) return string.Empty;
		string normalized = raw.Replace('\\', '/');
		if (normalized.StartsWith("//", StringComparison.Ordinal)) throw new ResourceException($"ZIP 包包含 UNC 路径: {raw}");
		if (normalized.StartsWith('/')) throw new ResourceException($"ZIP 包包含绝对路径: {raw}");
		if (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':')
		{
			throw new ResourceException($"ZIP 包包含 Windows 绝对路径: {raw}");
		}
		List<string> parts = [];
		foreach (string part in normalized.Split('/'))
		{
			if (part.Length == 0 || part == ".") continue;
			if (part == "..") throw new ResourceException($"ZIP 条目包含路径穿越: {raw}");
			if (part.Any(char.IsControl)) throw new ResourceException($"ZIP 条目包含非法字符: {raw}");
			parts.Add(part);
		}
		return string.Join('/', parts);
	}

	/// <summary>目录条目的名称部分为空。文件条目即使路径以分隔符写法出现，也不靠后缀判断。</summary>
	public static bool IsDirectoryEntry(ZipArchiveEntry entry) => entry.Name.Length == 0;

	/// <summary>
	/// 找出所有条目共有的唯一顶层目录.
	/// 没有共同顶层目录 (或顶层就是文件) 时返回 null.
	/// </summary>
	public static string? FindCommonTopDirectory(IEnumerable<string> entryPaths)
	{
		string? top = null;
		bool any = false;
		foreach (string path in entryPaths)
		{
			if (path.Length == 0) continue;
			int slash = path.IndexOf('/');
			if (slash < 0) return null;
			string head = path[..slash];
			if (top is null) top = head;
			else if (!string.Equals(top, head, StringComparison.Ordinal)) return null;
			any = true;
		}
		return any ? top : null;
	}

	/// <summary>使用默认上限安全解压.</summary>
	public static void Extract(string zipPath, string targetDir) =>
		Extract(zipPath, targetDir, CancellationToken.None, null);

	/// <summary>使用默认上限安全解压并支持取消.</summary>
	public static void Extract(string zipPath, string targetDir, CancellationToken cancellationToken) =>
		Extract(zipPath, targetDir, cancellationToken, null);

	/// <summary>使用自定义上限安全解压.</summary>
	public static void Extract(string zipPath, string targetDir, ZipExtractionLimits limits) =>
		Extract(zipPath, targetDir, CancellationToken.None, limits);

	/// <summary>
	/// 安全解压到目标目录.
	/// 会自动剥掉多余的唯一顶层目录; 写入前后都会检查父目录没有链接越界.
	/// </summary>
	public static void Extract(
		string zipPath,
		string targetDir,
		CancellationToken cancellationToken,
		ZipExtractionLimits? limits)
	{
		ZipExtractionLimits effectiveLimits = limits ?? DefaultLimits;
		effectiveLimits.Validate();
		cancellationToken.ThrowIfCancellationRequested();

		string canonicalZipPath = Path.GetFullPath(zipPath);
		if (Path.GetDirectoryName(canonicalZipPath) is null)
		{
			throw new ResourceException($"ZIP 文件没有父目录: {zipPath}");
		}
		ResourcePathSafety.EnsureNoReparsePointsAlongPath(canonicalZipPath, "ZIP 文件路径包含符号链接或 reparse point");
		if (!File.Exists(canonicalZipPath)) throw new ResourceException($"ZIP 文件不存在: {zipPath}");

		string canonicalTarget = ResourcePathSafety.FullPath(targetDir);
		Directory.CreateDirectory(canonicalTarget);
		ResourcePathSafety.EnsureNoReparsePointsAlongPath(canonicalTarget, "ZIP 目标目录包含符号链接或 reparse point");

		ZipArchive archive;
		try
		{
			archive = ZipFile.OpenRead(canonicalZipPath);
		}
		catch (InvalidDataException exception)
		{
			throw new ResourceException($"ZIP 文件格式无效: {zipPath}", exception);
		}

		using (archive)
		{
			if (archive.Entries.Count > effectiveLimits.MaxEntryCount)
			{
				throw new ResourceException($"ZIP 条目数量超过上限: {effectiveLimits.MaxEntryCount}");
			}

			List<(ZipArchiveEntry Entry, string Path, bool IsDirectory)> entries = [];
			long totalUncompressed = 0;
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string sanitized = SanitizePath(entry.FullName);
				if (sanitized.Length == 0) continue;
				bool isDirectory = IsDirectoryEntry(entry);
				if (!isDirectory)
				{
					if (entry.Length < 0 || entry.Length > effectiveLimits.MaxSingleFileBytes)
					{
						throw new ResourceException($"ZIP 单个文件展开大小超过上限: {entry.FullName}");
					}
					try
					{
						totalUncompressed = checked(totalUncompressed + entry.Length);
					}
					catch (OverflowException exception)
					{
						throw new ResourceException("ZIP 总展开大小超过可处理范围", exception);
					}
					if (totalUncompressed > effectiveLimits.MaxTotalUncompressedBytes)
					{
						throw new ResourceException($"ZIP 总展开大小超过上限: {effectiveLimits.MaxTotalUncompressedBytes}");
					}
					if (entry.Length > 0 && (entry.CompressedLength <= 0
						|| entry.Length / (double)entry.CompressedLength > effectiveLimits.MaxCompressionRatio))
					{
						throw new ResourceException($"ZIP 条目压缩比异常: {entry.FullName}");
					}
				}
				entries.Add((entry, sanitized, isDirectory));
			}

			string? commonTop = FindCommonTopDirectory(entries
				.Where(item => !item.IsDirectory)
				.Select(item => item.Path));
			HashSet<string> outputPaths = new(OperatingSystem.IsWindows()
				? StringComparer.OrdinalIgnoreCase
				: StringComparer.Ordinal);

			foreach ((ZipArchiveEntry entry, string sanitized, bool isDirectory) in entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string relative = StripCommonTop(sanitized, commonTop, entry.FullName);
				if (relative.Length == 0) continue;
				string outPath;
				try
				{
					outPath = Path.GetFullPath(Path.Combine(canonicalTarget, relative.Replace('/', Path.DirectorySeparatorChar)));
				}
				catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
				{
					throw new ResourceException($"ZIP 条目路径无效: {entry.FullName}", exception);
				}
				ResourcePathSafety.EnsureContained(canonicalTarget, outPath, $"ZIP 条目超出目标目录: {entry.FullName}");
				if (!outputPaths.Add(outPath)) throw new ResourceException($"ZIP 包含重复条目: {entry.FullName}");

				if (IsSymlink(entry)) throw new ResourceException($"ZIP 包包含不允许的符号链接: {entry.FullName}");
				if (isDirectory)
				{
					ResourcePathSafety.EnsureNoReparsePoints(canonicalTarget, outPath, $"ZIP 目录包含符号链接或 reparse point: {entry.FullName}");
					Directory.CreateDirectory(outPath);
					ResourcePathSafety.EnsureNoReparsePoints(canonicalTarget, outPath, $"ZIP 目录包含符号链接或 reparse point: {entry.FullName}");
					continue;
				}

				string parent = Path.GetDirectoryName(outPath)
					?? throw new ResourceException($"ZIP 条目没有父目录: {entry.FullName}");
				ResourcePathSafety.EnsureNoReparsePoints(canonicalTarget, parent, $"ZIP 父目录包含符号链接或 reparse point: {entry.FullName}");
				Directory.CreateDirectory(parent);
				ResourcePathSafety.EnsureNoReparsePoints(canonicalTarget, parent, $"ZIP 父目录包含符号链接或 reparse point: {entry.FullName}");
				ExtractFile(entry, outPath, cancellationToken, effectiveLimits);
			}
		}
	}

	private static string StripCommonTop(string path, string? commonTop, string originalPath)
	{
		if (commonTop is null) return path;
		if (path.Equals(commonTop, StringComparison.Ordinal)) return string.Empty;
		string prefix = commonTop + "/";
		if (!path.StartsWith(prefix, StringComparison.Ordinal))
		{
			throw new ResourceException($"ZIP 条目顶层目录不一致: {originalPath}");
		}
		return path[prefix.Length..];
	}

	private static void ExtractFile(
		ZipArchiveEntry entry,
		string outPath,
		CancellationToken cancellationToken,
		ZipExtractionLimits limits)
	{
		try
		{
			using Stream input = entry.Open();
			using FileStream output = new(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
			byte[] buffer = new byte[64 * 1024];
			long copied = 0;
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				int read = input.Read(buffer, 0, buffer.Length);
				if (read == 0) break;
				copied = checked(copied + read);
				if (copied > entry.Length || copied > limits.MaxSingleFileBytes)
				{
					throw new ResourceException($"ZIP 条目实际展开大小超过上限: {entry.FullName}");
				}
				output.Write(buffer, 0, read);
			}
			if (copied != entry.Length)
			{
				throw new ResourceException($"ZIP 条目展开长度不匹配: {entry.FullName}");
			}
		}
		catch
		{
			try { File.Delete(outPath); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
			throw;
		}
	}

	/// <summary>判断 ZIP 条目是否是 Unix 符号链接.</summary>
	private static bool IsSymlink(ZipArchiveEntry entry)
	{
		int mode = entry.ExternalAttributes >> 16;
		return (mode & 0xF000) == 0xA000;
	}
}
