using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nori.Core.Tools;

/// <summary>
/// 工作目录文件工具：列目录、搜索、读取、写入、定点替换。
///
/// 此前 19 件内建工具均不具备本地文件访问能力，模型无法读取用户当前处理的文件。
///
/// 权限分档：读取、列目录、搜索为 `safe`，写入与替换为 `confirm`。工作目录由用户显式配置，该配置
/// 动作即为授权；若每次读取都触发授权对话框，一次代码浏览会产生数十次确认，实际效果是用户
/// 关闭该功能。写入修改用户数据，逐次确认。
///
/// 不提供删除工具：删除的收益低于风险，清空内容可由写入空文件实现。
/// </summary>
public static class WorkspaceTools
{
	/// <summary>本组工具名。变更工作目录时据此先注销，避免残留持有旧目录的注册项。</summary>
	public static readonly IReadOnlyList<string> ToolNames = ["listFiles", "readFile", "searchFiles", "writeFile", "editFile"];

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
			"新建文件或整份覆盖写入。父文件夹不存在时会创建。修改已有文件请改用 editFile。", "confirm",
			Schema(
				("path", "相对工作目录的文件路径", true),
				("content", "要写入的完整文本内容", true)),
			(args, _) => Task.FromResult<object?>(WriteFile(workspace, Str(args, "path"), Str(args, "content"))));

		Register(registry, "editFile",
			"替换工作目录中某个文件里的一段文本。oldText 必须与文件中的内容逐字符一致且唯一；"
				+ "修改已有文件用本工具，不要用 writeFile 整份回传。", "confirm",
			WithBoolean(
				Schema(
					("path", "相对工作目录的文件路径", true),
					("oldText", "要被替换掉的原文，需包含足够上下文以在文件中唯一", true),
					("newText", "替换成的新内容，留空表示删除这一段", true)),
				"replaceAll",
				"原文出现多次时是否全部替换，默认 false（此时出现多次会拒绝执行）"),
			(args, _) => Task.FromResult<object?>(EditFile(
				workspace, Str(args, "path"), Str(args, "oldText"), Str(args, "newText"), Bool(args, "replaceAll"))));
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
	/// 定点替换文件中的一段文本。
	///
	/// 与 <c>writeFile</c> 的分工：新建文件用写入，修改已有文件用本工具。两点原因：
	///
	/// 1. <see cref="ToolLimits.MaxArgumentsCharacters"/> 为 32,000 字符，而单文件读取上限是
	///    128 KB。整份回传的写入方式无法改写超过参数上限的文件，定点替换不受文件大小约束；
	/// 2. 整份回传会把未被提及的部分一并覆盖，模型少输出一段即造成静默删除。
	///
	/// **`oldText` 必须在文件中唯一**，否则拒绝执行并报告出现次数。命中多处时替换首处是错误的
	/// 默认行为：模型给出的短片段（`return null;`、`}`）在源码中普遍重复，改错的位置与改对的
	/// 位置在结果里无法区分。需要全部替换时由调用方显式传 `replaceAll`。
	/// </summary>
	private static object EditFile(
		WorkspaceAccess workspace,
		string? path,
		string? oldText,
		string? newText,
		bool replaceAll)
	{
		string resolved = workspace.Resolve(path) ?? throw OutOfBounds(path);
		if (Directory.Exists(resolved)) throw new InvalidOperationException($"这是一个文件夹，不能改: {Show(path)}");
		if (!File.Exists(resolved)) throw new InvalidOperationException($"文件不存在，新建请用 writeFile: {Show(path)}");

		string target = oldText ?? string.Empty;
		string replacement = newText ?? string.Empty;
		if (target.Length == 0) throw new InvalidOperationException("oldText 不能为空");
		if (target == replacement) throw new InvalidOperationException("oldText 与 newText 相同，无需替换");

		string text = ReadForEdit(resolved, path);
		bool adjusted = false;

		// 行尾适配：文件为 CRLF 而 oldText 为纯 LF 时，逐字符匹配必然失败。
		//
		// 此处做一次确定性转换而非直接报错：模型难以从错误信息中推断出文件的行尾形式，
		// 报错的结果是同一处编辑反复重试。转换只在精确匹配失败后尝试，且结果在返回值的
		// `lineEndingAdjusted` 中报告，不是静默行为。
		if (Count(text, target) == 0
			&& text.Contains("\r\n", StringComparison.Ordinal)
			&& target.Contains('\n')
			&& !target.Contains("\r\n", StringComparison.Ordinal)
			&& !replacement.Contains("\r\n", StringComparison.Ordinal))
		{
			string candidate = target.Replace("\n", "\r\n", StringComparison.Ordinal);
			if (Count(text, candidate) > 0)
			{
				target = candidate;
				replacement = replacement.Replace("\n", "\r\n", StringComparison.Ordinal);
				adjusted = true;
			}
		}

		int occurrences = Count(text, target);
		if (occurrences == 0)
		{
			throw new InvalidOperationException(
				$"未找到 oldText，它必须与文件中的文本逐字符一致（含缩进与空格）: {Show(path)}");
		}

		if (occurrences > 1 && !replaceAll)
		{
			throw new InvalidOperationException(
				$"oldText 在文件中出现 {occurrences} 次，无法确定改哪一处。" +
				"请在 oldText 前后补充上下文使其唯一，或传 replaceAll 替换全部");
		}

		int first = text.IndexOf(target, StringComparison.Ordinal);
		string updated = replaceAll
			? text.Replace(target, replacement, StringComparison.Ordinal)
			: string.Concat(text.AsSpan(0, first), replacement, text.AsSpan(first + target.Length));

		byte[] bytes = new UTF8Encoding(false).GetBytes(updated);
		if (bytes.Length > WorkspaceAccess.MaxWriteBytes)
		{
			throw new InvalidOperationException(
				$"替换后内容超过 {WorkspaceAccess.MaxWriteBytes / 1024} KB 上限，拒绝写入");
		}

		File.WriteAllBytes(resolved, bytes);
		return new
		{
			path = workspace.Relative(resolved),
			replaced = replaceAll ? occurrences : 1,
			line = text.AsSpan(0, first).Count('\n') + 1,
			bytes = bytes.Length,
			lineEndingAdjusted = adjusted,
		};
	}

	/// <summary>
	/// 读出整份文件供改写使用。
	///
	/// 不复用 <see cref="WorkspaceAccess.ReadText"/>：后者在 <see cref="WorkspaceAccess.MaxReadBytes"/>
	/// 处截断，用截断后的文本改写并整份写回会删除尾部内容。此处超限即拒绝。
	///
	/// 解码与编码均使用无 BOM 的 <see cref="UTF8Encoding"/>。文件原有的 BOM 会被解码成首位的
	/// U+FEFF 字符并在编码时原样写回，故 BOM 得以保留。
	/// </summary>
	private static string ReadForEdit(string resolved, string? path)
	{
		if (new FileInfo(resolved).Length > WorkspaceAccess.MaxWriteBytes)
		{
			throw new InvalidOperationException(
				$"文件超过 {WorkspaceAccess.MaxWriteBytes / 1024} KB，超出可改写范围: {Show(path)}");
		}

		byte[] bytes = File.ReadAllBytes(resolved);
		if (WorkspaceAccess.LooksBinary(bytes))
		{
			throw new InvalidOperationException($"这是二进制文件，改不了: {Show(path)}");
		}

		return new UTF8Encoding(false, false).GetString(bytes);
	}

	/// <summary>不重叠地统计子串出现次数。</summary>
	private static int Count(string text, string needle)
	{
		int count = 0;
		int index = 0;
		while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
		{
			count++;
			index += needle.Length;
		}

		return count;
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

	/// <summary>
	/// 读一个布尔参数，缺省为 false。
	///
	/// 同时接受 JSON 布尔与 `"true"` 这种字符串形式：多数模型在工具参数里混用两者，
	/// 只认布尔会使 `replaceAll` 被静默当成 false，替换范围与调用方的意图不一致。
	/// </summary>
	private static bool Bool(JsonNode? args, string name)
	{
		JsonNode? node = args?[name];
		if (node is null) return false;
		if (node.GetValueKind() == JsonValueKind.True) return true;
		if (node.GetValueKind() == JsonValueKind.False) return false;
		return node.GetValueKind() == JsonValueKind.String
			&& string.Equals(node.GetValue<string>().Trim(), "true", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>给 <see cref="Schema"/> 产出的对象补一个布尔属性（非必填）。</summary>
	private static JsonObject WithBoolean(JsonObject schema, string name, string description)
	{
		((JsonObject)schema["properties"]!)[name] =
			new JsonObject { ["type"] = "boolean", ["description"] = description };
		return schema;
	}

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
