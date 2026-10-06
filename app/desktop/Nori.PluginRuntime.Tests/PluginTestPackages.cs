using System.IO.Compression;

namespace Nori.PluginRuntime.Tests;

/// <summary>测试插件包的机械操作：写 ZIP 条目、复制程序集、建临时目录与清理。manifest 与包名策略留在各测试类。</summary>
internal static class PluginTestPackages
{
	internal static void WriteEntry(ZipArchive archive, string name, string content)
	{
		using StreamWriter writer = new(archive.CreateEntry(name).Open());
		writer.Write(content);
	}

	internal static void WriteAssemblyEntry(ZipArchive archive, string name, string assemblyPath)
	{
		ZipArchiveEntry entry = archive.CreateEntry(name);
		using Stream target = entry.Open();
		using FileStream source = File.OpenRead(assemblyPath);
		source.CopyTo(target);
	}

	internal static string CreateTemp(string directoryName) => Directory.CreateTempSubdirectory($"{directoryName}-").FullName;

	internal static void DeleteDirectory(string path)
	{
		// 调用前先结束插件操作阶段，避免测试栈或异步状态机仍持有插件类型及异常。
		// ALC.Unload 只发出卸载请求，回收已卸载的上下文后才能释放 Windows 的 DLL 映射。
		for (int attempt = 0; ; attempt++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			try
			{
				if (Directory.Exists(path)) Directory.Delete(path, true);
				return;
			}
			catch (Exception exception) when (OperatingSystem.IsWindows() && attempt < 9 &&
				exception is IOException or UnauthorizedAccessException)
			{
				// 等待已结束的线程池任务释放最后的引用及 DLL 映射，最多重试 450 毫秒。
				Thread.Sleep(50);
			}
		}
	}
}
