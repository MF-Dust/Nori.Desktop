using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Tools;

namespace Nori.Core.Tests;

/// <summary>
/// 工作目录文件工具。
///
/// 这一族的全部安全性压在 <see cref="WorkspaceAccess.Resolve"/> 上：模型给的路径是不可信
/// 输入，一次越界读取泄露的是用户磁盘上的任意文件。所以越界那几条是本文件的重点，其余是
/// 可用性判据。
/// </summary>
public sealed class WorkspaceToolsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"nori-ws-{Guid.NewGuid():N}");
	private readonly string _outside = Path.Combine(Path.GetTempPath(), $"nori-out-{Guid.NewGuid():N}");

	public WorkspaceToolsTests()
	{
		Directory.CreateDirectory(_root);
		Directory.CreateDirectory(_outside);
		File.WriteAllText(Path.Combine(_outside, "secret.txt"), "不该被读到");
	}

	public void Dispose()
	{
		foreach (string directory in new[] { _root, _outside })
		{
			try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* 清理失败不影响断言 */ }
		}
	}

	private WorkspaceAccess Access() => new(_root);

	private void Write(string relative, string content)
	{
		string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, content);
	}

	private ToolRegistry Registry()
	{
		ToolRegistry registry = new();
		WorkspaceTools.RegisterAll(registry, Access());
		return registry;
	}

	private static async Task<JsonElement> CallAsync(ToolRegistry registry, string name, object args)
	{
		ToolResult result = await registry.ExecuteAsync(
			name,
			JsonNode.Parse(JsonSerializer.Serialize(args)),
			new ToolContext { Approve = _ => Task.FromResult(true) });
		Assert.True(result.IsSuccess, result.Error);
		return JsonSerializer.SerializeToElement(result.Result);
	}

	private static async Task<string> FailAsync(ToolRegistry registry, string name, object args)
	{
		ToolResult result = await registry.ExecuteAsync(
			name,
			JsonNode.Parse(JsonSerializer.Serialize(args)),
			new ToolContext { Approve = _ => Task.FromResult(true) });
		Assert.False(result.IsSuccess);
		return result.Error ?? "";
	}

	/// <summary>取结果里某个数组字段的 path 列表。</summary>
	private static string[] Paths(JsonElement result, string field) =>
		[.. result.GetProperty(field).EnumerateArray().Select(entry => entry.GetProperty("path").GetString()!)];

	// ---- 边界 ----

	/// <summary>
	/// 这些形式在 Windows 与非 Windows 上必须得到同一个结论。
	///
	/// 盘符那三条不能交给 <c>Path.IsPathRooted</c>：非 Windows 上它对 `C:/x` 返回 false，
	/// 该串会被当作名为 `C:` 的相对目录落进工作目录。判据由 <c>IsDriveQualified</c> 补齐。
	/// </summary>
	[Theory]
	[InlineData("../secret.txt")]
	[InlineData("../../etc/passwd")]
	[InlineData("sub/../../outside.txt")]
	[InlineData("C:/Windows/System32/drivers/etc/hosts")]
	[InlineData("c:/temp/x")]
	[InlineData("C:report.txt")]
	[InlineData("/etc/passwd")]
	public void 越界路径一律解析失败(string path)
	{
		Assert.Null(Access().Resolve(path));
	}

	[Fact]
	public void 目录内的路径正常解析()
	{
		WorkspaceAccess access = Access();

		Assert.NotNull(access.Resolve("a.txt"));
		Assert.NotNull(access.Resolve("sub/b.txt"));
		// 反斜杠也收：Windows 上的模型很容易给出这种写法。
		Assert.NotNull(access.Resolve("sub\\b.txt"));
		Assert.Equal(access.Root, access.Resolve("."));
	}

	/// <summary>
	/// 前缀比较挡不住符号链接：工作目录里放一个指向外面的链接，`GetFullPath` 之后它仍然
	/// 是界内路径。必须解引用之后再比一次。
	/// </summary>
	[Fact]
	public void 指向目录外的符号链接被拦下()
	{
		string link = Path.Combine(_root, "escape");
		try
		{
			Directory.CreateSymbolicLink(link, _outside);
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
		{
			// Windows 上建符号链接要开发者模式或管理员权限，建不了就跳过 —— 这一条在能建的
			// 环境里才有意义，而判据本身由上面几条覆盖。
			return;
		}

		Assert.Null(Access().Resolve("escape/secret.txt"));
	}

	[Fact]
	public void 没配工作目录时整族工具不注册()
	{
		ToolRegistry registry = new();
		WorkspaceTools.RegisterAll(registry, new WorkspaceAccess(""));

		// 让模型看见一件永远失败的工具只会让它反复试。
		Assert.Empty(registry.List());
	}

	[Fact]
	public void 目录不存在时同样视为未配置()
	{
		Assert.False(new WorkspaceAccess(Path.Combine(_root, "并不存在")).IsConfigured);
	}



	// ---- findFiles ----

	[Fact]
	public async Task 按扩展名找到深层文件()
	{
		Write("a.cs", "x");
		Write("src/b.cs", "x");
		Write("src/deep/c.cs", "x");
		Write("src/readme.md", "x");

		JsonElement result = await CallAsync(Registry(), "findFiles", new { pattern = "*.cs" });
		string[] paths = Paths(result, "matches");

		Assert.Equal(new[] { "a.cs", "src/b.cs", "src/deep/c.cs" }, paths.Order(StringComparer.Ordinal).ToArray());
	}

	/// <summary>
	/// `**/*.cs` 必须有命中。
	///
	/// <see cref="FileSystemName.MatchesSimpleExpression"/> 只认 `*` 与 `?`，`**` 在它那里是
	/// 两个连续的 `*`，语义与单个 `*` 相同但会使含 `/` 的判断走偏。模型普遍按 glob 习惯写这种
	/// 形式，不归一的后果是零命中，而零命中与「确实没有」在返回值里无法区分。
	/// </summary>
	[Fact]
	public async Task 归一glob风格的双星号()
	{
		Write("a.cs", "x");
		Write("src/deep/c.cs", "x");
		Write("src/deep/c.md", "x");

		JsonElement result = await CallAsync(Registry(), "findFiles", new { pattern = "**/*.cs" });

		// 根目录下的 a.cs 也必须命中：`**/` 表示任意层级，含零层。
		Assert.Equal(
			new[] { "a.cs", "src/deep/c.cs" },
			Paths(result, "matches").Order(StringComparer.Ordinal).ToArray());
	}

	[Fact]
	public async Task 模式含斜杠时按相对路径匹配()
	{
		Write("src/a.cs", "x");
		Write("test/a.cs", "x");

		JsonElement result = await CallAsync(Registry(), "findFiles", new { pattern = "src/*.cs" });

		Assert.Equal(new[] { "src/a.cs" }, Paths(result, "matches"));
	}

	[Fact]
	public async Task 不含斜杠时只匹配文件名()
	{
		Write("src/AgentEngine.cs", "x");
		Write("src/other.cs", "x");

		JsonElement result = await CallAsync(Registry(), "findFiles", new { pattern = "Agent*.cs" });

		Assert.Equal(new[] { "src/AgentEngine.cs" }, Paths(result, "matches"));
	}

	[Fact]
	public async Task 大小写不敏感与内容搜索口径一致()
	{
		Write("src/AgentEngine.cs", "x");

		JsonElement result = await CallAsync(Registry(), "findFiles", new { pattern = "agentengine.CS" });

		Assert.Equal(new[] { "src/AgentEngine.cs" }, Paths(result, "matches"));
	}

	[Fact]
	public async Task 查找可以限定子目录()
	{
		Write("src/a.cs", "x");
		Write("test/a.cs", "x");

		JsonElement result = await CallAsync(Registry(), "findFiles", new { pattern = "*.cs", path = "test" });

		Assert.Equal(new[] { "test/a.cs" }, Paths(result, "matches"));
	}

	/// <summary>查找与搜索共用 <c>Walk</c>，噪音目录的排除口径必须一致。</summary>
	[Fact]
	public async Task 查找跳过噪音目录()
	{
		Write("a.cs", "x");
		Write("node_modules/b.cs", "x");
		Write(".git/c.cs", "x");

		JsonElement result = await CallAsync(Registry(), "findFiles", new { pattern = "*.cs" });

		Assert.Equal(new[] { "a.cs" }, Paths(result, "matches"));
	}

	/// <summary>查找走的也是 <c>Walk</c>，指向目录外的链接同样不得跟随。</summary>
	[Fact]
	public async Task 查找不跟随指向目录外的链接()
	{
		File.WriteAllText(Path.Combine(_outside, "leak.cs"), "x");
		Write("inside.cs", "x");
		try
		{
			Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), _outside);
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
		{
			return; // 该环境不允许建符号链接
		}

		JsonElement result = await CallAsync(Registry(), "findFiles", new { pattern = "*.cs" });

		Assert.Equal(new[] { "inside.cs" }, Paths(result, "matches"));
	}

	[Fact]
	public async Task 空模式被拒绝()
	{
		Assert.Contains(
			"不能为空",
			await FailAsync(Registry(), "findFiles", new { pattern = "  " }),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task 查找同样受工作目录边界约束()
	{
		Assert.Contains(
			"超出工作目录范围",
			await FailAsync(Registry(), "findFiles", new { pattern = "*.txt", path = "../" }),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task 命中数超过上限时截断并标注()
	{
		for (int index = 0; index < WorkspaceAccess.MaxEntries + 10; index++)
		{
			Write($"many/f{index}.cs", "x");
		}

		JsonElement result = await CallAsync(Registry(), "findFiles", new { pattern = "*.cs" });

		Assert.True(result.GetProperty("truncated").GetBoolean());
		Assert.Equal(WorkspaceAccess.MaxEntries, result.GetProperty("matches").GetArrayLength());
	}

	// ---- editFile ----

	[Fact]
	public async Task 定点替换只改命中的那一段()
	{
		Write("a.cs", "第一行\n中间要改\n第三行\n");

		JsonElement result = await CallAsync(
			Registry(), "editFile", new { path = "a.cs", oldText = "中间要改", newText = "已经改了" });

		Assert.Equal("已经改了", File.ReadAllText(Path.Combine(_root, "a.cs")).Split('\n')[1]);
		Assert.Equal("第一行\n已经改了\n第三行\n", File.ReadAllText(Path.Combine(_root, "a.cs")));
		Assert.Equal(1, result.GetProperty("replaced").GetInt32());
		Assert.Equal(2, result.GetProperty("line").GetInt32());
		Assert.False(result.GetProperty("lineEndingAdjusted").GetBoolean());
	}

	/// <summary>
	/// 出现多处时必须拒绝，而不是替换首处。
	///
	/// 模型给出的短片段在源码中普遍重复；改错的位置与改对的位置在返回值里无法区分，
	/// 而文件已经被改掉了。错误信息要带出现次数，否则模型只能盲目加上下文重试。
	/// </summary>
	[Fact]
	public async Task 原文出现多次时拒绝执行并报告次数()
	{
		Write("a.cs", "return null;\nreturn null;\nreturn null;\n");

		string error = await FailAsync(
			Registry(), "editFile", new { path = "a.cs", oldText = "return null;", newText = "return 0;" });

		Assert.Contains("3 次", error, StringComparison.Ordinal);
		Assert.Contains("replaceAll", error, StringComparison.Ordinal);
		Assert.Equal("return null;\nreturn null;\nreturn null;\n", File.ReadAllText(Path.Combine(_root, "a.cs")));
	}

	[Fact]
	public async Task 传了replaceAll就全部替换()
	{
		Write("a.cs", "x\nx\nx\n");

		JsonElement result = await CallAsync(
			Registry(), "editFile", new { path = "a.cs", oldText = "x", newText = "y", replaceAll = true });

		Assert.Equal(3, result.GetProperty("replaced").GetInt32());
		Assert.Equal("y\ny\ny\n", File.ReadAllText(Path.Combine(_root, "a.cs")));
	}

	/// <summary>模型常把布尔参数写成字符串，只认 JSON 布尔会使 replaceAll 静默失效。</summary>
	[Fact]
	public async Task 字符串形式的replaceAll也认()
	{
		Write("a.cs", "x\nx\n");

		JsonElement result = await CallAsync(
			Registry(), "editFile", new { path = "a.cs", oldText = "x", newText = "y", replaceAll = "true" });

		Assert.Equal(2, result.GetProperty("replaced").GetInt32());
	}

	/// <summary>
	/// 文件是 CRLF 而 oldText 是纯 LF 时做一次确定性转换。
	///
	/// 直接报错的话模型无从得知文件的行尾形式，同一处编辑会反复重试。转换只在精确匹配
	/// 失败后尝试，并在返回值里报告。
	/// </summary>
	[Fact]
	public async Task 行尾不一致时自动适配并报告()
	{
		Write("a.cs", "第一行\r\n要改的\r\n第三行\r\n");

		JsonElement result = await CallAsync(
			Registry(),
			"editFile",
			new { path = "a.cs", oldText = "要改的\n第三行", newText = "改过了\n新第三行" });

		Assert.True(result.GetProperty("lineEndingAdjusted").GetBoolean());
		// 转换后的新内容同样是 CRLF，不能在文件里混进 LF。
		Assert.Equal("第一行\r\n改过了\r\n新第三行\r\n", File.ReadAllText(Path.Combine(_root, "a.cs")));
	}

	[Fact]
	public async Task 未找到原文时报错且不动文件()
	{
		Write("a.cs", "原样");

		string error = await FailAsync(
			Registry(), "editFile", new { path = "a.cs", oldText = "并不存在", newText = "x" });

		Assert.Contains("逐字符一致", error, StringComparison.Ordinal);
		Assert.Equal("原样", File.ReadAllText(Path.Combine(_root, "a.cs")));
	}

	[Fact]
	public async Task 空的oldText与原样替换都被拒绝()
	{
		Write("a.cs", "内容");
		ToolRegistry registry = Registry();

		Assert.Contains(
			"不能为空",
			await FailAsync(registry, "editFile", new { path = "a.cs", oldText = "", newText = "x" }),
			StringComparison.Ordinal);
		Assert.Contains(
			"相同",
			await FailAsync(registry, "editFile", new { path = "a.cs", oldText = "内容", newText = "内容" }),
			StringComparison.Ordinal);
	}

	/// <summary>不存在的文件不隐式新建：那会把模型笔误的路径变成一个新文件。</summary>
	[Fact]
	public async Task 文件不存在时指向writeFile()
	{
		string error = await FailAsync(
			Registry(), "editFile", new { path = "没有这个.cs", oldText = "a", newText = "b" });

		Assert.Contains("writeFile", error, StringComparison.Ordinal);
		Assert.False(File.Exists(Path.Combine(_root, "没有这个.cs")));
	}

	[Fact]
	public async Task 替换同样受工作目录边界约束()
	{
		string error = await FailAsync(
			Registry(), "editFile", new { path = "../secret.txt", oldText = "不该", newText = "x" });

		Assert.Contains("超出工作目录范围", error, StringComparison.Ordinal);
		Assert.Equal("不该被读到", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
	}

	[Fact]
	public async Task 二进制文件不做替换()
	{
		File.WriteAllBytes(Path.Combine(_root, "bin.dat"), [0x41, 0x00, 0x42]);

		Assert.Contains(
			"二进制",
			await FailAsync(Registry(), "editFile", new { path = "bin.dat", oldText = "A", newText = "B" }),
			StringComparison.Ordinal);
	}

	/// <summary>BOM 由解码保留为首位 U+FEFF、编码时原样写回，替换不得把它抹掉。</summary>
	[Fact]
	public async Task 替换保留文件原有的BOM()
	{
		string file = Path.Combine(_root, "bom.cs");
		File.WriteAllBytes(file, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("旧值")]);

		await CallAsync(Registry(), "editFile", new { path = "bom.cs", oldText = "旧值", newText = "新值" });

		byte[] written = File.ReadAllBytes(file);
		Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, written[..3]);
		Assert.Equal("新值", Encoding.UTF8.GetString(written[3..]));
	}

	[Fact]
	public async Task 空的newText等于删掉那一段()
	{
		Write("a.cs", "保留A删除B保留C");

		await CallAsync(Registry(), "editFile", new { path = "a.cs", oldText = "删除B", newText = "" });

		Assert.Equal("保留A保留C", File.ReadAllText(Path.Combine(_root, "a.cs")));
	}

	/// <summary>
	/// 超过可改写上限的文件拒绝执行，不能按读取上限截断后整份写回。
	///
	/// <see cref="WorkspaceAccess.ReadText"/> 在 128 KB 处截断；用截断后的文本改写再写回，
	/// 结果是尾部内容被删除。
	/// </summary>
	[Fact]
	public async Task 超过改写上限的文件被拒绝()
	{
		Write("big.txt", new string('a', WorkspaceAccess.MaxWriteBytes + 16));

		string error = await FailAsync(
			Registry(), "editFile", new { path = "big.txt", oldText = "aaa", newText = "b" });

		Assert.Contains("超出可改写范围", error, StringComparison.Ordinal);
		Assert.Equal(WorkspaceAccess.MaxWriteBytes + 16, new FileInfo(Path.Combine(_root, "big.txt")).Length);
	}

	[Fact]
	public void 替换工具随工作目录一起注册与注销()
	{
		ToolRegistry registry = Registry();
		Assert.NotNull(registry.Get("editFile"));
		Assert.Equal("confirm", registry.Get("editFile")!.PermissionLevel);

		WorkspaceTools.RegisterAll(registry, new WorkspaceAccess(""));
		Assert.Null(registry.Get("editFile"));
	}

	/// <summary>
	/// 大于读取上限、小于改写上限的文件：替换必须保住尾部。
	///
	/// <see cref="WorkspaceAccess.ReadText"/> 在 128 KB 处截断。若替换沿用它读入再整份写回，
	/// 128 KB 之后的内容会被删除，而返回值显示替换成功 —— 这类损坏只有打开文件才发现。
	/// </summary>
	[Fact]
	public async Task 超过读取上限但可改写的文件不丢尾部()
	{
		string filler = new('x', WorkspaceAccess.MaxReadBytes);
		Write("mid.txt", "开头标记\n" + filler + "\n结尾标记");
		long before = new FileInfo(Path.Combine(_root, "mid.txt")).Length;

		await CallAsync(Registry(), "editFile", new { path = "mid.txt", oldText = "开头标记", newText = "已改开头" });

		string after = File.ReadAllText(Path.Combine(_root, "mid.txt"));
		Assert.StartsWith("已改开头", after, StringComparison.Ordinal);
		Assert.EndsWith("结尾标记", after, StringComparison.Ordinal);
		Assert.Equal(before, new FileInfo(Path.Combine(_root, "mid.txt")).Length);
	}

	// ---- review 指出的两处越界 ----

	/// <summary>
	/// 遍历过程中的目录链接必须逐个复验。
	///
	/// `Resolve` 只覆盖调用方给出的相对路径；`Directory.GetDirectories` 返回的绝对路径不经过
	/// 它，而其中可能有指向工作目录之外的链接。搜索是 `safe` 档、不经确认，这条路径上的越界
	/// 等于无确认的任意文件读取。
	/// </summary>
	[Fact]
	public async Task 搜索不跟随指向目录外的链接()
	{
		File.WriteAllText(Path.Combine(_outside, "leak.txt"), "越界内容 needle");
		Write("inside.txt", "界内内容 needle");
		try
		{
			Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), _outside);
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
		{
			return; // 该环境不允许建符号链接
		}

		JsonElement found = await CallAsync(Registry(), "searchFiles", new { query = "needle" });
		string[] paths = [.. found.GetProperty("hits").EnumerateArray().Select(e => e.GetProperty("path").GetString()!)];

		Assert.Contains("inside.txt", paths);
		Assert.DoesNotContain(paths, path => path.Contains("leak", StringComparison.Ordinal));
	}

	[Fact]
	public async Task 列目录不列出指向目录外的链接()
	{
		try
		{
			Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), _outside);
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
		{
			return;
		}

		JsonElement listed = await CallAsync(Registry(), "listFiles", new { });
		string[] names = [.. listed.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()!)];

		Assert.DoesNotContain("escape", names);
	}

	/// <summary>
	/// 大小写敏感的文件系统上，同级目录不能因为只有大小写差异就被当成界内。
	///
	/// 另有一处：`Path.Combine` 不做规范化，`<root>/..` 这个字符串仍以根为前缀，逐段校验时
	/// 必须显式拒绝 `..` 与 `.`，不能交给前缀比较。
	/// </summary>
	[Fact]
	public void 逐段校验拒绝点段()
	{
		WorkspaceAccess access = Access();

		Assert.Null(access.Resolve("sub/../../outside.txt"));
		Assert.Null(access.Resolve("./../escape"));
		Assert.Null(access.Resolve("a/./../../b"));
	}

	[Fact]
	public void 路径比较的大小写口径按平台取()
	{
		string sibling = _root.ToUpperInvariant();
		WorkspaceAccess access = Access();

		// Windows 上大小写不敏感，同一目录的大写形式仍是它自己；其余平台上是另一个目录。
		Assert.Equal(OperatingSystem.IsWindows(), access.Inside(sibling));
	}

	/// <summary>遍历拿到的绝对路径与相对路径走同一道判据。</summary>
	[Fact]
	public void 界外的绝对路径不被接受()
	{
		WorkspaceAccess access = Access();

		Assert.Null(access.Accept(Path.Combine(_outside, "secret.txt")));
		Assert.NotNull(access.Accept(Path.Combine(_root, "a.txt")));
	}

	// ---- 权限档位 ----

	/// <summary>
	/// 读是 safe、写是 confirm。
	///
	/// 配工作目录本身就是用户那次同意；再让每次读都弹框，「看看这个项目」会变成五十次点击，
	/// 用户会直接关掉这个功能 —— 那比没有还糟。写改的是用户的东西，每次都问。
	/// </summary>
	[Fact]
	public void 读是safe写要确认()
	{
		ToolRegistry registry = Registry();

		Assert.Equal("safe", registry.Get("readFile")!.PermissionLevel);
		Assert.Equal("safe", registry.Get("listFiles")!.PermissionLevel);
		Assert.Equal("safe", registry.Get("searchFiles")!.PermissionLevel);
		Assert.Equal("confirm", registry.Get("writeFile")!.PermissionLevel);
	}

	[Fact]
	public async Task 没有授权通道时写入被拒()
	{
		ToolResult result = await Registry().ExecuteAsync(
			"writeFile",
			JsonNode.Parse("{\"path\":\"a.txt\",\"content\":\"x\"}"),
			new ToolContext());

		Assert.False(result.IsSuccess);
		Assert.False(File.Exists(Path.Combine(_root, "a.txt")));
	}

	[Fact]
	public async Task 用户拒绝时不写入()
	{
		ToolResult result = await Registry().ExecuteAsync(
			"writeFile",
			JsonNode.Parse("{\"path\":\"a.txt\",\"content\":\"x\"}"),
			new ToolContext { Approve = _ => Task.FromResult(false) });

		Assert.False(result.IsSuccess);
		Assert.False(File.Exists(Path.Combine(_root, "a.txt")));
	}

	// ---- 可用性 ----

	[Fact]
	public async Task 列目录区分文件与文件夹并跳过噪音目录()
	{
		Write("a.txt", "甲");
		Write("src/b.cs", "乙");
		Write("node_modules/pkg/index.js", "丙");
		Write(".git/config", "丁");

		JsonElement listed = await CallAsync(Registry(), "listFiles", new { });
		string[] names = [.. listed.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()!)];

		Assert.Contains("a.txt", names);
		Assert.Contains("src", names);
		Assert.DoesNotContain("node_modules", names);
		Assert.DoesNotContain(".git", names);
	}

	[Fact]
	public async Task 读文件返回内容与相对路径()
	{
		Write("src/b.cs", "class B {}");

		JsonElement read = await CallAsync(Registry(), "readFile", new { path = "src/b.cs" });

		Assert.Equal("class B {}", read.GetProperty("content").GetString());
		Assert.Equal("src/b.cs", read.GetProperty("path").GetString());
		Assert.False(read.GetProperty("truncated").GetBoolean());
	}

	[Fact]
	public async Task 二进制文件不读进上下文()
	{
		File.WriteAllBytes(Path.Combine(_root, "a.bin"), [0x00, 0x01, 0x02, 0x00]);

		Assert.Contains("二进制", await FailAsync(Registry(), "readFile", new { path = "a.bin" }), StringComparison.Ordinal);
	}

	/// <summary>
	/// 超大文件截断之后返回的必须仍然是**结构**。
	///
	/// `ToolRegistry` 有一道 48,000 字符的结果上限，超了就把整个结果序列化再截断 —— 模型拿到
	/// 的会是一段半截 JSON 文本，既读不出文件名也读不全内容，而且看不出发生了什么。所以工具
	/// 自己要先把结果收进这个预算里。
	/// </summary>
	[Fact]
	public async Task 超大文件截断后仍是结构化结果()
	{
		File.WriteAllText(Path.Combine(_root, "big.txt"), new string('x', WorkspaceAccess.MaxReadBytes + 5000));

		JsonElement read = await CallAsync(Registry(), "readFile", new { path = "big.txt" });

		Assert.Equal(JsonValueKind.Object, read.ValueKind);
		Assert.Equal("big.txt", read.GetProperty("path").GetString());
		Assert.True(read.GetProperty("truncated").GetBoolean());
		Assert.NotEmpty(read.GetProperty("content").GetString()!);
	}

	/// <summary>非 ASCII 会被 JSON 转义成 6 个字符一枚，膨胀率和纯英文完全不同。</summary>
	[Fact]
	public async Task 中文大文件同样收进预算()
	{
		File.WriteAllText(Path.Combine(_root, "big-zh.txt"), new string('文', 60_000));

		JsonElement read = await CallAsync(Registry(), "readFile", new { path = "big-zh.txt" });

		Assert.Equal(JsonValueKind.Object, read.ValueKind);
		Assert.True(read.GetProperty("truncated").GetBoolean());
		Assert.NotEmpty(read.GetProperty("content").GetString()!);
	}

	[Fact]
	public async Task 命中过多时丢掉尾部而不是整个结果被压扁()
	{
		for (int index = 0; index < 300; index++)
		{
			Write($"f{index}.txt", "目标目标目标目标目标目标目标目标目标目标目标目标目标目标目标目标");
		}

		JsonElement found = await CallAsync(Registry(), "searchFiles", new { query = "目标" });

		Assert.Equal(JsonValueKind.Object, found.ValueKind);
		Assert.True(found.GetProperty("truncated").GetBoolean());
		Assert.NotEmpty(found.GetProperty("hits").EnumerateArray().ToArray());
	}

	[Fact]
	public async Task 搜索给出文件行号与该行内容()
	{
		Write("src/b.cs", "class B {}\nvoid 找我() {}\n");
		Write("src/c.txt", "无关内容");

		JsonElement found = await CallAsync(Registry(), "searchFiles", new { query = "找我" });
		JsonElement[] hits = [.. found.GetProperty("hits").EnumerateArray()];

		Assert.Single(hits);
		Assert.Equal("src/b.cs", hits[0].GetProperty("path").GetString());
		Assert.Equal(2, hits[0].GetProperty("line").GetInt32());
		Assert.Contains("找我", hits[0].GetProperty("text").GetString()!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 搜索能按扩展名收窄且跳过噪音目录()
	{
		Write("a.cs", "目标");
		Write("a.txt", "目标");
		Write("node_modules/x.cs", "目标");

		JsonElement found = await CallAsync(Registry(), "searchFiles", new { query = "目标", extension = "cs" });
		string[] paths = [.. found.GetProperty("hits").EnumerateArray().Select(e => e.GetProperty("path").GetString()!)];

		Assert.Equal(["a.cs"], paths);
	}

	[Fact]
	public async Task 写入并能读回且报告是否覆盖()
	{
		ToolRegistry registry = Registry();

		JsonElement first = await CallAsync(registry, "writeFile", new { path = "notes/n.md", content = "第一版" });
		Assert.False(first.GetProperty("overwritten").GetBoolean());

		JsonElement second = await CallAsync(registry, "writeFile", new { path = "notes/n.md", content = "第二版" });
		Assert.True(second.GetProperty("overwritten").GetBoolean());

		Assert.Equal("第二版", File.ReadAllText(Path.Combine(_root, "notes", "n.md")));
	}

	[Fact]
	public async Task 写入越界路径被拒()
	{
		string error = await FailAsync(Registry(), "writeFile", new { path = "../escaped.txt", content = "x" });

		Assert.Contains("超出工作目录", error, StringComparison.Ordinal);
		Assert.False(File.Exists(Path.Combine(_outside, "..", "escaped.txt")));
	}

	[Fact]
	public async Task 写入超过上限被拒()
	{
		string error = await FailAsync(
			Registry(),
			"writeFile",
			new { path = "big.txt", content = new string('x', WorkspaceAccess.MaxWriteBytes + 10) });

		Assert.Contains("上限", error, StringComparison.Ordinal);
		Assert.False(File.Exists(Path.Combine(_root, "big.txt")));
	}

	[Fact]
	public void 二进制判据认NUL字节()
	{
		Assert.True(WorkspaceAccess.LooksBinary(new byte[] { 0x41, 0x00, 0x42 }));
		Assert.False(WorkspaceAccess.LooksBinary(Encoding.UTF8.GetBytes("纯文本 plain text")));
	}
}
