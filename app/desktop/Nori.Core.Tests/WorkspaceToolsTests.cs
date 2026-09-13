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

	// ---- 边界 ----

	[Theory]
	[InlineData("../secret.txt")]
	[InlineData("../../etc/passwd")]
	[InlineData("sub/../../outside.txt")]
	[InlineData("C:/Windows/System32/drivers/etc/hosts")]
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
