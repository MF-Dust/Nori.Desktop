using Nori.Core.Agent;

namespace Nori.Core.Tests;

/// <summary>
/// 提示词里的工作目录分段。
///
/// 文件工具的描述里只写「工作目录」，不含具体路径。缺这一段时模型回答不了「你能看到哪个
/// 文件夹」，也无法把用户贴来的绝对路径换算成工具要求的相对路径 —— 而绝对路径一律判越界，
/// 表现为模型换着写法反复重试同一个文件。
/// </summary>
public sealed class PromptWorkspaceTests
{
	private static string Build(string workspaceRoot) =>
		PromptBuilder.Build(new PromptBuildOptions { WorkspaceRoot = workspaceRoot, ToolsJson = "[]" });

	[Fact]
	public void 配置了工作目录时提示词里给出路径()
	{
		string prompt = Build(@"D:\projects\demo");

		Assert.Contains(@"D:\projects\demo", prompt, StringComparison.Ordinal);
		Assert.Contains("当前工作目录", prompt, StringComparison.Ordinal);
	}

	/// <summary>换算规则要写出来：不写的话模型会把用户贴来的绝对路径原样传进工具。</summary>
	[Fact]
	public void 说明相对路径的换算规则()
	{
		string prompt = Build(@"D:\projects\demo");

		Assert.Contains("相对路径", prompt, StringComparison.Ordinal);
		Assert.Contains("绝对路径", prompt, StringComparison.Ordinal);
	}

	[Fact]
	public void 没有工作目录时整段不注入()
	{
		string prompt = Build("");

		Assert.DoesNotContain("当前工作目录", prompt, StringComparison.Ordinal);
	}

	[Fact]
	public void 只有空白的配置值同样不注入()
	{
		Assert.DoesNotContain("当前工作目录", Build("   "), StringComparison.Ordinal);
	}

	// ---- 注入判据 ----

	[Fact]
	public void 文件工具已注册时取配置值()
	{
		Assert.Equal(
			@"D:\work",
			PromptBuilder.WorkspaceRootFor(
				new HashSet<string>(StringComparer.Ordinal) { "readFile", "writeFile" }, @"D:\work"));
	}

	/// <summary>
	/// 安全模式下配置项仍在库里、文件工具已被注销。此时按配置注入的后果是模型看到一个
	/// 工作目录却调不到任何文件工具，表现为它坚持说自己能读文件然后每次都失败。
	/// </summary>
	[Fact]
	public void 文件工具未注册时不取配置值()
	{
		Assert.Equal(
			"",
			PromptBuilder.WorkspaceRootFor(
				new HashSet<string>(StringComparer.Ordinal) { "calculate", "getWeather" }, @"D:\work"));
	}

	[Fact]
	public void 一件工具都没有时同样为空()
	{
		Assert.Equal("", PromptBuilder.WorkspaceRootFor(new HashSet<string>(StringComparer.Ordinal), @"D:\work"));
	}
}
