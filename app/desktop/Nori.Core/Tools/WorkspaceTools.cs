using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nori.Core.Tools;

/// <summary>
/// 工作目录文件工具：看、找、读、写。
///
/// 此前 19 件内建工具均不具备本地文件访问能力，模型无法读取用户当前处理的文件。
///
/// 权限分档：读取、列目录、搜索为 `safe`，写入为 `confirm`。工作目录由用户显式配置，该配置
/// 动作即为授权；若每次读取都触发授权对话框，一次代码浏览会产生数十次确认，实际效果是用户
/// 关闭该功能。写入修改用户数据，逐次确认。
///
/// 不提供删除工具：删除的收益低于风险，清空内容可由写入空文件实现。
/// </summary>
public static class WorkspaceTools
{
	/// <summary>本组工具名。变更工作目录时据此先注销，避免残留持有旧目录的注册项。</summary>
	public static readonly IReadOnlyList<string> ToolNames = ["listFiles", "readFile", "searchFiles", "writeFile"];

	/// <summary>
	/// 注册本组工具，注册前先注销同名项。工作目录被清空时必须真正移除：仅跳过注册的话，旧注册
	/// 项仍在注册表中，其闭包持有旧目录，配置变更不生效。
	///
	/// 未配置工作目录时整组不注册：暴露一件必定失败的工具会导致模型反复重试。
	/// </summary>
	public static void RegisterAll(ToolRegistry registry, WorkspaceAccess workspace)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(workspace);
		foreach (string name in ToolNames) registry.Unregister(name);
		if (!workspace.IsConfigured) return;

		Register(registry, "listFiles",
			"列出工作目录中某个文件夹下的文件与子文件夹。path 省略时列根目录。", "safe",
			Schema(("path", "相对工作目录的文件夹路径，省略表示根目录", false)),
			(args, _) => Task.FromResult<object?>(ListFiles(workspace, Str(args, "path"))));

		Register(registry, "readFile",
			"读取工作目录中一个文本文件的内容。文件过大时只返回开头部分。", "safe",
			Schema(("path", "相对工作目录的文件路径", true)),
			(args, _) => Task.FromResult<object?>(ReadFile(workspace, Str(args, "path"))));

		Register(registry, "searchFiles",
			"在工作目录中按内容搜索文本，返回命中的文件、行号与该行内容。", "safe",
			Schema(
				("query", "要搜索的文本（区分大小写与否由 caseSensitive 决定）", true),
				("path", "限定搜索的子目录，省略表示整个工作目录", false),
				("extension", "限定文件扩展名，例如 .cs；省略表示不限", false)),
			(args, token) => Task.FromResult<object?>(SearchFiles(
				workspace, Str(args, "query"), Str(args, "path"), Str(args, "extension"), token.CancellationToken)));

		Register(registry, "writeFile",
			"把内容写入工作目录中的文件，覆盖原有内容。父文件夹不存在时会创建。", "confirm",
			Schema(
				("path", "相对工作目录的文件路径", true),
				("content", "要写入的完整文本内容", true)),
			(args, _) => Task.FromResult<object?>(WriteFile(workspace, Str(args, "path"), Str(args, "content"))));
	}

	// ---- 实现 ----

	private static object ListFiles(WorkspaceAccess workspace, string? path)
	{
		string resolved = workspace.Resolve(path) ?? throw OutOfBounds(path);
		if (!Directory.Exists(resolved)) throw new InvalidOperationException($"文件夹不存在: {Show(path)}");

		List<object> entries = [];
		bool truncated = false;
		foreach (string directory in Directory.EnumerateDirectories(resolved).Order(StringComparer.Ordinal))
		{
			string name = Path.GetFileName(directory);
			if (WorkspaceAccess.IsSkipped(name)) continue;
			// 枚举的产物同样可能是指向外部的链接，列出来即等于告诉调用方那个路径可读。
			if (workspace.Accept(directory) is null) continue;
			if (entries.Count >= WorkspaceAccess.MaxEntries) { truncated = true; break; }
			entries.Add(new { name, kind = "folder", path = workspace.Relative(directory) });
		}

		if (!truncated)
		{
			foreach (string file in Directory.EnumerateFiles(resolved).Order(StringComparer.Ordinal))
			{
				if (workspace.Accept(file) is null) continue;
				if (entries.Count >= WorkspaceAccess.MaxEntries) { truncated = true; break; }
				entries.Add(new
				{
					name = Path.GetFileName(file),
					kind = "file",
					path = workspace.Relative(file),
					bytes = new FileInfo(file).Length,
				});
			}
		}

		return new { path = workspace.Relative(resolved), entries, truncated };
	}

	private static object ReadFile(WorkspaceAccess workspace, string? path)
	{
		string resolved = workspace.Resolve(path) ?? throw OutOfBounds(path);
		if (!File.Exists(resolved)) throw new InvalidOperationException($"文件不存在: {Show(path)}");

		(string text, bool truncated) = WorkspaceAccess.ReadText(resolved);
		string relative = workspace.Relative(resolved);
		return FitWithin(
			text,
			(content, cut) => new { path = relative, content, truncated = truncated || cut });
	}

	/// <summary>
	/// 将正文裁剪至工具结果长度上限之内，保证返回值仍为对象而非截断后的字符串。
	///
	/// 否则 <see cref="ToolLimits.MaxResultCharacters"/> 生效时会把超限结果序列化后整体截断，
	/// 调用方收到的是不完整的 JSON 文本，既无法取出 `path` 也无法取全 `content`，且无从判断
	/// 发生了截断。
	///
	/// 采用逐级减半而非一次估算：JSON 转义的膨胀系数随内容而变（非 ASCII 字符转义为 6 个
	/// 字符），按字节预估会显著偏离，实测序列化长度是唯一可靠判据。
	/// </summary>
	private static object FitWithin(string text, Func<string, bool, object> build)
	{
		string candidate = text;
		bool cut = false;
		for (int attempt = 0; attempt < 24; attempt++)
		{
			object result = build(candidate, cut);
			if (ToolLimits.SerializedLength(JsonSerializer.SerializeToNode(result)) <= ToolLimits.MaxResultCharacters)
			{
				return result;
			}

			if (candidate.Length == 0) break;
			candidate = candidate[..(candidate.Length / 2)];
			cut = true;
		}

		return build("", true);
	}

	private static object SearchFiles(
		WorkspaceAccess workspace,
		string? query,
		string? path,
		string? extension,
		CancellationToken cancellationToken)
	{
		string needle = (query ?? string.Empty).Trim();
		if (needle.Length == 0) throw new InvalidOperationException("搜索内容不能为空");

		string root = workspace.Resolve(path) ?? throw OutOfBounds(path);
		if (!Directory.Exists(root)) throw new InvalidOperationException($"文件夹不存在: {Show(path)}");

		string? suffix = string.IsNullOrWhiteSpace(extension)
			? null
			: extension.Trim().StartsWith('.') ? extension.Trim() : "." + extension.Trim();

		List<object> hits = [];
		bool truncated = false;
		foreach (string file in Walk(workspace, root, cancellationToken))
		{
			if (hits.Count >= WorkspaceAccess.MaxEntries) { truncated = true; break; }
			if (suffix is not null && !file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

			foreach ((int number, string line) in MatchingLines(file, needle))
			{
				if (hits.Count >= WorkspaceAccess.MaxEntries) { truncated = true; break; }
				hits.Add(new
				{
					path = workspace.Relative(file),
					line = number,
					// 单行可能极长（压缩后的 js 为单行），按上限截断。
					text = ToolLimits.CapText(line.Trim(), 300),
				});
			}
		}

		// 命中数超限时从尾部移除，而非触发整体截断：靠前的命中相关度通常更高。
		while (hits.Count > 0 &&
			ToolLimits.SerializedLength(JsonSerializer.SerializeToNode(new { query = needle, hits, truncated = true }))
				> ToolLimits.MaxResultCharacters)
		{
			hits.RemoveAt(hits.Count - 1);
			truncated = true;
		}

		return new { query = needle, hits, truncated };
	}

	private static object WriteFile(WorkspaceAccess workspace, string? path, string? content)
	{
		string resolved = workspace.Resolve(path) ?? throw OutOfBounds(path);
		if (Directory.Exists(resolved)) throw new InvalidOperationException($"这是一个文件夹，不能写: {Show(path)}");

		string text = content ?? string.Empty;
		byte[] bytes = new UTF8Encoding(false).GetBytes(text);
		if (bytes.Length > WorkspaceAccess.MaxWriteBytes)
		{
			throw new InvalidOperationException(
				$"内容超过 {WorkspaceAccess.MaxWriteBytes / 1024} KB 上限，拒绝写入");
		}

		string? parent = Path.GetDirectoryName(resolved);
		if (parent is not null && !Directory.Exists(parent)) Directory.CreateDirectory(parent);

		bool existed = File.Exists(resolved);
		File.WriteAllBytes(resolved, bytes);
		return new { path = workspace.Relative(resolved), bytes = bytes.Length, overwritten = existed };
	}

	/// <summary>
	/// 递归遍历，跳过排除目录；无访问权限的目录跳过，不使整次搜索失败。
	///
	/// **每一个目录与文件都要过一次 <see cref="WorkspaceAccess.Accept"/>。**
	/// `Directory.GetDirectories` 会返回指向工作目录之外的链接，`GetFiles` 与后续读取都会跟随
	/// 它。搜索是 `safe` 档、不经确认，因此这条路径上的越界等于无确认的任意文件读取。
	/// `Resolve` 只覆盖调用方给出的相对路径，遍历拿到的绝对路径必须单独校验。
	/// </summary>
	private static IEnumerable<string> Walk(
		WorkspaceAccess workspace,
		string root,
		CancellationToken cancellationToken)
	{
		Stack<string> pending = new();
		pending.Push(root);
		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string current = pending.Pop();

			string[] directories;
			string[] files;
			try
			{
				directories = Directory.GetDirectories(current);
				files = Directory.GetFiles(current);
			}
			catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
			{
				continue;
			}

			foreach (string directory in directories)
			{
				if (WorkspaceAccess.IsSkipped(Path.GetFileName(directory))) continue;
				if (workspace.Accept(directory) is null) continue;
				pending.Push(directory);
			}

			foreach (string file in files.Order(StringComparer.Ordinal))
			{
				if (workspace.Accept(file) is null) continue;
				yield return file;
			}
		}
	}

	private static IEnumerable<(int Number, string Line)> MatchingLines(string file, string needle)
	{
		byte[] head;
		try
		{
			using FileStream stream = File.OpenRead(file);
			int length = (int)Math.Min(stream.Length, WorkspaceAccess.MaxScanBytes);
			head = new byte[length];
			stream.ReadExactly(head, 0, length);
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
		{
			yield break;
		}

		if (WorkspaceAccess.LooksBinary(head)) yield break;

		string text = new UTF8Encoding(false, false).GetString(head);
		int number = 0;
		foreach (string line in text.Split('\n'))
		{
			number++;
			if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
			{
				yield return (number, line.TrimEnd('\r'));
			}
		}
	}

	// ---- 小工具 ----

	private static InvalidOperationException OutOfBounds(string? path) =>
		new($"路径超出工作目录范围: {Show(path)}");

	private static string Show(string? path) => string.IsNullOrWhiteSpace(path) ? "(空)" : path;

	private static string? Str(JsonNode? args, string name) =>
		args?[name]?.GetValue<string>();

	private static JsonObject Schema(params (string Name, string Description, bool Required)[] properties)
	{
		JsonObject props = new();
		JsonArray required = [];
		foreach ((string name, string description, bool isRequired) in properties)
		{
			props[name] = new JsonObject { ["type"] = "string", ["description"] = description };
			if (isRequired) required.Add(name);
		}

		return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required };
	}

	private static void Register(
		ToolRegistry registry,
		string name,
		string description,
		string permissionLevel,
		JsonObject parameters,
		Func<JsonNode?, ToolContext, Task<object?>> execute) =>
		registry.Register(new RegisteredTool
		{
			Name = name,
			Description = description,
			Parameters = parameters,
			PermissionLevel = permissionLevel,
			Execute = execute,
		});
}
