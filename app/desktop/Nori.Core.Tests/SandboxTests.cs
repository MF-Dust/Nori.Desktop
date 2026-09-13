using System.Text;
using Nori.Core.Sandbox;
using Nori.Core.Sandbox.Windows;

namespace Nori.Core.Tests;

/// <summary>
/// 受限执行。
///
/// 这一族的重点是隔离强度必须被如实报告：把 <see cref="SandboxIsolation.None"/> 当成受限
/// 执行来放宽确认要求，等于在没有边界的情况下让模型跑命令。
/// </summary>
public sealed class SandboxTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"nori-sbx-{Guid.NewGuid():N}");

	public SandboxTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* 清理失败不影响断言 */ }
	}

	private static string Echo(string text) => OperatingSystem.IsWindows()
		? $"cmd.exe /c echo {text}"
		: $"/bin/sh -c \"echo {text}\"";

	// ---- 命令行拆分 ----

	[Theory]
	[InlineData("dotnet build", "dotnet", "build")]
	[InlineData("dotnet", "dotnet", "")]
	[InlineData("  dotnet   build -v q  ", "dotnet", "build -v q")]
	public void 命令行按首个空格拆分(string input, string file, string arguments)
	{
		Assert.Equal((file, arguments), CommandLine.Split(input));
	}

	/// <summary>可执行文件路径常含空格，首段必须支持引号。</summary>
	[Fact]
	public void 带引号的可执行文件路径被正确拆出()
	{
		Assert.Equal(
			(@"C:\Program Files\dotnet\dotnet.exe", "build -v q"),
			CommandLine.Split(@"""C:\Program Files\dotnet\dotnet.exe"" build -v q"));
	}

	[Fact]
	public void 拼回去的命令行给含空格的路径加引号()
	{
		Assert.Equal(@"""C:\a b\x.exe"" run", CommandLine.Join(@"C:\a b\x.exe", "run"));
		Assert.Equal("dotnet build", CommandLine.Join("dotnet", "build"));
	}

	// ---- 输出解码 ----

	[Fact]
	public void UTF8输出按UTF8解码()
	{
		Assert.Equal("构建成功", SandboxOutput.Decode(Encoding.UTF8.GetBytes("构建成功")));
	}

	/// <summary>
	/// 非 UTF-8 的输出不能抛异常，也不能把 ASCII 部分一起丢掉。
	///
	/// 中文 Windows 上 `cmd.exe` 输出 GBK。按 UTF-8 严格解会失败，退到 Latin1 之后中文是
	/// 乱码，但编译错误里的文件名、行号、错误码是 ASCII，必须完整保留 —— 那才是模型要读的。
	/// </summary>
	[Fact]
	public void 非UTF8输出退到Latin1且保住ASCII部分()
	{
		byte[] gbk = [.. Encoding.ASCII.GetBytes("Program.cs(12,5): error CS0103: "), 0xC3, 0xBB, 0xD3, 0xD0];

		string decoded = SandboxOutput.Decode(gbk);

		Assert.Contains("Program.cs(12,5): error CS0103:", decoded, StringComparison.Ordinal);
		Assert.Equal(gbk.Length, decoded.Length);
	}

	// ---- 输出截断 ----

	[Fact]
	public void 未超限时原样返回()
	{
		(string text, bool truncated) = SandboxOutput.Fit("短输出", 1000);

		Assert.Equal("短输出", text);
		Assert.False(truncated);
	}

	/// <summary>
	/// 构建工具的失败摘要在尾部、编译错误在中间偏前，只留头部会把「错在哪」丢掉。
	/// </summary>
	[Fact]
	public void 超限时首尾都保留()
	{
		string text = "开头标记" + new string('x', 50_000) + "结尾标记";

		(string fitted, bool truncated) = SandboxOutput.Fit(text, 2_000);

		Assert.True(truncated);
		Assert.StartsWith("开头标记", fitted, StringComparison.Ordinal);
		Assert.EndsWith("结尾标记", fitted, StringComparison.Ordinal);
		Assert.Contains("中间省略", fitted, StringComparison.Ordinal);
		Assert.True(fitted.Length < 2_200, $"截断后仍有 {fitted.Length} 字符");
	}

	// ---- 无隔离执行 ----

	[Fact]
	public async Task 无隔离执行如实报告自己没有隔离()
	{
		UnsandboxedLauncher launcher = new();

		Assert.Equal(SandboxIsolation.None, launcher.Isolation);
		Assert.Contains("无沙箱", launcher.Describe(), StringComparison.Ordinal);
		await Task.CompletedTask;
	}

	[Fact]
	public async Task 无隔离执行返回输出与退出码()
	{
		SandboxResult result = await new UnsandboxedLauncher().RunAsync(
			Echo("hello-sandbox"), new SandboxPolicy { WorkspaceRoot = _root }, CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("hello-sandbox", result.Output, StringComparison.Ordinal);
		Assert.False(result.TimedOut);
	}

	[Fact]
	public async Task 当前目录钉在工作目录()
	{
		string command = OperatingSystem.IsWindows() ? "cmd.exe /c cd" : "/bin/sh -c pwd";

		SandboxResult result = await new UnsandboxedLauncher().RunAsync(
			command, new SandboxPolicy { WorkspaceRoot = _root }, CancellationToken.None);

		// 临时目录在 macOS 上可能经由符号链接，比较末段即可。
		Assert.Contains(Path.GetFileName(_root), result.Output, StringComparison.Ordinal);
	}

	/// <summary>超时必须结束进程并置位，不能把超时表现成「命令成功但没输出」。</summary>
	[Fact]
	public async Task 超时会结束进程并置位()
	{
		string command = OperatingSystem.IsWindows()
			? "cmd.exe /c ping -n 30 127.0.0.1"
			: "/bin/sh -c \"sleep 30\"";

		SandboxResult result = await new UnsandboxedLauncher().RunAsync(
			command,
			new SandboxPolicy { WorkspaceRoot = _root, Timeout = TimeSpan.FromSeconds(2) },
			CancellationToken.None);

		Assert.True(result.TimedOut);
		Assert.Equal(-1, result.ExitCode);
	}

	[Fact]
	public async Task 环境变量覆盖传得进去()
	{
		string command = OperatingSystem.IsWindows()
			? "cmd.exe /c echo %NORI_SANDBOX_PROBE%"
			: "/bin/sh -c \"echo $NORI_SANDBOX_PROBE\"";

		SandboxResult result = await new UnsandboxedLauncher().RunAsync(
			command,
			new SandboxPolicy
			{
				WorkspaceRoot = _root,
				EnvironmentOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					["NORI_SANDBOX_PROBE"] = "probe-value",
				},
			},
			CancellationToken.None);

		Assert.Contains("probe-value", result.Output, StringComparison.Ordinal);
	}

	// ---- 选型 ----

	[Fact]
	public void 不要求隔离时直接给无隔离实现()
	{
		Assert.Equal(SandboxIsolation.None, SandboxLauncherFactory.Create(preferIsolation: false).Isolation);
	}

	/// <summary>
	/// Windows 上应当拿到 AppContainer，其余平台目前只有无隔离实现。
	///
	/// 传入独立容器名并在结束时删除：默认名的配置文件是应用的持久状态，测试不该留下它。
	/// </summary>
	[Fact]
	public void 按平台挑选隔离实现()
	{
		string name = "Nori.Desktop.Test." + Guid.NewGuid().ToString("N")[..8];
		ISandboxLauncher launcher = SandboxLauncherFactory.Create(containerName: name);
		try
		{
			Assert.Equal(
				OperatingSystem.IsWindows() ? SandboxIsolation.AppContainer : SandboxIsolation.None,
				launcher.Isolation);
		}
		finally
		{
			if (OperatingSystem.IsWindows() && launcher is AppContainerLauncher container) container.DeleteProfile();
		}
	}

	// ---- AppContainer 实机 ----

	/// <summary>
	/// 目录的 ACL 里有没有这个容器的 ACE。
	///
	/// 授权就是往 DACL 里加一条该 SID 的 ACE，所以这是判断「授权还在不在」的直接判据。
	/// </summary>
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static bool HasGrant(string path, string containerSid)
	{
		System.Security.AccessControl.AuthorizationRuleCollection rules =
			new DirectoryInfo(path).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access)
				.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier));

		return rules.Cast<System.Security.AccessControl.FileSystemAccessRule>().Any(
			rule => rule.IdentityReference.Value.Equals(containerSid, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// 释放之后目录上的授权必须真的消失。
	///
	/// 这条是回归测试。原实现写了「必须在工作目录变更时调用」的文档，却没有任何生产调用点 ——
	/// 用户换一个工作目录，旧目录上的 ACE 就永久残留，他看不见也无从清理。
	/// </summary>
	[Fact]
	public async Task 释放之后工作目录上的授权真的消失()
	{
		if (!OperatingSystem.IsWindows()) return;

		AppContainerLauncher launcher = new("Nori.Desktop.Test." + Guid.NewGuid().ToString("N")[..8]);
		SandboxPolicy policy = new() { WorkspaceRoot = _root, Timeout = TimeSpan.FromSeconds(30) };
		try
		{
			launcher.EnsureReady();
			Assert.False(HasGrant(_root, launcher.ContainerSid), "还没跑过任何命令，不该有授权");

			await launcher.RunAsync("cmd.exe /c echo inside> probe.txt", policy, CancellationToken.None);
			Assert.True(HasGrant(_root, launcher.ContainerSid), "跑过之后应当有授权");

			launcher.Release(policy);

			Assert.False(HasGrant(_root, launcher.ContainerSid), "释放之后授权必须消失");
		}
		finally
		{
			launcher.DeleteProfile();
		}
	}

	/// <summary>释放必须可重复调用，且对从未授权过的路径安全 —— 清理路径上的异常会掩盖真正的问题。</summary>
	[Fact]
	public void 释放从未授权过的路径不报错()
	{
		if (!OperatingSystem.IsWindows()) return;

		AppContainerLauncher launcher = new("Nori.Desktop.Test." + Guid.NewGuid().ToString("N")[..8]);
		SandboxPolicy policy = new() { WorkspaceRoot = _root };

		launcher.Release(policy);
		launcher.Release(policy);

		// 释放不该把容器建出来：为了清理残留反而新增一份残留。
		Assert.False(
			Directory.Exists(Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "Nori.Desktop.Test")),
			"释放不应创建容器配置文件");
	}

	/// <summary>无隔离实现不产生持久授权，释放是空操作但必须存在 —— 调用方不该做平台判断。</summary>
	[Fact]
	public void 无隔离实现的释放是空操作()
	{
		new UnsandboxedLauncher().Release(new SandboxPolicy { WorkspaceRoot = _root });
		Assert.True(Directory.Exists(_root));
	}

	/// <summary>
	/// 在真实容器里跑一条命令，并验证围栏成立。
	///
	/// 这条是集成测试：它建立容器配置文件、修改临时目录的 ACL，结束时全部撤销。判据取
	/// 「界外写入没有落盘」而不是退出码 —— 被拒绝的写在不同 shell 下退出码不一致。
	/// </summary>
	[Fact]
	public async Task AppContainer里能跑命令且写不出工作目录()
	{
		if (!OperatingSystem.IsWindows()) return;

		string outside = Path.Combine(Path.GetTempPath(), $"nori-sbx-escape-{Guid.NewGuid():N}.txt");
		AppContainerLauncher launcher = new("Nori.Desktop.Test." + Guid.NewGuid().ToString("N")[..8]);
		SandboxPolicy policy = new() { WorkspaceRoot = _root, Timeout = TimeSpan.FromSeconds(30) };

		try
		{
			launcher.EnsureReady();

			SandboxResult inside = await launcher.RunAsync(
				"cmd.exe /c echo inside> probe.txt", policy, CancellationToken.None);
			Assert.Equal(0, inside.ExitCode);
			Assert.True(File.Exists(Path.Combine(_root, "probe.txt")), "工作目录内的写入应当成功");
			Assert.Equal(SandboxIsolation.AppContainer, launcher.Isolation);

			await launcher.RunAsync($"cmd.exe /c echo escaped> {outside}", policy, CancellationToken.None);
			Assert.False(File.Exists(outside), "工作目录外的写入必须失败");
		}
		finally
		{
			try { launcher.Release(policy); } catch (UnauthorizedAccessException) { /* 清理失败不影响断言 */ }
			launcher.DeleteProfile();
			try { File.Delete(outside); } catch (IOException) { /* 同上 */ }
		}
	}
}
