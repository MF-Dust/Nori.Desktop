using System.IO.Compression;
using System.Text.Json.Nodes;
using Nori.PluginRuntime;
using Nori.PluginRuntime.TestPlugin;

namespace Nori.PluginRuntime.Tests;

public sealed class PluginRuntimeReviewTests
{
	[Fact]
	public async Task 禁用尚未激活的插件会保持Disabled状态()
	{
		string root = CreateTemp();
		try
		{
			string package = CreateTestPackage(root, "disabled.plugin");
			PluginManager manager = new(new PluginRuntimeOptions
			{
				PluginsDirectory = Path.Combine(root, "plugins"),
				DataDirectory = Path.Combine(root, "plugin-data"),
				KnownCapabilityIds = [],
			});
			manager.Installer.Install(package);
			manager.Discover();

			await manager.DisableAsync("disabled.plugin");

			Assert.Equal(PluginLifecycleState.Disabled, Assert.Single(manager.Plugins).State);
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public void 安装目录中的Contract副本会在加载前被拒绝()
	{
		string root = CreateTemp();
		try
		{
			string lib = Path.Combine(root, "lib");
			Directory.CreateDirectory(lib);
			File.Copy(typeof(INoriPlugin).Assembly.Location, Path.Combine(lib, "Nori.Plugin.Abstractions.dll"));

			PluginException exception = Assert.Throws<PluginException>(() => PluginLoadContext.EnsureReferencesAllowed(root));
			Assert.Equal(PluginErrorCodes.ContractAssemblyDenied, exception.Code);
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public void Windows原生Dll不会被误判为损坏的托管程序集()
	{
		if (!OperatingSystem.IsWindows()) return;

		string root = CreateTemp();
		try
		{
			string runtime = Path.Combine(root, "runtimes", "win-x64", "native");
			Directory.CreateDirectory(runtime);
			string systemDll = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
			Assert.True(File.Exists(systemDll));
			File.Copy(systemDll, Path.Combine(runtime, "plugin-native.dll"));

			PluginLoadContext.EnsureReferencesAllowed(root);
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task Unix允许系统级符号链接祖先但仍拒绝插件根本身是链接()
	{
		if (OperatingSystem.IsWindows()) return;

		string root = CreateTemp();
		try
		{
			string realParent = Path.Combine(root, "real-parent");
			Directory.CreateDirectory(realParent);
			string linkedParent = Path.Combine(root, "linked-parent");
			Directory.CreateSymbolicLink(linkedParent, realParent);

			PluginPackageInstaller installer = new(Path.Combine(linkedParent, "plugins"));
			Assert.True(Directory.Exists(installer.RootDirectory));

			JsonPluginStorage storage = new(Path.Combine(linkedParent, "plugin-data", "demo.plugin"));
			await storage.SetAsync("state", new JsonObject { ["ok"] = true });
			Assert.True((await storage.GetAsync("state"))!["ok"]!.GetValue<bool>());

			string realPluginRoot = Path.Combine(root, "real-plugin-root");
			Directory.CreateDirectory(realPluginRoot);
			string linkedPluginRoot = Path.Combine(root, "linked-plugin-root");
			Directory.CreateSymbolicLink(linkedPluginRoot, realPluginRoot);

			PluginException exception = Assert.Throws<PluginException>(() => new PluginPackageInstaller(linkedPluginRoot));
			Assert.Equal(PluginErrorCodes.PackagePathDenied, exception.Code);
		}
		finally { DeleteDirectory(root); }
	}

	private static string CreateTestPackage(string root, string id)
	{
		string package = Path.Combine(root, $"{id}.noripack");
		string assembly = typeof(Nori.PluginRuntime.TestPlugin.TestPlugin).Assembly.Location;
		using FileStream file = File.Create(package);
		using ZipArchive archive = new(file, ZipArchiveMode.Create);
		WriteEntry(archive, "manifest.json", $"{{\"schemaVersion\":1,\"id\":\"{id}\",\"name\":\"Demo\",\"description\":\"Demo plugin\",\"version\":\"1.0.0\",\"authors\":[{{\"name\":\"Nori\"}}],\"apiVersion\":\"2.0\",\"minHostVersion\":\"1.0.0\",\"runtime\":{{\"kind\":\"dotnet\",\"assembly\":\"lib/Nori.PluginRuntime.TestPlugin.dll\",\"entryType\":\"Nori.PluginRuntime.TestPlugin.TestPlugin\"}},\"capabilities\":[],\"optionalCapabilities\":[],\"platforms\":[],\"dependencies\":[]}}");
		ZipArchiveEntry assemblyEntry = archive.CreateEntry("lib/Nori.PluginRuntime.TestPlugin.dll");
		using Stream target = assemblyEntry.Open();
		using FileStream source = File.OpenRead(assembly);
		source.CopyTo(target);
		return package;
	}

	private static void WriteEntry(ZipArchive archive, string name, string content)
	{
		using StreamWriter writer = new(archive.CreateEntry(name).Open());
		writer.Write(content);
	}

	private static string CreateTemp()
	{
		string path = Path.Combine(Path.GetTempPath(), "nori-plugin-review-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static void DeleteDirectory(string path)
	{
		try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } // NOSONAR: 测试夹具销毁阶段只能尽力清理，不能让清理异常覆盖测试结果。
	}
}
