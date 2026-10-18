using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Nori.Core.Tools;
using Nori.Desktop.Settings;
using Nori.Desktop.Settings.Pages;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativePluginActionsUseHostPermissionsAndPersistEnabledState() => WithSettingsUiAsync(async () =>
	{
		int calls = 0;
		foreach (string permission in new[] {"safe", "confirm", "dangerous"})
		{
			_runtime.Tools.Register(new RegisteredTool
			{
				Name = $"plugin__demo_plugin__{permission}", Description = "测试插件动作", Category = "plugin",
				PermissionLevel = permission, Parameters = new JsonObject(),
				Execute = (arguments, _) =>
				{
					calls++;
					return Task.FromResult<object?>(new JsonObject { ["value"] = arguments?["value"]?.DeepClone() });
				},
			});
		}
		string root = Path.Combine(Path.GetTempPath(), "nori-plugin-actions-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		Window window = new();
		window.Show();
		try
		{
			await using var runtime = CreatePageRuntime(root);
			_services.PluginRuntime = runtime;
			using SettingsService service = new(_services, window);
			PluginsSettingsViewModel vm = new(service);
			await vm.ConfirmTrustAsync();
			await vm.RefreshAsync();
			Assert.Equal(3, vm.Actions.Count);
			McpToolItem safe = vm.Actions.Single(action => action.PermissionLevel == "safe");
			Assert.Equal(JsonValueKind.Object, safe.InputSchema.ValueKind);
			JsonElement result = await vm.TestActionAsync(safe, "{\"value\":42}");
			Assert.Equal(42, result.GetProperty("value").GetInt32());
			Assert.Equal(1, calls);
			foreach (McpToolItem action in vm.Actions.Where(action => action.PermissionLevel != "safe"))
				await Assert.ThrowsAsync<InvalidOperationException>(() => vm.TestActionAsync(action, "{}"));
			await Assert.ThrowsAsync<ArgumentException>(() => vm.TestActionAsync(safe, "[]"));
			await Assert.ThrowsAsync<InvalidOperationException>(() => vm.TestActionAsync(safe with {Name = "getTime", Category = "builtin"}, "{}"));
			Assert.Equal(1, calls);
			await vm.ToggleActionAsync(safe, false);
			Assert.False(vm.Actions.Single(action => action.Name == safe.Name).Enabled);
			Assert.False(_runtime.Tools.Get(safe.Name)!.Enabled);
			await Assert.ThrowsAsync<InvalidOperationException>(() => vm.TestActionAsync(vm.Actions.Single(action => action.Name == safe.Name), "{}"));
			await vm.ToggleActionAsync(safe, true);
			Assert.True(vm.Actions.Single(action => action.Name == safe.Name).Enabled);
		}
		finally
		{
			window.Close();
			_services.PluginRuntime = null;
			Directory.Delete(root, true);
		}
	});

	[Fact]
	public Task NativePluginSearchFiltersByMetadataAndActivity() => WithSettingsUiAsync(async () =>
	{
		string root = Path.Combine(Path.GetTempPath(), "nori-plugin-search-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		Window window = new();
		window.Show();
		try
		{
			await using var runtime = CreatePageRuntime(root);
			_services.PluginRuntime = runtime;
			using SettingsService service = new(_services, window);
			PluginsSettingsViewModel vm = new(service);
			await vm.RefreshAsync();
			PluginItem plugin = Assert.Single(vm.FilteredPlugins);
			vm.SearchText = "NATIVE.PLUGIN";
			Assert.Single(vm.FilteredPlugins);
			vm.SearchText = "页面测试";
			Assert.Single(vm.FilteredPlugins);
			vm.SearchText = "不存在的插件";
			Assert.Empty(vm.FilteredPlugins);
			vm.SearchText = "  ";
			vm.ActiveOnly = true;
			Assert.Empty(vm.FilteredPlugins);
			vm.ActiveOnly = false;
			Assert.Equal(plugin.Id, Assert.Single(vm.FilteredPlugins).Id);
		}
		finally
		{
			window.Close();
			_services.PluginRuntime = null;
			Directory.Delete(root, true);
		}
	});
}
