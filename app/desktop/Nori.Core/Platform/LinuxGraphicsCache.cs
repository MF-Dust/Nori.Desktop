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
		// 仅更新 libc getenv 供进程内 Mesa 使用；保留 .NET 环境副本，使子进程继续继承用户原值。
		if (SetEnvironment(Variable, directory, 1) != 0)
			throw new IOException("无法配置包内 Mesa 图形缓存目录", new Win32Exception(Marshal.GetLastPInvokeError()));
	}

	[DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
	private static extern int SetEnvironment(string name, string value, int overwrite);
}
