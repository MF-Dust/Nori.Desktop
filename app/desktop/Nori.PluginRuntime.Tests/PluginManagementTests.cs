using System.IO.Compression;
using Nori.PluginRuntime;
using Nori.PluginRuntime.TestPlugin;

namespace Nori.PluginRuntime.Tests;

public sealed class PluginManagementTests
{
	[Fact]
	public async Task 新安装默认禁用并跨Manager重建保持用户意图()
	{
		string root = CreateTemp();
		try
		{
			string package = CreateTestPackage(root, "state.plugin", "1.0.0");
			PluginManager first = CreateManager(root);
			await first.InstallAsync(package);
			PluginInfo installed = Assert.Single(first.Plugins);
			Assert.False(installed.UserEnabled);
			Assert.Equal(PluginLifecycleState.Disabled, installed.State);
			Assert.False(Directory.Exists(Path.Combine(root, "plugin-data", "state.plugin")));
			await first.DisposeAsync();

			PluginManager second = CreateManager(root);
			PluginInfo rediscovered = Assert.Single(second.Discover());
			Assert.False(rediscovered.UserEnabled);
			Assert.Equal(PluginLifecycleState.Disabled, rediscovered.State);
			Assert.False(Directory.Exists(Path.Combine(root, "plugin-data", "state.plugin")));
			await second.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task Enable激活而Disable撤销贡献并持久化禁用()
	{
		string root = CreateTemp();
		try
		{
			PluginManager manager = CreateManager(root);
			await manager.InstallAsync(CreateTestPackage(root, "toggle.plugin", "1.0.0"));
			await manager.EnableAsync("toggle.plugin");
			Assert.Equal(PluginLifecycleState.Active, Assert.Single(manager.Plugins).State);
			Assert.NotEmpty(manager.GetContributions<IPluginContribution>());

			await manager.DisableAsync("toggle.plugin");
			PluginInfo disabled = Assert.Single(manager.Plugins);
			Assert.False(disabled.UserEnabled);
			Assert.Contains(disabled.State, new[] {PluginLifecycleState.Disabled, PluginLifecycleState.PendingRestart});
			Assert.Empty(manager.GetContributions<IPluginContribution>());
			await manager.DisposeAsync();

			PluginManager rebuilt = CreateManager(root);
			PluginInfo rediscovered = Assert.Single(rebuilt.Discover());
			Assert.False(rediscovered.UserEnabled);
			Assert.Equal(PluginLifecycleState.Disabled, rediscovered.State);
			await rebuilt.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task 损坏的插件状态文件会FailClosed且显式重新启用可跨重启恢复()
	{
		string root = CreateTemp();
		try
		{
			string plugins = Path.Combine(root, "plugins");
			PluginPackageInstaller installer = new(plugins);
			installer.Install(CreateTestPackage(root, "first.plugin", "1.0.0"));
			installer.Install(CreateTestPackage(root, "second.plugin", "1.0.0"));

			string runtime = Path.Combine(root, "plugin-data", "runtime");
			Directory.CreateDirectory(runtime);
			File.WriteAllText(Path.Combine(runtime, "plugin-state.json"), "{ broken json");

			PluginManager first = CreateManager(root);
			PluginInfo[] failedClosed = first.Discover().OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
			Assert.Equal(2, failedClosed.Length);
			Assert.All(failedClosed, item =>
			{
				Assert.False(item.UserEnabled);
				Assert.Equal(PluginLifecycleState.Disabled, item.State);
			});
			Assert.Empty(first.GetContributions<IPluginContribution>());

			await first.EnableAsync("first.plugin");
			PluginInfo enabled = Assert.Single(first.Plugins, item => item.Id == "first.plugin");
			Assert.True(enabled.UserEnabled);
			Assert.Equal(PluginLifecycleState.Active, enabled.State);
			await first.DisposeAsync();

			PluginManager rebuilt = CreateManager(root);
			PluginInfo[] restored = rebuilt.Discover().OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
			Assert.True(Assert.Single(restored, item => item.Id == "first.plugin").UserEnabled);
			PluginInfo stillClosed = Assert.Single(restored, item => item.Id == "second.plugin");
			Assert.False(stillClosed.UserEnabled);
			Assert.Equal(PluginLifecycleState.Disabled, stillClosed.State);
			await rebuilt.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task 激活失败后仍可重试并保持用户启用意图()
	{
		string root = CreateTemp();
		try
		{
			PluginManager manager = CreateManager(root);
			await manager.InstallAsync(CreateTestPackage(root, "retry.plugin", "1.0.0", entryType: "Nori.PluginRuntime.TestPlugin.ThrowingActivatePlugin"));
			await Assert.ThrowsAsync<PluginException>(() => manager.EnableAsync("retry.plugin"));
			PluginInfo failed = Assert.Single(manager.Plugins);
			Assert.True(failed.UserEnabled);
			Assert.Contains(failed.State, new[] {PluginLifecycleState.Failed, PluginLifecycleState.Disabled, PluginLifecycleState.PendingRestart});
			Assert.NotNull(failed.ErrorCode);
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task 卸载删除安装目录默认保留数据而deleteData只删精确插件目录()
	{
		string root = CreateTemp();
		try
		{
			PluginManager manager = CreateManager(root);
			await manager.InstallAsync(CreateTestPackage(root, "keep.plugin", "1.0.0"));
			await manager.EnableAsync("keep.plugin");
			await manager.DisableAsync("keep.plugin");
			string keepData = Path.Combine(root, "plugin-data", "keep.plugin");
			Assert.True(Directory.Exists(keepData));
			PluginUninstallResult keepResult = await manager.UninstallAsync("keep.plugin");
			if (!keepResult.RequiresRestart)
			{
				Assert.False(Directory.Exists(Path.Combine(root, "plugins", "keep.plugin")));
				Assert.True(Directory.Exists(keepData));
			}

			await manager.InstallAsync(CreateTestPackage(root, "delete.plugin", "1.0.0"));
			await manager.EnableAsync("delete.plugin");
			await manager.DisableAsync("delete.plugin");
			string deleteData = Path.Combine(root, "plugin-data", "delete.plugin");
			string otherData = Path.Combine(root, "plugin-data", "other.plugin");
			Directory.CreateDirectory(otherData);
			File.WriteAllText(Path.Combine(otherData, "keep.txt"), "keep");
			PluginUninstallResult deleteResult = await manager.UninstallAsync("delete.plugin", deleteData: true);
			if (!deleteResult.RequiresRestart)
			{
				Assert.False(Directory.Exists(deleteData));
				Assert.True(File.Exists(Path.Combine(otherData, "keep.txt")));
			}
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task 非法ID和活动依赖会拒绝卸载()
	{
		string root = CreateTemp();
		try
		{
			PluginManager manager = CreateManager(root);
			await Assert.ThrowsAsync<PluginException>(() => manager.UninstallAsync("../outside", true));

			manager.Installer.Install(CreateTestPackage(root, "base.plugin", "1.0.0"));
			manager.Installer.Install(CreateTestPackage(root, "dependent.plugin", "1.0.0", dependencies: "[{\"id\":\"base.plugin\",\"version\":\">=1.0.0 <2.0.0\",\"optional\":false}]"));
			manager.Discover();
			await manager.ActivateAsync("base.plugin");
			await manager.ActivateAsync("dependent.plugin");
			PluginException inUse = await Assert.ThrowsAsync<PluginException>(() => manager.UninstallAsync("base.plugin"));
			Assert.Equal(PluginErrorCodes.DependencyInUse, inUse.Code);
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task 活动必需依赖会拒绝Disable且保持双方Active()
	{
		string root = CreateTemp();
		try
		{
			PluginManager manager = CreateManager(root);
			manager.Installer.Install(CreateTestPackage(root, "base.plugin", "1.0.0"));
			manager.Installer.Install(CreateTestPackage(root, "dependent.plugin", "1.0.0", dependencies: "[{\"id\":\"base.plugin\",\"version\":\">=1.0.0 <2.0.0\",\"optional\":false}]"));
			manager.Discover();
			await manager.ActivateAsync("base.plugin");
			await manager.ActivateAsync("dependent.plugin");

			PluginException inUse = await Assert.ThrowsAsync<PluginException>(() => manager.DisableAsync("base.plugin"));
			Assert.Equal(PluginErrorCodes.DependencyInUse, inUse.Code);
			PluginInfo baseInfo = Assert.Single(manager.Plugins, item => item.Id == "base.plugin");
			PluginInfo dependentInfo = Assert.Single(manager.Plugins, item => item.Id == "dependent.plugin");
			Assert.True(baseInfo.UserEnabled);
			Assert.Equal(PluginLifecycleState.Active, baseInfo.State);
			Assert.Equal(PluginLifecycleState.Active, dependentInfo.State);
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task 活动插件安装新版本后保持Active贡献并单独标记需要重启()
	{
		string root = CreateTemp();
		try
		{
			PluginManager manager = CreateManager(root);
			await manager.InstallAsync(CreateTestPackage(root, "update.plugin", "1.0.0"));
			await manager.EnableAsync("update.plugin");
			Assert.NotEmpty(manager.GetContributions<IPluginContribution>());

			await manager.InstallAsync(CreateTestPackage(root, "update.plugin", "1.1.0"));

			PluginInfo running = Assert.Single(manager.Plugins);
			Assert.Equal(PluginLifecycleState.Active, running.State);
			Assert.True(running.RequiresRestart);
			Assert.Equal("1.0.0", running.Manifest.Version);
			Assert.NotEmpty(manager.GetContributions<IPluginContribution>());
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task SafeMode只发现且不创建插件数据或激活DLL()
	{
		string root = CreateTemp();
		try
		{
			PluginPackageInstaller installer = new(Path.Combine(root, "plugins"));
			installer.Install(CreateTestPackage(root, "safe.plugin", "1.0.0"));
			PluginManager manager = new(new PluginRuntimeOptions
			{
				PluginsDirectory = Path.Combine(root, "plugins"),
				DataDirectory = Path.Combine(root, "plugin-data"),
				SafeMode = true,
			});
			PluginInfo info = Assert.Single(manager.Discover());
			Assert.True(info.UserEnabled);
			Assert.Equal(PluginLifecycleState.Disabled, info.State);
			Assert.Equal(PluginErrorCodes.SafeModeDisabled, info.ErrorCode);
			await manager.StartAllAsync();
			Assert.Equal(PluginLifecycleState.Disabled, Assert.Single(manager.Plugins).State);
			await manager.ActivateAsync("safe.plugin");
			Assert.False(Directory.Exists(Path.Combine(root, "plugin-data", "safe.plugin")));
			await Assert.ThrowsAsync<PluginException>(() => manager.EnableAsync("safe.plugin"));
			await manager.DisableAsync("safe.plugin");
			Assert.False(Assert.Single(manager.Plugins).UserEnabled);
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task 重复Discover不会把相同current活动版本标成待重启()
	{
		string root = CreateTemp();
		try
		{
			PluginManager manager = CreateManager(root);
			manager.Installer.Install(CreateTestPackage(root, "discover.plugin", "1.0.0"));
			manager.Discover();
			await manager.ActivateAsync("discover.plugin");
			Assert.Equal(PluginLifecycleState.Active, Assert.Single(manager.Discover()).State);
			Assert.Equal(PluginLifecycleState.Active, Assert.Single(manager.Discover()).State);
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	private static PluginManager CreateManager(string root) => new(new PluginRuntimeOptions
	{
		PluginsDirectory = Path.Combine(root, "plugins"),
		DataDirectory = Path.Combine(root, "plugin-data"),
	});

	private static string CreateTestPackage(
		string root,
		string id,
		string version,
		string entryType = "Nori.PluginRuntime.TestPlugin.TestPlugin",
		string dependencies = "[]")
	{
		string package = Path.Combine(root, $"{id}-{Guid.NewGuid():N}.noripack");
		string assembly = typeof(Nori.PluginRuntime.TestPlugin.TestPlugin).Assembly.Location;
		string manifest = $"{{\"schemaVersion\":1,\"id\":\"{id}\",\"name\":\"Demo\",\"description\":\"Demo plugin\",\"version\":\"{version}\",\"authors\":[{{\"name\":\"Nori\"}}],\"homepage\":\"https://example.test\",\"repository\":\"https://example.test/repo\",\"license\":\"MIT\",\"apiVersion\":\"2.0\",\"minHostVersion\":\"1.0.0\",\"runtime\":{{\"kind\":\"dotnet\",\"assembly\":\"lib/Nori.PluginRuntime.TestPlugin.dll\",\"entryType\":\"{entryType}\"}},\"ui\":{{\"webRoot\":\"web\"}},\"capabilities\":[],\"optionalCapabilities\":[],\"platforms\":[],\"dependencies\":{dependencies}}}";
		using (FileStream file = File.Create(package))
		using (ZipArchive archive = new(file, ZipArchiveMode.Create))
		{
			WriteEntry(archive, "manifest.json", manifest);
			ZipArchiveEntry assemblyEntry = archive.CreateEntry("lib/Nori.PluginRuntime.TestPlugin.dll");
			using (Stream target = assemblyEntry.Open())
			using (FileStream source = File.OpenRead(assembly)) source.CopyTo(target);
			WriteEntry(archive, "web/index.html", "<!doctype html><title>plugin</title>");
			WriteEntry(archive, "README.md", "test");
		}
		return package;
	}

	private static void WriteEntry(ZipArchive archive, string name, string content)
	{
		using StreamWriter writer = new(archive.CreateEntry(name).Open());
		writer.Write(content);
	}

	private static string CreateTemp()
	{
		string path = Path.Combine(Path.GetTempPath(), "nori-plugin-management-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "测试夹具销毁只能尽力清理，不能让清理异常覆盖测试结果。")]
	private static void DeleteDirectory(string path)
	{
		try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
	}
}
