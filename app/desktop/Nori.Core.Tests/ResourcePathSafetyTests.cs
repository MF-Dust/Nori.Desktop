using System.Diagnostics;
using System.Runtime.InteropServices;
using Nori.Core.Resources;

namespace Nori.Core.Tests;

/// <summary>
/// 资源路径安全检查测试。
///
/// NORI-1T/1S 回归: 目录段此前错误地走 File.ResolveLinkTarget 而抛 IOException,
/// 普通目录必须能通过检查。junction 不需要特权, 在 Windows 上必须真实运行;
/// symlink 依赖开发者模式/特权, 创建失败时按平台条件跳过。
/// </summary>
public sealed class ResourcePathSafetyTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("nori-rps-").FullName;

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	[Fact]
	public void 普通目录逐段检查通过()
	{
		string nested = Path.Combine(_root, "staging", "model", "sub");
		Directory.CreateDirectory(nested);

		ResourcePathSafety.EnsureNoReparsePoints(_root, nested, "不允许的路径");
		ResourcePathSafety.EnsureNoReparsePointsAlongPath(nested, "不允许的路径");
	}

	[Fact]
	public void 普通文件检查通过()
	{
		string file = Path.Combine(_root, "model3.json");
		File.WriteAllText(file, "{}");

		ResourcePathSafety.EnsureNoReparsePoints(_root, file, "不允许的路径");
	}

	[Fact]
	public void 不存在的末尾路径不当作链接拒绝()
	{
		string missing = Path.Combine(_root, "missing", "deeper");
		Directory.CreateDirectory(Path.Combine(_root, "missing"));

		ResourcePathSafety.EnsureNoReparsePoints(_root, missing, "不允许的路径");
	}

	[Fact]
	public void 越界路径被containment拒绝()
	{
		string outside = Path.Combine(_root, "..", "outside");

		Assert.Throws<ResourceException>(() =>
			ResourcePathSafety.EnsureNoReparsePoints(_root, outside, "不允许的路径"));
	}

	[Fact]
	public void 路径中段的链接被拒绝()
	{
		// root/real 是普通目录; root/link 指向它, 通过 link 访问必须被拒。
		string real = Path.Combine(_root, "real");
		Directory.CreateDirectory(real);
		string link = Path.Combine(_root, "link");
		CreateDirectoryLink(link, real);

		Assert.Throws<ResourceException>(() =>
			ResourcePathSafety.EnsureNoReparsePoints(_root, Path.Combine(link, "inner.json"), "不允许的路径"));
	}

	[Fact]
	public void Windows目录junction被拒绝()
	{
		if (!OperatingSystem.IsWindows()) return; // junction 仅 Windows 存在

		string real = Path.Combine(_root, "junction-target");
		Directory.CreateDirectory(real);
		string junction = Path.Combine(_root, "junction");
		RunLinkTool($"/c mklink /J \"{junction}\" \"{real}\"");

		Assert.True(Directory.Exists(junction), "junction 创建失败");
		Assert.Throws<ResourceException>(() =>
			ResourcePathSafety.EnsureNoReparsePoints(_root, junction, "不允许的路径"));
		Assert.Throws<ResourceException>(() =>
			ResourcePathSafety.EnsureNoReparsePoints(_root, Path.Combine(junction, "child.json"), "不允许的路径"));
	}

	[Fact]
	public void 目录符号链接被拒绝()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !CanCreateSymbolicLinks()) return;

		string real = Path.Combine(_root, "symlink-dir-target");
		Directory.CreateDirectory(real);
		string link = Path.Combine(_root, "symlink-dir");
		RunLinkTool(OperatingSystem.IsWindows()
			? $"/c mklink /D \"{link}\" \"{real}\""
			: $"-s \"{real}\" \"{link}\"");

		Assert.Throws<ResourceException>(() =>
			ResourcePathSafety.EnsureNoReparsePoints(_root, link, "不允许的路径"));
	}

	[Fact]
	public void 文件符号链接被拒绝()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !CanCreateSymbolicLinks()) return;

		string real = Path.Combine(_root, "symlink-file-target.json");
		File.WriteAllText(real, "{}");
		string link = Path.Combine(_root, "symlink-file.json");
		RunLinkTool(OperatingSystem.IsWindows()
			? $"/c mklink \"{link}\" \"{real}\""
			: $"-s \"{real}\" \"{link}\"");

		Assert.Throws<ResourceException>(() =>
			ResourcePathSafety.EnsureNoReparsePoints(_root, link, "不允许的路径"));
	}

	/// <summary>Windows 上 symlink 需要开发者模式或管理员特权; 创建能力探测一次并缓存。</summary>
	private static bool? _canCreateSymbolicLinks;

	private static bool CanCreateSymbolicLinks()
	{
		if (_canCreateSymbolicLinks.HasValue) return _canCreateSymbolicLinks.Value;
		string probeDir = Directory.CreateTempSubdirectory("nori-symlink-probe-").FullName;
		try
		{
			string link = Path.Combine(probeDir, "probe-link");
			RunLinkTool($"/c mklink /D \"{link}\" \"{probeDir}\"");
			_canCreateSymbolicLinks = Directory.Exists(link);
		}
		catch
		{
			_canCreateSymbolicLinks = false;
		}
		finally
		{
			try { Directory.Delete(probeDir, recursive: true); } catch { }
		}
		return _canCreateSymbolicLinks.Value;
	}

	private static void CreateDirectoryLink(string link, string target)
	{
		if (OperatingSystem.IsWindows())
		{
			RunLinkTool($"/c mklink /J \"{link}\" \"{target}\"");
			return;
		}
		RunLinkTool($"-s \"{target}\" \"{link}\"");
	}

	private static void RunLinkTool(string arguments)
	{
		bool isWindows = OperatingSystem.IsWindows();
		// 测试辅助程序只在固定的 cmd.exe 与 ln 中二选一。
		ProcessStartInfo startInfo = isWindows // nosemgrep
			? new ProcessStartInfo("cmd.exe", arguments)
			: new ProcessStartInfo("ln", arguments);
		startInfo.UseShellExecute = false;
		startInfo.CreateNoWindow = true;
		startInfo.RedirectStandardError = true;
		using Process? process = Process.Start(startInfo) // nosemgrep
			?? throw new InvalidOperationException("无法启动链接创建工具");
		string error = process.StandardError.ReadToEnd();
		process.WaitForExit(5000);
		if (!process.HasExited || process.ExitCode != 0)
			throw new InvalidOperationException($"链接创建失败: {error}");
	}
}
