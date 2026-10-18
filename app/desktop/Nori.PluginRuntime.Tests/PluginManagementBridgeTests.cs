using System.Text.Json;
using Nori.PluginRuntime;

namespace Nori.PluginRuntime.Tests;

public sealed class PluginManagementBridgeTests : IDisposable
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly string _root = Directory.CreateTempSubdirectory("nori-plugin-bridge-tests-").FullName;

	private sealed class FakePicker(string? result) : IPluginPackagePicker
	{
		public int Calls { get; private set; }

		public Task<string?> PickAsync(Avalonia.Controls.Window? owner, CancellationToken cancellationToken = default)
		{
			Calls++;
			return Task.FromResult(result);
		}
	}

	[Fact]
	public async Task 六个管理命令都拒绝非main和不可见来源()
	{
		FakePicker picker = new(null);
		await using PluginRuntimeHost runtime = CreateHost(picker: picker);
		string[] names = ["plugin_list", "plugin_install_local", "plugin_enable", "plugin_reload", "plugin_disable", "plugin_uninstall"];
		foreach (string name in names)
		{
			await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runtime.InvokeManagementAsync(new PluginManagementSource("init", true), name, Args(new {id = "io.nori.test"})));
			await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runtime.InvokeManagementAsync(new PluginManagementSource("main", false), name, Args(new {id = "io.nori.test"})));
			await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runtime.InvokeManagementAsync(new PluginManagementSource("settings", false, IsTrustedNativeSettings: true), name, Args(new {id = "io.nori.test"})));
		}
	}

	[Fact]
	public async Task plugin_list返回稳定DTO且不泄露安装路径或异常对象()
	{
		CreateInstalledLayout("io.nori.dto", "1.2.3");
		await using PluginRuntimeHost runtime = CreateHost();

		object? result = await runtime.InvokeManagementAsync(new PluginManagementSource("main", true), "plugin_list", Args(new { }));
		string json = JsonSerializer.Serialize(result, JsonOptions);
		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement item = document.RootElement.GetProperty("plugins")[0];
		Assert.Equal("io.nori.dto", item.GetProperty("id").GetString());
		Assert.Equal("installed", item.GetProperty("state").GetString());
		Assert.Equal("Nori Test", item.GetProperty("author").GetString());
		Assert.False(item.TryGetProperty("installPath", out _));
		Assert.False(item.TryGetProperty("storagePath", out _));
		Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("stackTrace", json, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task 安装取消是正常结果且前端路径参数完全被忽略()
	{
		FakePicker picker = new(null);
		await using PluginRuntimeHost runtime = CreateHost(picker: picker);
		object? result = await runtime.InvokeManagementAsync(
			new PluginManagementSource("main", true),
			"plugin_install_local",
			Args(new {path = Path.Combine(_root, "forged.noripack"), filePath = "C:\\forged.noripack"}));
		string json = JsonSerializer.Serialize(result, JsonOptions);
		Assert.Contains("\"cancelled\":true", json, StringComparison.Ordinal);
		Assert.Equal(1, picker.Calls);
	}

	[Fact]
	public async Task SafeMode允许列表禁用卸载并拒绝安装启用和Reload()
	{
		CreateInstalledLayout("io.nori.safe", "1.0.0");
		FakePicker picker = new(null);
		await using PluginRuntimeHost runtime = CreateHost(safeMode: true, picker: picker);
		PluginManagementSource main = new("main", true);

		object? list = await runtime.InvokeManagementAsync(main, "plugin_list", Args(new { }));
		Assert.Contains("safe_mode_disabled", JsonSerializer.Serialize(list, JsonOptions), StringComparison.Ordinal);
		await Assert.ThrowsAsync<PluginException>(() => runtime.InvokeManagementAsync(main, "plugin_install_local", Args(new { })));
		Assert.Equal(0, picker.Calls);
		await Assert.ThrowsAsync<PluginException>(() => runtime.InvokeManagementAsync(main, "plugin_enable", Args(new {id = "io.nori.safe"})));
		PluginException reload = await Assert.ThrowsAsync<PluginException>(() => runtime.InvokeManagementAsync(main, "plugin_reload", Args(new {id = "io.nori.safe"})));
		Assert.Equal(PluginErrorCodes.SafeModeDisabled, reload.Code);
		Assert.False(Directory.Exists(Path.Combine(_root, "plugins", "data", "io.nori.safe")));

		object? disabled = await runtime.InvokeManagementAsync(main, "plugin_disable", Args(new {id = "io.nori.safe"}));
		Assert.Contains("\"enabled\":false", JsonSerializer.Serialize(disabled, JsonOptions), StringComparison.Ordinal);
		object? uninstalled = await runtime.InvokeManagementAsync(main, "plugin_uninstall", Args(new {id = "io.nori.safe", deleteData = false}));
		Assert.Contains("\"success\":true", JsonSerializer.Serialize(uninstalled, JsonOptions), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("main", false, false)]
	[InlineData("settings", true, false)]
	[InlineData("main", false, true)]
	public async Task plugin_reload从授权来源返回活动或待重启DTO(string label, bool trustedSettings, bool retainContribution)
	{
		await Task.Run(async () =>
		{
			CreateInstalledLayout("io.nori.reload", "1.0.0", loadable: true);
			await using PluginRuntimeHost runtime = CreateHost();
			runtime.Discover();
			await runtime.StartAllAsync();
			IPluginContribution? retained = retainContribution ? Assert.Single(runtime.GetContributions<IPluginContribution>()) : null;

			object? result = await runtime.InvokeManagementAsync(
				new PluginManagementSource(label, true, IsTrustedNativeSettings: trustedSettings),
				"plugin_reload", Args(new {id = "io.nori.reload"}));

			PluginListItemDto info = Assert.IsType<PluginListItemDto>(result);
			Assert.Equal("io.nori.reload", info.Id);
			Assert.Equal(retainContribution ? "pending_restart" : "active", info.State);
			Assert.True(info.Enabled);
			Assert.Equal(retainContribution, info.RequiresRestart);
			Assert.Equal(retainContribution ? PluginErrorCodes.UnloadPendingRestart : null, info.ErrorCode);
			Assert.Equal(retainContribution ? 0 : 1, runtime.GetContributions<IPluginContribution>().Count);
			GC.KeepAlive(retained);
		});
	}

	[Theory]
	[InlineData("{}", PluginErrorCodes.InvalidPluginId)]
	[InlineData("{\"id\":\"../outside\"}", PluginErrorCodes.InvalidPluginId)]
	[InlineData("{\"id\":\"missing.plugin\"}", PluginErrorCodes.PluginNotFound)]
	public async Task plugin_reload校验参数和已安装身份(string json, string code)
	{
		await using PluginRuntimeHost runtime = CreateHost();
		using JsonDocument args = JsonDocument.Parse(json);
		PluginException failure = await Assert.ThrowsAsync<PluginException>(() => runtime.InvokeManagementAsync(
			new PluginManagementSource("main", true), "plugin_reload", args.RootElement));
		Assert.Equal(code, failure.Code);
	}

	private PluginRuntimeHost CreateHost(bool safeMode = false, IPluginPackagePicker? picker = null) =>
		new(new PluginRuntimeHostOptions
		{
			DataDirectory = _root,
			SafeMode = safeMode,
			PackagePicker = picker,
		});

	private void CreateInstalledLayout(string id, string version, bool loadable = false)
	{
		string directory = Path.Combine(_root, "plugins", id, version);
		Directory.CreateDirectory(directory);
		string assembly = loadable ? "Nori.PluginRuntime.TestPlugin.dll" : "missing.dll";
		string entryType = loadable ? "Nori.PluginRuntime.TestPlugin.TestPlugin" : "Missing.Entry";
		if (loadable)
		{
			Directory.CreateDirectory(Path.Combine(directory, "lib"));
			File.Copy(typeof(Nori.PluginRuntime.TestPlugin.TestPlugin).Assembly.Location, Path.Combine(directory, "lib", assembly));
		}
		File.WriteAllText(Path.Combine(_root, "plugins", id, PluginPackageInstaller.CurrentFileName), JsonSerializer.Serialize(new {Version = version}));
		File.WriteAllText(Path.Combine(directory, PluginPackageInstaller.ManifestFileName), $"{{\"schemaVersion\":1,\"id\":\"{id}\",\"name\":\"Bridge Test\",\"description\":\"Plugin DTO test\",\"version\":\"{version}\",\"authors\":[{{\"name\":\"Nori Test\"}}],\"homepage\":\"https://example.test\",\"repository\":\"https://example.test/repo\",\"license\":\"MIT\",\"apiVersion\":\"2.0\",\"minHostVersion\":\"1.0.0\",\"runtime\":{{\"kind\":\"dotnet\",\"assembly\":\"lib/{assembly}\",\"entryType\":\"{entryType}\"}},\"ui\":{{\"webRoot\":\"web\"}},\"capabilities\":[],\"optionalCapabilities\":[],\"platforms\":[],\"dependencies\":[]}}");
	}

	private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

	public void Dispose() => PluginTestPackages.DeleteDirectory(_root);
}
