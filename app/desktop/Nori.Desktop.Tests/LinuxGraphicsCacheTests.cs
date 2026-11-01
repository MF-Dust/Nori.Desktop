using System.Diagnostics;
using System.Runtime.InteropServices;
using Nori.Core.Data;

namespace Nori.Desktop.Tests;

[Collection("Native settings")]
public sealed class LinuxGraphicsCacheTests
{
	[Fact]
	public void 图形缓存使用包内资源缓存而非继承的用户目录()
	{
		const string Variable = "MESA_SHADER_CACHE_DIR";
		string? previous = Environment.GetEnvironmentVariable(Variable);
		string? nativePrevious = System.OperatingSystem.IsLinux() ? Marshal.PtrToStringUTF8(GetEnvironment(Variable)) : null;
		string packageRoot = Path.Combine(Path.GetTempPath(), $"nori-图形缓存 空格-{Guid.NewGuid():N}");
		AppStoragePaths paths = new(packageRoot);
		string inherited = Path.Combine(packageRoot, "outside-data");
		try
		{
			Environment.SetEnvironmentVariable(Variable, inherited);
			Program.ConfigureGraphicsCache(paths);

			Assert.Equal(inherited, Environment.GetEnvironmentVariable(Variable));
			if (System.OperatingSystem.IsLinux())
			{
				Assert.Equal(paths.ResourcesCacheDirectory, Marshal.PtrToStringUTF8(GetEnvironment(Variable)));
				ProcessStartInfo info = new("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
				info.ArgumentList.Add("-c");
				info.ArgumentList.Add("printf '%s' \"$MESA_SHADER_CACHE_DIR\"");
				using Process child = Process.Start(info)!;
				string childValue = child.StandardOutput.ReadToEnd();
				Assert.True(child.WaitForExit(5000));
				Assert.Equal(0, child.ExitCode);
				Assert.Equal(inherited, childValue);
			}
			Assert.True(AppStoragePaths.IsContained(paths.ResourcesCacheDirectory, paths.DataRoot));
			// 初始化目录仍由存储引导负责；配置环境变量不会触碰旧缓存或创建包外文件。
			Assert.False(Directory.Exists(packageRoot));
		}
		finally
		{
			Environment.SetEnvironmentVariable(Variable, previous);
			if (System.OperatingSystem.IsLinux())
			{
				if (nativePrevious is null) UnsetEnvironment(Variable);
				else SetEnvironment(Variable, nativePrevious, 1);
			}
		}
	}

	[DllImport("libc", EntryPoint = "getenv")]
	private static extern nint GetEnvironment(string name);
	[DllImport("libc", EntryPoint = "setenv")]
	private static extern int SetEnvironment(string name, string value, int overwrite);
	[DllImport("libc", EntryPoint = "unsetenv")]
	private static extern int UnsetEnvironment(string name);
}
