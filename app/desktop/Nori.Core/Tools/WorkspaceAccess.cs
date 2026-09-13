using System.Text;

namespace Nori.Core.Tools;

/// <summary>
/// 工作目录的边界判据。
///
/// 文件工具的全部安全性都压在这一个类上：模型给出的路径是不可信输入，而一次越界读取
/// 泄露的是用户磁盘上的任意文件。因此判据集中在这里、只有一份，四个工具都从它拿路径，
/// 没有第二条自己拼路径的路。
///
/// **未配置工作目录时整族工具不注册**（`Root` 为空）。默认关闭而不是默认给一个
/// 「用户目录」之类的值：那等于替用户做了一个他没同意过的决定。
/// </summary>
public sealed class WorkspaceAccess
{
	/// <summary>
	/// 单个文件最多读多少字节。超出即截断，不是拒绝 —— 看前面那一段往往就够了。
	///
	/// 调大这个数有前置条件：工具结果受 <see cref="ToolLimits.MaxResultCharacters"/> 约束
	/// （48,000 字符），超限时 <c>ToolLimits.CapResult</c> 会把整个结果序列化后截断为字符串，
	/// 调用方收到的不再是 <c>{path, content}</c> 对象。因此读取侧先自行收窄结果，由
	/// <c>FitWithin</c> 逐级裁剪，本常量只是第一级粗筛。
	/// </summary>
	public const int MaxReadBytes = 128 * 1024;

	/// <summary>一次列目录 / 搜索返回的条目上限。同样受结果长度约束，优先减少条目而非触发截断。</summary>
	public const int MaxEntries = 80;

	/// <summary>搜索时单个文件最多扫多少字节，超出的部分不看。</summary>
	public const int MaxScanBytes = 1024 * 1024;

	/// <summary>写入的内容上限。</summary>
	public const int MaxWriteBytes = 1024 * 1024;

	/// <summary>
	/// 一律跳过的目录名。
	///
	/// 与安全无关，是结果质量约束：`node_modules` 与 `.git` 的命中数量通常远超源码目录，
	/// 会占满条目上限，使真正相关的命中不进入结果集。
	/// </summary>
	private static readonly string[] SkippedDirectories =
		[".git", ".svn", "node_modules", "bin", "obj", ".venv", "venv", "__pycache__", ".next", "dist", "target"];

	/// <summary>工作目录的绝对路径。空串表示没有配置，整族工具不可用。</summary>
	public string Root { get; }

	public bool IsConfigured => Root.Length > 0;

	public WorkspaceAccess(string? root)
	{
		string trimmed = (root ?? string.Empty).Trim();
		if (trimmed.Length == 0 || !Directory.Exists(trimmed))
		{
			Root = string.Empty;
			return;
		}

		// 根路径本身也需解引用：工作目录为符号链接时，未解引用的根会使后续前缀比较全部失配。
		Root = ResolveFinal(Path.GetFullPath(trimmed));
	}

	/// <summary>
	/// 把模型给的相对路径解析成工作目录内的绝对路径。越界返回 null。
	///
	/// 三层判据缺一不可：
	/// 1. 拒绝绝对路径：工具契约要求相对路径，绝对路径一律视为越界；
	/// 2. `GetFullPath` 规范化后做前缀比较：拦截 `../` 形式的回溯；
	/// 3. 逐段解引用符号链接后再比较：工作目录内指向外部的链接，前两层均无法拦截。
	/// </summary>
	public string? Resolve(string? relative)
	{
		if (!IsConfigured) return null;
		string path = (relative ?? string.Empty).Trim().Replace('\\', '/');
		if (path.Length == 0 || path == ".") return Root;
		if (Path.IsPathRooted(path)) return null;
		if (path.Contains('\0')) return null;

		string combined = Path.GetFullPath(Path.Combine(Root, path));
		if (!Inside(combined)) return null;

		// 逐段解引用，而非只解析末段。
		//
		// 只解析末段无法处理中间段为链接的情形：工作目录内存在指向外部的目录链接 `escape` 时，
		// `escape/secret.txt` 自身不是链接，解析返回原值，前缀比较通过。逐段解析并比较，
		// 链接出现在任意层级都会在该层被判定越界。
		string current = Root;
		foreach (string segment in Path.GetRelativePath(Root, combined)
			.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
		{
			current = ResolveFinal(Path.Combine(current, segment));
			if (!Inside(current)) return null;
		}

		return current;
	}

	/// <summary>相对工作目录的路径，统一使用正斜杠：该值会被调用方在后续请求中原样回传。</summary>
	public string Relative(string absolute)
	{
		string relative = Path.GetRelativePath(Root, absolute).Replace('\\', '/');
		return relative == "." ? "" : relative;
	}

	/// <summary>这个目录该不该跳过（噪音目录）。</summary>
	public static bool IsSkipped(string directoryName) =>
		SkippedDirectories.Contains(directoryName, StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// 看起来是不是二进制。
	///
	/// 判据为前 8KB 内是否出现 NUL 字节，与 grep 的判定一致，开销恒定。二进制内容进入上下文
	/// 既无可读性，也会占用 token 预算。
	/// </summary>
	public static bool LooksBinary(ReadOnlySpan<byte> head)
	{
		int limit = Math.Min(head.Length, 8 * 1024);
		for (int index = 0; index < limit; index++)
		{
			if (head[index] == 0) return true;
		}

		return false;
	}

	/// <summary>读文本，超出上限就截断并标注。</summary>
	public static (string Text, bool Truncated) ReadText(string path)
	{
		using FileStream stream = File.OpenRead(path);
		int length = (int)Math.Min(stream.Length, MaxReadBytes);
		byte[] buffer = new byte[length];
		stream.ReadExactly(buffer, 0, length);
		if (LooksBinary(buffer)) throw new InvalidOperationException("这是二进制文件，读不了");
		return (new UTF8Encoding(false, false).GetString(buffer), stream.Length > MaxReadBytes);
	}

	private bool Inside(string candidate)
	{
		if (candidate.Equals(Root, StringComparison.OrdinalIgnoreCase)) return true;
		string prefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
		return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 解引用符号链接 / junction，拿到最终目标。
	///
	/// 路径不存在时原样返回：写入新文件的场景只要求父目录在界内，目标文件尚未创建。
	/// </summary>
	private static string ResolveFinal(string path)
	{
		try
		{
			FileSystemInfo? target = Directory.Exists(path)
				? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
				: File.Exists(path)
					? File.ResolveLinkTarget(path, returnFinalTarget: true)
					: null;
			return target is null ? path : Path.GetFullPath(target.FullName);
		}
		catch (IOException)
		{
			// 链接失效或权限不足时按原路径处理，后续前缀比较仍然有效。
			return path;
		}
		catch (UnauthorizedAccessException)
		{
			return path;
		}
	}
}
