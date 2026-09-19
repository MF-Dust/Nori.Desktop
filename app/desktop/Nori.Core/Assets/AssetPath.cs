namespace Nori.Core.Assets;

/// <summary>
/// 资源路径处理
///
/// 对应 Rust 版 asset.rs 的路径安全逻辑, 逐条移植, 不要放宽:
/// 百分号解码 / 绝对路径与 UNC 与盘符拒绝 / .. 与 . 拒绝 / 控制字符拒绝 / 少一层目录的候选重试
/// </summary>
public static class AssetPath
{
	/// <summary>
	/// 百分号解码. `%` 后面不是合法 HEX 时返回 null (视为非法请求)
	/// </summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S127", Justification = "百分号编码会消费三个字符，UTF-8 分支会消费一个字符。")]
	public static string? PercentDecode(string input)
	{
		byte[] bytes = new byte[input.Length];
		int length = 0;
		for (int index = 0; index < input.Length;)
		{
			char current = input[index];
			if (current == '%')
			{
				if (index + 2 >= input.Length) return null;
				int high = HexValue(input[index + 1]);
				int low = HexValue(input[index + 2]);
				if (high < 0 || low < 0) return null;
				bytes[length++] = (byte)((high << 4) | low);
				index += 3;
				continue;
			}
			// 非 ASCII 字符按 UTF-8 展开
			if (current > 0x7F)
			{
				byte[] encoded = System.Text.Encoding.UTF8.GetBytes(current.ToString());
				if (length + encoded.Length > bytes.Length) Array.Resize(ref bytes, length + encoded.Length);
				encoded.CopyTo(bytes, length);
				length += encoded.Length;
				index++;
				continue;
			}
			bytes[length++] = (byte)current;
			index++;
		}
		try
		{
			return new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, length);
		}
		catch (ArgumentException)
		{
			// 解码结果不是合法 UTF-8
			return null;
		}
	}

	/// <summary>
	/// 判断解码后的路径是否为安全的相对路径
	/// </summary>
	public static bool IsSafeRelativePath(string path)
	{
		if (path.Length == 0) return false;
		// Unix 绝对路径
		if (path[0] == '/') return false;
		// Windows / UNC 绝对路径
		if (path[0] == '\\') return false;
		if (IsWindowsAbsolutePath(path)) return false;
		foreach (string segment in path.Split(new[] { '/', '\\' }))
		{
			if (segment.Length == 0) continue;
			if (segment is ".." or ".") return false;
		}
		return true;
	}

	/// <summary>
	/// 判断 Windows 风格绝对路径 (C:\foo / C:/foo / C:)
	/// </summary>
	public static bool IsWindowsAbsolutePath(string path) =>
		path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

	/// <summary>
	/// 生成资源路径候选
	///
	/// 原始路径优先, 之后从第二层开始逐个尝试删掉一层目录:
	/// live2d/arg-nori/arg-nori/model.json → live2d/arg-nori/model.json
	/// 用来兼容多包了一层顶层目录的资源包
	/// </summary>
	public static IReadOnlyList<string> PathCandidates(string path)
	{
		string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length < 3) return [path];
		List<string> candidates = [string.Join('/', segments)];
		for (int skip = 1; skip < segments.Length; skip++)
		{
			candidates.Add(string.Join('/', segments.Where((_, index) => index != skip)));
		}
		return candidates;
	}

	/// <summary>
	/// 根据文件扩展名返回 MIME
	/// </summary>
	public static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
	{
		".json" => "application/json; charset=utf-8",
		".png" => "image/png",
		".jpg" or ".jpeg" => "image/jpeg",
		".webp" => "image/webp",
		".gif" => "image/gif",
		".svg" => "image/svg+xml",
		".moc3" => "application/octet-stream",
		".motion3" => "application/json; charset=utf-8",
		".physics3" => "application/json; charset=utf-8",
		".exp3" => "application/json; charset=utf-8",
		".zip" => "application/zip",
		".mp3" => "audio/mpeg",
		".wav" => "audio/wav",
		".ogg" => "audio/ogg",
		".mp4" => "video/mp4",
		// 前端 bundle 用得到, Rust 版没有是因为页面走的是 Tauri 内建协议
		".html" => "text/html; charset=utf-8",
		".js" or ".mjs" => "text/javascript; charset=utf-8",
		".css" => "text/css; charset=utf-8",
		".woff2" => "font/woff2",
		".woff" => "font/woff",
		".ttf" => "font/ttf",
		".ico" => "image/x-icon",
		".map" => "application/json; charset=utf-8",
		_ => "application/octet-stream",
	};

	/// <summary>
	/// 把相对路径解析成根目录内的真实文件,
	/// 解析失败 / 不是文件 / 越出根目录 一律返回 null
	/// </summary>
	public static string? Resolve(string root, string relative)
	{
		if (!IsSafeRelativePath(relative)) return null;
		string canonicalRoot;
		try
		{
			canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
		foreach (string candidate in PathCandidates(relative))
		{
			string full;
			try
			{
				full = Path.GetFullPath(Path.Combine(canonicalRoot, candidate.Replace('/', Path.DirectorySeparatorChar)));
			}
			catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
			{
				continue;
			}
			// 防止路径穿越: 必须仍在根目录内
			if (!full.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
			if (!File.Exists(full)) continue;
			// 防止 symlink 逃逸: 解析链接后再查一次
			try
			{
				string real = Path.GetFullPath(File.ResolveLinkTarget(full, true)?.FullName ?? full);
				if (!real.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
			}
			catch (IOException)
			{
				continue;
			}
			return full;
		}
		return null;
	}

	/// <summary>
	/// 在根目录内精确解析一个文件，不尝试 PathCandidates。
	/// 附加公开资源使用这个入口，避免一个 URL 因候选删段而命中另一个文件。
	/// </summary>
	public static string? ResolveExact(string root, string relative)
	{
		if (!IsSafeRelativePath(relative)) return null;
		string canonicalRoot;
		try
		{
			canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
		string full;
		try
		{
			full = Path.GetFullPath(Path.Combine(canonicalRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
		StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		if (!full.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, comparison) || !File.Exists(full)) return null;
		try
		{
			if ((File.GetAttributes(canonicalRoot) & FileAttributes.ReparsePoint) != 0) return null;
		}
		catch (FileNotFoundException) { return null; }
		catch (DirectoryNotFoundException) { return null; }
		catch (UnauthorizedAccessException) { return null; }
		catch (IOException) { return null; }
		string current = canonicalRoot;
		string relativeToRoot = Path.GetRelativePath(canonicalRoot, full);
		foreach (string segment in relativeToRoot.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
		{
			current = Path.Combine(current, segment);
			try
			{
				FileAttributes attributes = File.GetAttributes(current);
				if ((attributes & FileAttributes.ReparsePoint) != 0) return null;
			}
			catch (FileNotFoundException) { return null; }
			catch (DirectoryNotFoundException) { return null; }
			catch (UnauthorizedAccessException) { return null; }
			catch (IOException) { return null; }
		}
		return full;
	}

	/// <summary>
	/// HEX 字符转数值, 非法返回 -1
	/// </summary>
	private static int HexValue(char value) => value switch
	{
		>= '0' and <= '9' => value - '0',
		>= 'a' and <= 'f' => value - 'a' + 10,
		>= 'A' and <= 'F' => value - 'A' + 10,
		_ => -1,
	};
}
