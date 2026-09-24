using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.PluginRuntime;
using Nori.PluginRuntime.TestPlugin;

namespace Nori.PluginRuntime.Tests;

public sealed class PluginRuntimeTests
{
	[Fact]
	public void Manifest正常解析且独立处理三个版本概念()
	{
		PluginManifest manifest = PluginManifestReader.ReadJson(CreateManifest("demo.plugin", "1.2.3", "1.2"));

		Assert.Equal(1, manifest.SchemaVersion);
		Assert.Equal("demo.plugin", manifest.Id);
		Assert.Equal("1.2.3", manifest.Version);
		Assert.Equal(new PluginApiVersion(1, 2), manifest.Api);
		Assert.Equal("Nori", Assert.Single(manifest.Authors).Name);
		Assert.Equal("lib/Nori.PluginRuntime.TestPlugin.dll", manifest.Runtime.Assembly);
		Assert.True(PluginManifestReader.IsCompatible(new PluginApiVersion(1, 2), manifest.Api));
		Assert.True(PluginManifestReader.IsCompatible(new PluginApiVersion(1, 5), manifest.Api));
		Assert.False(PluginManifestReader.IsCompatible(new PluginApiVersion(2, 0), manifest.Api));
	}

	[Theory]
	[InlineData("demo")]
	[InlineData("Demo.plugin")]
	[InlineData("demo..plugin")]
	[InlineData("demo/plugin")]
	public void Manifest非法ID返回稳定错误(string id)
	{
		PluginException exception = Assert.Throws<PluginException>(() => PluginManifestReader.ReadJson(CreateManifest(id, "1.0.0", "1.0")));
		Assert.Equal(PluginErrorCodes.InvalidManifest, exception.Code);
	}

	[Fact]
	public async Task 旧版插件API只展示不加载()
	{
		string root = CreateTemp();
		try
		{
			PluginManager manager = CreateManager(root, CreateTestPackage(root, "legacy.plugin", "1.0.0", apiVersion: "1.0"));
			PluginInfo info = Assert.Single(manager.Plugins);
			Assert.Equal(PluginLifecycleState.Incompatible, info.State);
			Assert.Equal(PluginErrorCodes.IncompatibleApi, info.ErrorCode);
			await manager.StartAllAsync();
			Assert.False(Directory.Exists(Path.Combine(root, "plugin-data", "legacy.plugin")));
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public void Manifest未知schema返回稳定错误()
	{
		string json = CreateManifest("demo.plugin", "1.0.0", "1.0").Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal);
		PluginException exception = Assert.Throws<PluginException>(() => PluginManifestReader.ReadJson(json));
		Assert.Equal(PluginErrorCodes.UnknownSchema, exception.Code);
	}

	[Fact]
	public void Manifest重复属性不会被静默覆盖()
	{
		PluginException exception = Assert.Throws<PluginException>(() => PluginManifestReader.ReadJson("""
			{"schemaVersion":1,"id":"demo.plugin","id":"other.plugin"}
			"""));
		Assert.Equal(PluginErrorCodes.DuplicateManifestProperty, exception.Code);
	}

	[Theory]
	[InlineData("1.0", "1.2", false)]
	[InlineData("1.1", "1.2", false)]
	[InlineData("1.2", "1.2", true)]
	[InlineData("1.5", "1.2", true)]
	[InlineData("2.0", "1.2", false)]
	public void Api版本遵循major相等且hostMinor不低于插件(string host, string plugin, bool expected)
	{
		Assert.Equal(expected, PluginManifestReader.IsCompatible(PluginApiVersion.Parse(host), PluginApiVersion.Parse(plugin)));
	}

	[Fact]
	public async Task JSONStorage按插件目录隔离并深拷贝()
	{
		string root = CreateTemp();
		try
		{
			JsonPluginStorage first = new(Path.Combine(root, "plugin-data", "first.plugin"));
			JsonPluginStorage second = new(Path.Combine(root, "plugin-data", "second.plugin"));
			JsonObject value = new() { ["answer"] = 42 };
			await first.SetAsync("state", value);
			value["answer"] = 99;

			Assert.Equal(42, (await first.GetAsync("state"))!["answer"]!.GetValue<int>());
			Assert.Null(await second.GetAsync("state"));
			await first.DeleteAsync("state");
			Assert.Null(await first.GetAsync("state"));
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public void PluginAssets拒绝路径穿越和私有文件()
	{
		string root = CreateTemp();
		try
		{
			Directory.CreateDirectory(Path.Combine(root, "web"));
			File.WriteAllText(Path.Combine(root, "web", "index.html"), "ok");
			File.WriteAllText(Path.Combine(root, "manifest.json"), "private");
			IPluginAssets assets = new PluginAssetProvider(root, _ => new Uri("https://example.test/plugin"));

			Assert.Equal("ok", new StreamReader(assets.OpenRead("web/index.html")).ReadToEnd());
			Assert.Equal("https://example.test/plugin", assets.GetUri("web/index.html").ToString());
			Assert.Throws<PluginException>(() => assets.OpenRead("../manifest.json"));
			Assert.Throws<PluginException>(() => assets.OpenRead("manifest.json"));
			Assert.Throws<PluginException>(() => assets.OpenRead("web/../manifest.json"));
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public void 重复Contribution注册会返回稳定错误()
	{
		PluginContributionRegistry registry = new();
		TestContribution contribution = new();
		registry.Register(contribution);
		PluginException exception = Assert.Throws<PluginException>(() => registry.Register(contribution));
		Assert.Equal(PluginErrorCodes.DuplicateContribution, exception.Code);
	}

	[Fact]
	public void PluginCapabilities区分缺失和不可用()
	{
		PluginCapabilityRegistry registry = new(
			[PluginCapabilityIds.WebView],
			[PluginCapabilityIds.WebView],
			[]);

		PluginCapabilityStatus web = Assert.Single(registry.Statuses, status => status.Id == PluginCapabilityIds.WebView);
		Assert.True(web.Declared);
		Assert.True(web.Granted);
		Assert.False(web.Available);
		Assert.False(registry.TryGet<IWebViewCapability>(out _));
		PluginException unavailable = Assert.Throws<PluginException>(() => registry.GetRequired<IWebViewCapability>());
		Assert.Equal(PluginErrorCodes.CapabilityUnavailable, unavailable.Code);
		PluginException missing = Assert.Throws<PluginException>(() => registry.GetRequired<TestCapability>());
		Assert.Equal(PluginErrorCodes.CapabilityMissing, missing.Code);
		PluginCapabilityRegistry notGranted = new([PluginCapabilityIds.WebView], [], []);
		PluginException denied = Assert.Throws<PluginException>(() => notGranted.GetRequired<IWebViewCapability>());
		Assert.Equal(PluginErrorCodes.CapabilityNotGranted, denied.Code);
	}

	[Fact]
	public void 单一Runtime程序集不允许被插件包携带且测试插件只引用它()
	{
		string[] forbidden = ["Nori.Core", "Nori.Desktop", "Avalonia"];
		Assert.Equal("Nori.PluginRuntime", typeof(INoriPlugin).Assembly.GetName().Name);
		AssemblyName[] references = typeof(Nori.PluginRuntime.TestPlugin.TestPlugin).Assembly.GetReferencedAssemblies();
		Assert.Contains(references, reference => reference.Name == "Nori.PluginRuntime");
		Assert.DoesNotContain(references, reference => forbidden.Contains(reference.Name, StringComparer.OrdinalIgnoreCase));

		string root = CreateTemp();
		try
		{
			string package = CreateTestPackage(root, "contract.plugin", "1.0.0", includeContractAssembly: true);
			PluginException exception = Assert.Throws<PluginException>(() => new PluginPackageInstaller(Path.Combine(root, "plugins")).InspectPackage(package));
			Assert.Equal(PluginErrorCodes.ContractAssemblyDenied, exception.Code);
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public void ZIPSlip在manifest读取前被拒绝()
	{
		string root = CreateTemp();
		try
		{
			string package = Path.Combine(root, "bad.noripack");
			using (FileStream file = File.Create(package))
			using (ZipArchive archive = new(file, ZipArchiveMode.Create))
			{
				using StreamWriter writer = new(archive.CreateEntry("../escape.txt").Open());
				writer.Write("escape");
			}
			PluginException exception = Assert.Throws<PluginException>(() => new PluginPackageInstaller(Path.Combine(root, "plugins")).InspectPackage(package));
			Assert.Equal(PluginErrorCodes.PackagePathDenied, exception.Code);
			Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task InboxNoripack会被发现并安装()
	{
		string root = CreateTemp();
		try
		{
			string plugins = Path.Combine(root, "plugins");
			PluginManager manager = new(new PluginRuntimeOptions
			{
				PluginsDirectory = plugins,
				DataDirectory = Path.Combine(root, "plugin-data"),
			});
			string package = CreateTestPackage(root, "inbox.plugin", "1.0.0");
			string inboxPackage = Path.Combine(plugins, "inbox", "inbox.plugin.noripack");
			File.Move(package, inboxPackage);
			PluginInfo info = Assert.Single(manager.Discover());
			Assert.Equal("inbox.plugin", info.Id);
			Assert.True(File.Exists(Path.Combine(plugins, "inbox.plugin", "current.json")));
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task Noripack安装激活停用会撤销贡献但保留Storage()
	{
		string root = CreateTemp();
		try
		{
			string package = CreateTestPackage(root, "test.plugin", "1.0.0");
			string plugins = Path.Combine(root, "plugins");
			PluginManager manager = new(new PluginRuntimeOptions
			{
				PluginsDirectory = plugins,
				DataDirectory = Path.Combine(root, "plugin-data"),
				KnownCapabilityIds = [],
			});

			manager.Installer.Install(package);
			manager.Discover();
			await manager.StartAllAsync();
			PluginInfo info = Assert.Single(manager.Plugins);
			Assert.Equal(PluginLifecycleState.Active, info.State);
			Assert.Single(manager.GetContributions<IPluginContribution>());
			Assert.True(File.Exists(Path.Combine(plugins, "test.plugin", "current.json")));
			Assert.True(File.Exists(Path.Combine(root, "plugin-data", "test.plugin", "storage.json")));

			await manager.DeactivateAsync("test.plugin");
			Assert.Empty(manager.GetContributions<IPluginContribution>());
			PluginInfo deactivated = Assert.Single(manager.Plugins);
			Assert.Contains(deactivated.State, new[] { PluginLifecycleState.Installed, PluginLifecycleState.PendingRestart });
			Assert.True(File.Exists(Path.Combine(root, "plugin-data", "test.plugin", "storage.json")));
			if (deactivated.State == PluginLifecycleState.Installed)
			{
				await manager.ActivateAsync("test.plugin");
				await manager.DeactivateAsync("test.plugin");
				Assert.Contains(Assert.Single(manager.Plugins).State, new[] { PluginLifecycleState.Installed, PluginLifecycleState.PendingRestart });
			}
			else
			{
				PluginException pending = await Assert.ThrowsAsync<PluginException>(() => manager.ActivateAsync("test.plugin"));
				Assert.Equal(PluginErrorCodes.UnloadPendingRestart, pending.Code);
			}
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task 安全模式只发现并禁用插件不创建ALC()
	{
		string root = CreateTemp();
		try
		{
			string package = CreateTestPackage(root, "safe.plugin", "1.0.0");
			string plugins = Path.Combine(root, "plugins");
			new PluginPackageInstaller(plugins).Install(package);
			PluginManager manager = new(new PluginRuntimeOptions
			{
				PluginsDirectory = plugins,
				DataDirectory = Path.Combine(root, "plugin-data"),
				SafeMode = true,
				KnownCapabilityIds = [],
			});

			PluginInfo info = Assert.Single(manager.Discover());
			Assert.Equal(PluginLifecycleState.Disabled, info.State);
			await manager.StartAllAsync();
			Assert.Equal(PluginLifecycleState.Disabled, Assert.Single(manager.Plugins).State);
			Assert.False(File.Exists(Path.Combine(root, "plugin-data", "safe.plugin", "storage.json")));
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public void Contract程序集由DefaultALC提供()
	{
		string root = CreateTemp();
		try
		{
			string directory = Path.Combine(root, "1.0.0", "lib");
			Directory.CreateDirectory(directory);
			string path = Path.Combine(directory, "Nori.PluginRuntime.TestPlugin.dll");
			File.Copy(typeof(Nori.PluginRuntime.TestPlugin.TestPlugin).Assembly.Location, path);
			PluginLoadContext loadContext = new(path);
			Assembly assembly = loadContext.LoadFromAssemblyPath(path);
			Type type = assembly.GetType("Nori.PluginRuntime.TestPlugin.TestPlugin", throwOnError: true)!;
			Assert.True(typeof(INoriPlugin).IsAssignableFrom(type));
			Assert.Contains(type.GetInterfaces(), item => ReferenceEquals(item.Assembly, typeof(INoriPlugin).Assembly));
			loadContext.Unload();
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

	[Fact]
	public void 依赖范围支持AND比较()
	{
		Assert.True(PluginRange.Satisfies(PluginVersion.Parse("1.5.0"), ">=1.0.0 <2.0.0"));
		Assert.False(PluginRange.Satisfies(PluginVersion.Parse("2.0.0"), ">=1.0.0 <2.0.0"));
		Assert.False(PluginRange.TryParse("^1.0.0", out _));
	}

	[Fact]
	public async Task Activate异常被包装且不会留下贡献()
	{
		string root = CreateTemp();
		try
		{
			PluginManager manager = CreateManager(root, CreateTestPackage(root, "throws.plugin", "1.0.0", entryType: "Nori.PluginRuntime.TestPlugin.ThrowingActivatePlugin"));
			await manager.StartAllAsync();
			PluginInfo info = Assert.Single(manager.Plugins);
			Assert.Equal(PluginLifecycleState.Failed, info.State);
			Assert.Equal(PluginErrorCodes.ActivationFailed, info.ErrorCode);
			Assert.Empty(manager.GetContributions<IPluginContribution>());
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task 连续启动失败后插件会持久化禁用()
	{
		string root = CreateTemp();
		try
		{
			string package = CreateTestPackage(root, "recover.plugin", "1.0.0", entryType: "Nori.PluginRuntime.TestPlugin.ThrowingActivatePlugin");
			PluginManager first = CreateManager(root, package);
			await first.StartAllAsync();
			await first.DisposeAsync();
			PluginManager second = new(new PluginRuntimeOptions
			{
				PluginsDirectory = Path.Combine(root, "plugins"),
				DataDirectory = Path.Combine(root, "plugin-data"),
			});
			second.Discover();
			await second.StartAllAsync();
			await second.DisposeAsync();
			PluginManager third = new(new PluginRuntimeOptions
			{
				PluginsDirectory = Path.Combine(root, "plugins"),
				DataDirectory = Path.Combine(root, "plugin-data"),
			});
			Assert.Equal(PluginLifecycleState.Disabled, Assert.Single(third.Discover()).State);
			await third.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task Deactivate异常仍会撤销贡献并返回稳定错误()
	{
		string root = CreateTemp();
		try
		{
			PluginManager manager = CreateManager(root, CreateTestPackage(root, "stop.plugin", "1.0.0", entryType: "Nori.PluginRuntime.TestPlugin.ThrowingDeactivatePlugin"));
			await manager.StartAllAsync();
			PluginException exception = await Assert.ThrowsAsync<PluginException>(() => manager.DeactivateAsync("stop.plugin"));
			Assert.Equal(PluginErrorCodes.DeactivationFailed, exception.Code);
			Assert.Contains(Assert.Single(manager.Plugins).State, new[] { PluginLifecycleState.Failed, PluginLifecycleState.PendingRestart });
			Assert.Empty(manager.GetContributions<IPluginContribution>());
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task 入口类型错误和能力不可用不会创建活动插件()
	{
		string root = CreateTemp();
		try
		{
			PluginManager wrongEntry = CreateManager(root, CreateTestPackage(root, "wrong.plugin", "1.0.0", entryType: "Nori.PluginRuntime.TestPlugin.NotAPlugin"));
			await wrongEntry.StartAllAsync();
			Assert.Equal(PluginErrorCodes.EntryTypeNotFound, Assert.Single(wrongEntry.Plugins).ErrorCode);
			await wrongEntry.DisposeAsync();

			string secondRoot = CreateTemp();
			try
			{
				PluginManager unavailable = CreateManager(secondRoot, CreateTestPackage(secondRoot, "webview.plugin", "1.0.0", "[\"ui.webview\"]"));
				await unavailable.StartAllAsync();
				Assert.Equal(PluginErrorCodes.CapabilityUnavailable, Assert.Single(unavailable.Plugins).ErrorCode);
				await unavailable.DisposeAsync();
			}
			finally { DeleteDirectory(secondRoot); }
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task 可选能力不可用不会阻断插件()
	{
		string root = CreateTemp();
		try
		{
			string package = CreateTestPackage(root, "optional.plugin", "1.0.0", optionalCapabilities: "[\"arcade\"]");
			// optional 字段通过直接写包清单验证仍保留在 manifest 模型中。
			PluginManager manager = CreateManager(root, package);
			await manager.StartAllAsync();
			Assert.Equal(PluginLifecycleState.Active, Assert.Single(manager.Plugins).State);
			await manager.DisposeAsync();
		}
		finally { DeleteDirectory(root); }
	}

	private static PluginManager CreateManager(string root, string package)
	{
		PluginManager manager = new(new PluginRuntimeOptions
		{
			PluginsDirectory = Path.Combine(root, "plugins"),
			DataDirectory = Path.Combine(root, "plugin-data"),
		});
		manager.Installer.Install(package);
		manager.Discover();
		return manager;
	}

	[PluginCapability("test.available")]
	private sealed class TestCapability : IPluginCapability
	{
	}

	private static string CreateManifest(string id, string version, string apiVersion, string? capabilities = null, string entryType = "Nori.PluginRuntime.TestPlugin.TestPlugin", string minHostVersion = "1.0.0", string? optionalCapabilities = null)
	{
		string capabilityJson = capabilities ?? "[]";
		string optionalCapabilityJson = optionalCapabilities ?? "[]";
		return $"{{\"schemaVersion\":1,\"id\":\"{id}\",\"name\":\"Demo\",\"description\":\"Demo plugin\",\"version\":\"{version}\",\"authors\":[{{\"name\":\"Nori\"}}],\"homepage\":\"https://example.test\",\"repository\":\"https://example.test/repo\",\"license\":\"MIT\",\"apiVersion\":\"{apiVersion}\",\"minHostVersion\":\"{minHostVersion}\",\"runtime\":{{\"kind\":\"dotnet\",\"assembly\":\"lib/Nori.PluginRuntime.TestPlugin.dll\",\"entryType\":\"{entryType}\"}},\"ui\":{{\"webRoot\":\"web\"}},\"capabilities\":{capabilityJson},\"optionalCapabilities\":{optionalCapabilityJson},\"platforms\":[],\"dependencies\":[]}}";
	}

	private static string CreateTestPackage(string root, string id, string version, string? capabilities = null, string entryType = "Nori.PluginRuntime.TestPlugin.TestPlugin", string? optionalCapabilities = null, bool includeContractAssembly = false, string apiVersion = "2.0")
	{
		string package = Path.Combine(root, $"{id}.noripack");
		string assembly = typeof(Nori.PluginRuntime.TestPlugin.TestPlugin).Assembly.Location;
		using (FileStream file = File.Create(package))
		using (ZipArchive archive = new(file, ZipArchiveMode.Create))
		{
			WriteEntry(archive, "manifest.json", CreateManifest(id, version, apiVersion, capabilities, entryType, optionalCapabilities: optionalCapabilities));
			ZipArchiveEntry assemblyEntry = archive.CreateEntry("lib/Nori.PluginRuntime.TestPlugin.dll");
			using (Stream target = assemblyEntry.Open())
			using (FileStream source = File.OpenRead(assembly))
			{
				source.CopyTo(target);
			}
			WriteEntry(archive, "web/index.html", "<!doctype html><title>plugin</title>");
			WriteEntry(archive, "README.md", "test");
			if (includeContractAssembly)
			{
				ZipArchiveEntry contractEntry = archive.CreateEntry("lib/Nori.PluginRuntime.dll");
				using Stream target = contractEntry.Open();
				using FileStream source = File.OpenRead(typeof(INoriPlugin).Assembly.Location);
				source.CopyTo(target);
			}
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
		string path = Path.Combine(Path.GetTempPath(), "nori-plugin-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "测试夹具销毁只能尽力清理，不能让清理异常覆盖测试结果。")]
	private static void DeleteDirectory(string path)
	{
		try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
	}
}
