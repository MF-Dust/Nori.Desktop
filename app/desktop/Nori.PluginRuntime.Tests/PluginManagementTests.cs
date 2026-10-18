using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
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
			await Task.Run(async () =>
			{
				PluginManager manager = CreateManager(root);
				await manager.InstallAsync(CreateTestPackage(root, "retry.plugin", "1.0.0", entryType: "Nori.PluginRuntime.TestPlugin.ThrowingActivatePlugin"));
				await Assert.ThrowsAsync<PluginException>(() => manager.EnableAsync("retry.plugin"));
				PluginInfo failed = Assert.Single(manager.Plugins);
				Assert.True(failed.UserEnabled);
				Assert.Contains(failed.State, new[] {PluginLifecycleState.Failed, PluginLifecycleState.Disabled, PluginLifecycleState.PendingRestart});
				Assert.NotNull(failed.ErrorCode);
				await manager.DisposeAsync();
			});
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task 卸载删除安装目录默认保留数据而deleteData只删精确插件目录()
	{
		string root = CreateTemp();
		PluginManager? manager = null;
		try
		{
			manager = CreateManager(root);
			await manager.InstallAsync(CreateTestPackage(root, "keep.plugin", "1.0.0"));
			await manager.EnableAsync("keep.plugin");
			await manager.DisableAsync("keep.plugin");
			string keepInstall = Path.Combine(root, "plugins", "keep.plugin");
			string keepData = Path.Combine(root, "plugin-data", "keep.plugin");
			Directory.CreateDirectory(keepData);
			string keepMarker = Path.Combine(keepData, "keep.txt");
			File.WriteAllText(keepMarker, "keep");
			PluginUninstallResult keepResult = await manager.UninstallAsync("keep.plugin");
			Assert.True(keepResult.Success);
			if (keepResult.RequiresRestart)
			{
				PluginInfo pending = Assert.IsType<PluginInfo>(keepResult.Plugin);
				Assert.Equal(PluginLifecycleState.PendingRestart, pending.State);
				Assert.Equal(PluginErrorCodes.UninstallPendingRestart, pending.ErrorCode);
				Assert.True(pending.RequiresRestart);
				Assert.True(Directory.Exists(keepInstall));
			}
			else
			{
				Assert.Null(keepResult.Plugin);
				Assert.False(Directory.Exists(keepInstall));
			}
			Assert.True(File.Exists(keepMarker));
			await manager.DisposeAsync();
			manager = null;
			manager = CreateManager(root);
			Assert.Empty(manager.Discover());
			Assert.False(Directory.Exists(keepInstall));
			Assert.True(File.Exists(keepMarker));

			await manager.InstallAsync(CreateTestPackage(root, "delete.plugin", "1.0.0"));
			await manager.EnableAsync("delete.plugin");
			await manager.DisableAsync("delete.plugin");
			string deleteInstall = Path.Combine(root, "plugins", "delete.plugin");
			string deleteData = Path.Combine(root, "plugin-data", "delete.plugin");
			string otherData = Path.Combine(root, "plugin-data", "other.plugin");
			Directory.CreateDirectory(deleteData);
			Directory.CreateDirectory(otherData);
			File.WriteAllText(Path.Combine(deleteData, "remove.txt"), "remove");
			string otherMarker = Path.Combine(otherData, "keep.txt");
			File.WriteAllText(otherMarker, "keep");
			PluginUninstallResult deleteResult = await manager.UninstallAsync("delete.plugin", deleteData: true);
			Assert.True(deleteResult.Success);
			if (deleteResult.RequiresRestart)
			{
				PluginInfo pending = Assert.IsType<PluginInfo>(deleteResult.Plugin);
				Assert.Equal(PluginLifecycleState.PendingRestart, pending.State);
				Assert.Equal(PluginErrorCodes.UninstallPendingRestart, pending.ErrorCode);
				Assert.True(pending.RequiresRestart);
				Assert.True(Directory.Exists(deleteInstall));
				Assert.True(Directory.Exists(deleteData));
			}
			else
			{
				Assert.Null(deleteResult.Plugin);
				Assert.False(Directory.Exists(deleteInstall));
				Assert.False(Directory.Exists(deleteData));
			}
			Assert.True(File.Exists(otherMarker));
			await manager.DisposeAsync();
			manager = null;
			manager = CreateManager(root);
			Assert.Empty(manager.Discover());
			Assert.False(Directory.Exists(deleteInstall));
			Assert.False(Directory.Exists(deleteData));
			Assert.True(File.Exists(otherMarker));
		}
		finally
		{
			if (manager is not null) await manager.DisposeAsync();
			DeleteDirectory(root);
		}
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
	public async Task 活动必需依赖会拒绝Disable和Reload且保持双方Active()
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
			PluginException reloadInUse = await Assert.ThrowsAsync<PluginException>(() => manager.ReloadAsync("base.plugin"));
			Assert.Equal(PluginErrorCodes.DependencyInUse, reloadInUse.Code);
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

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Reload撤销旧贡献并激活current版本且保留数据和启用意图(bool update)
	{
		string root = CreateTemp();
		try
		{
			await Task.Run(async () =>
			{
				await using PluginManager manager = CreateManager(root);
				await manager.InstallAsync(CreateTestPackage(root, "reload.plugin", "1.0.0"));
				await manager.EnableAsync("reload.plugin");
				WeakReference previous = ContributionReference(manager);
				string data = Path.Combine(root, "plugin-data", "reload.plugin");
				await new JsonPluginStorage(data).SetAsync("keep", JsonValue.Create("saved"));
				string intent = File.ReadAllText(Path.Combine(root, "plugin-data", "runtime", "plugin-state.json"));
				List<PluginLifecycleState> transitions = [];
				manager.ActivePluginsChanged += () => transitions.Add(Assert.Single(manager.Plugins).State);
				if (update) await manager.InstallAsync(CreateTestPackage(root, "reload.plugin", "1.1.0"));

				await manager.ReloadAsync("reload.plugin");

				PluginInfo info = Assert.Single(manager.Plugins);
				Assert.Equal(PluginLifecycleState.Active, info.State);
				Assert.Equal(update ? "1.1.0" : "1.0.0", info.Manifest.Version);
				Assert.True(info.UserEnabled);
				Assert.False(info.RequiresRestart);
				Assert.Null(info.ErrorCode);
				Assert.False(previous.IsAlive);
				Assert.Single(manager.GetContributions<IPluginContribution>());
				Assert.Equal(new[] {PluginLifecycleState.Installed, PluginLifecycleState.Active}, transitions);
				Assert.Equal("saved", (await new JsonPluginStorage(data).GetAsync("keep"))!.GetValue<string>());
				Assert.Equal(intent, File.ReadAllText(Path.Combine(root, "plugin-data", "runtime", "plugin-state.json")));
			});
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task Reload可从激活失败恢复到新版本而不修改启用意图()
	{
		string root = CreateTemp();
		try
		{
			await Task.Run(async () =>
			{
				await using PluginManager manager = CreateManager(root);
				await manager.InstallAsync(CreateTestPackage(root, "retry.plugin", "1.0.0", entryType: "Nori.PluginRuntime.TestPlugin.ThrowingActivatePlugin"));
				await FailEnableAsync(manager, "retry.plugin");
				Assert.Equal(PluginLifecycleState.Failed, Assert.Single(manager.Plugins).State);
				await manager.InstallAsync(CreateTestPackage(root, "retry.plugin", "1.1.0"));

				await manager.ReloadAsync("retry.plugin");

				PluginInfo info = Assert.Single(manager.Plugins);
				Assert.Equal(PluginLifecycleState.Active, info.State);
				Assert.Equal("1.1.0", info.Manifest.Version);
				Assert.True(info.UserEnabled);
				Assert.False(info.RequiresRestart);
				Assert.Single(manager.GetContributions<IPluginContribution>());
			});
		}
		finally { DeleteDirectory(root); }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Reload无法回收旧版本时返回待重启且不加载第二份(bool failedActivation)
	{
		string root = CreateTemp();
		try
		{
			await Task.Run(async () =>
			{
				int activations = 0;
				CancellationToken stopping = default;
				await using PluginManager manager = new(new PluginRuntimeOptions
				{
					PluginsDirectory = Path.Combine(root, "plugins"),
					DataDirectory = Path.Combine(root, "plugin-data"),
					OnLog = (_, _, _) => activations++,
					CapabilityFactory = (_, token) => { stopping = token; return []; },
				});
				string entryType = failedActivation ? "Nori.PluginRuntime.TestPlugin.ThrowingActivatePlugin" : "Nori.PluginRuntime.TestPlugin.TestPlugin";
				await manager.InstallAsync(CreateTestPackage(root, "pinned.plugin", "1.0.0", entryType: entryType));
				object retained;
				if (failedActivation)
					retained = await Assert.ThrowsAsync<PluginException>(() => manager.EnableAsync("pinned.plugin"));
				else
				{
					await manager.EnableAsync("pinned.plugin");
					retained = Assert.Single(manager.GetContributions<IPluginContribution>());
				}
				await manager.InstallAsync(CreateTestPackage(root, "pinned.plugin", "1.1.0"));

				await manager.ReloadAsync("pinned.plugin");

				PluginInfo info = Assert.Single(manager.Discover());
				Assert.Equal(PluginLifecycleState.PendingRestart, info.State);
				Assert.Equal(PluginErrorCodes.UnloadPendingRestart, info.ErrorCode);
				Assert.Equal("1.0.0", info.Manifest.Version);
				Assert.True(info.UserEnabled);
				Assert.True(info.RequiresRestart);
				Assert.True(stopping.IsCancellationRequested);
				Assert.Empty(manager.GetContributions<IPluginContribution>());
				Assert.Equal(failedActivation ? 0 : 1, activations);
				PluginException pending = await Assert.ThrowsAsync<PluginException>(() => manager.ReloadAsync("pinned.plugin"));
				Assert.Equal(PluginErrorCodes.UnloadPendingRestart, pending.Code);
				GC.KeepAlive(retained);
			});
		}
		finally { DeleteDirectory(root); }
	}

	[Theory]
	[InlineData("../outside", PluginErrorCodes.InvalidPluginId)]
	[InlineData("missing.plugin", PluginErrorCodes.PluginNotFound)]
	[InlineData("disabled.plugin", PluginErrorCodes.UserDisabled)]
	public async Task Reload拒绝非法缺失或用户禁用插件且不改变意图(string id, string code)
	{
		string root = CreateTemp();
		try
		{
			await using PluginManager manager = CreateManager(root);
			await manager.InstallAsync(CreateTestPackage(root, "disabled.plugin", "1.0.0"));
			PluginException failure = await Assert.ThrowsAsync<PluginException>(() => manager.ReloadAsync(id));
			Assert.Equal(code, failure.Code);
			PluginInfo info = Assert.Single(manager.Plugins);
			Assert.False(info.UserEnabled);
			Assert.Equal(PluginLifecycleState.Disabled, info.State);
			Assert.False(Directory.Exists(Path.Combine(root, "plugin-data", "disabled.plugin")));
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task Reload不能绕过启动失败保护()
	{
		string root = CreateTemp();
		try
		{
			await Task.Run(async () =>
			{
				await using PluginManager manager = CreateManager(root);
				await manager.InstallAsync(CreateTestPackage(root, "recovery.plugin", "1.0.0", entryType: "Nori.PluginRuntime.TestPlugin.ThrowingActivatePlugin"));
				await FailEnableAsync(manager, "recovery.plugin");
				await Assert.ThrowsAsync<PluginException>(() => manager.ActivateAsync("recovery.plugin"));
				PluginException failure = await Assert.ThrowsAsync<PluginException>(() => manager.ReloadAsync("recovery.plugin"));
				Assert.Equal(PluginErrorCodes.StartupRecoveryDisabled, failure.Code);
				PluginInfo info = Assert.Single(manager.Plugins);
				Assert.True(info.UserEnabled);
				Assert.Equal(PluginLifecycleState.Disabled, info.State);
			});
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task Reload拒绝待重启卸载且不删除安装或用户数据()
	{
		string root = CreateTemp();
		try
		{
			await Task.Run(async () =>
			{
				await using PluginManager manager = CreateManager(root);
				await manager.InstallAsync(CreateTestPackage(root, "uninstall.plugin", "1.0.0"));
				await manager.EnableAsync("uninstall.plugin");
				IPluginContribution retained = Assert.Single(manager.GetContributions<IPluginContribution>());
				Assert.True((await manager.UninstallAsync("uninstall.plugin", deleteData: true)).RequiresRestart);
				PluginException failure = await Assert.ThrowsAsync<PluginException>(() => manager.ReloadAsync("uninstall.plugin"));
				Assert.Equal(PluginErrorCodes.UninstallPendingRestart, failure.Code);
				Assert.True(Directory.Exists(Path.Combine(root, "plugins", "uninstall.plugin")));
				Assert.True(File.Exists(Path.Combine(root, "plugin-data", "uninstall.plugin", "storage.json")));
				Assert.False(Assert.Single(manager.Plugins).UserEnabled);
				GC.KeepAlive(retained);
			});
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task Reload拒绝丢失current的活动插件且不停止旧实例()
	{
		string root = CreateTemp();
		try
		{
			await Task.Run(async () =>
			{
				await using PluginManager manager = CreateManager(root);
				await manager.InstallAsync(CreateTestPackage(root, "missing.plugin", "1.0.0"));
				await manager.EnableAsync("missing.plugin");
				File.Delete(Path.Combine(root, "plugins", "missing.plugin", PluginPackageInstaller.CurrentFileName));
				PluginException failure = await Assert.ThrowsAsync<PluginException>(() => manager.ReloadAsync("missing.plugin"));
				Assert.Equal(PluginErrorCodes.PluginNotFound, failure.Code);
				Assert.Equal(PluginLifecycleState.Active, Assert.Single(manager.Plugins).State);
				Assert.Single(manager.GetContributions<IPluginContribution>());
			});
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task Enable重试激活失败插件时加载已安装的新版本()
	{
		string root = CreateTemp();
		try
		{
			await Task.Run(async () =>
			{
				await using PluginManager manager = CreateManager(root);
				await manager.InstallAsync(CreateTestPackage(root, "retry.enable", "1.0.0", entryType: "Nori.PluginRuntime.TestPlugin.ThrowingActivatePlugin"));
				await FailEnableAsync(manager, "retry.enable");
				await manager.InstallAsync(CreateTestPackage(root, "retry.enable", "1.1.0"));

				await manager.EnableAsync("retry.enable");

				PluginInfo info = Assert.Single(manager.Plugins);
				Assert.Equal(PluginLifecycleState.Active, info.State);
				Assert.Equal("1.1.0", info.Manifest.Version);
				Assert.Single(manager.GetContributions<IPluginContribution>());
			});
		}
		finally { DeleteDirectory(root); }
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static WeakReference ContributionReference(PluginManager manager) => new(Assert.Single(manager.GetContributions<IPluginContribution>()));

	private static async Task FailEnableAsync(PluginManager manager, string id)
	{
		PluginException failure = await Assert.ThrowsAsync<PluginException>(() => manager.EnableAsync(id));
		Assert.Equal(PluginErrorCodes.ActivationFailed, failure.Code);
	}

	[Fact]
	public void 指针写入失败不会留下阻塞同版本安装的孤儿目录()
	{
		string root = CreateTemp();
		try
		{
			PluginPackageInstaller installer = new(Path.Combine(root, "plugins"));
			string pluginDirectory = Path.Combine(root, "plugins", "orphan.plugin");
			Directory.CreateDirectory(pluginDirectory);
			string pointerDirectory = Path.Combine(pluginDirectory, PluginPackageInstaller.CurrentFileName);
			Directory.CreateDirectory(pointerDirectory);
			string package = CreateTestPackage(root, "orphan.plugin", "1.0.0");

			Assert.Throws<PluginException>(() => installer.Install(package));
			Assert.False(Directory.Exists(Path.Combine(pluginDirectory, "1.0.0")));

			Directory.Delete(pointerDirectory);
			PluginManifest installed = installer.Install(package);
			Assert.Equal("orphan.plugin", installed.Id);
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
			PluginException reload = await Assert.ThrowsAsync<PluginException>(() => manager.ReloadAsync("safe.plugin"));
			Assert.Equal(PluginErrorCodes.SafeModeDisabled, reload.Code);
			Assert.True(Assert.Single(manager.Plugins).UserEnabled);
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
			PluginTestPackages.WriteEntry(archive, "manifest.json", manifest);
			PluginTestPackages.WriteAssemblyEntry(archive, "lib/Nori.PluginRuntime.TestPlugin.dll", assembly);
			PluginTestPackages.WriteEntry(archive, "web/index.html", "<!doctype html><title>plugin</title>");
			PluginTestPackages.WriteEntry(archive, "README.md", "test");
		}
		return package;
	}

	private static string CreateTemp() => PluginTestPackages.CreateTemp("nori-plugin-management-tests");

	private static void DeleteDirectory(string path) => PluginTestPackages.DeleteDirectory(path);
}
