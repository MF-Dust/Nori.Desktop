using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nori.Core.Platform;

[SupportedOSPlatform("linux")]
internal static class LinuxGraphicsCache
{
	internal static void Configure(string directory)
	{
		const string Variable = "MESA_SHADER_CACHE_DIR";
		// .NET 的环境变量副本不更新 libc getenv；Mesa 和后续子进程都必须看到同一路径。
		if (SetEnvironment(Variable, directory, 1) != 0)
			throw new IOException("无法配置包内 Mesa 图形缓存目录", new Win32Exception(Marshal.GetLastPInvokeError()));
		Environment.SetEnvironmentVariable(Variable, directory);
	}

	[DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
	private static extern int SetEnvironment(string name, string value, int overwrite);
}
