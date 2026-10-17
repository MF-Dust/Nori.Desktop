using System.Text.Json;
using Nori.Desktop.Automation;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;
using Nori.PluginRuntime;

namespace Nori.Desktop.Tests;

public sealed class BridgeCommandRouterTests
{
	[Fact]
	public void 视觉探测结果使用reason字段()
	{
		string json = JsonSerializer.Serialize(new AutomationVisionProbeSnapshot(false, "测试原因"), BridgeJson.Options);

		Assert.Contains("\"reason\":\"测试原因\"", json, StringComparison.Ordinal);
		Assert.DoesNotContain("\"message\"", json, StringComparison.Ordinal);
	}
}

public partial class BridgeCommandsTests
{
	[Fact]
	public async Task 插件命令路由到插件运行时()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		await using PluginRuntimeHost pluginRuntime = new(new PluginRuntimeHostOptions
		{
			DataDirectory = Path.Combine(fixture._tempDir, "plugin-router"),
			SafeMode = true,
		});
		fixture._services.PluginRuntime = pluginRuntime;

		PluginListResultDto result = Assert.IsType<PluginListResultDto>(await new BridgeCommandRouter(fixture._services).InvokeAsync(
			new NativeSettingsCommandSource(), "plugin_list", Args(new { })));

		Assert.Empty(result.Plugins);
	}

	[Fact]
	public async Task 非插件命令回退到BridgeCommands()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			new BridgeCommandRouter(fixture._services).InvokeAsync(
				new FakeBridgeSource(WindowLabels.Main), "future_command", Args(new { })));

		Assert.Equal("未知的命令: future_command", error.Message);
	}
}
