using System.Text.Json;
using Nori.Desktop.Automation;
using Nori.Desktop.Bridge;

namespace Nori.Desktop.Tests;

public sealed class BridgeCommandRouterTests
{
	[Theory]
	[InlineData("automation_get_snapshot", BridgeCommandDomain.Automation)]
	[InlineData("llm_fetch_models", BridgeCommandDomain.Ai)]
	[InlineData("settings_update_voice", BridgeCommandDomain.Voice)]
	[InlineData("model_select", BridgeCommandDomain.Model)]
	[InlineData("memory_get", BridgeCommandDomain.Memory)]
	[InlineData("skills_toggle", BridgeCommandDomain.Skills)]
	[InlineData("mcp_get_servers", BridgeCommandDomain.Mcp)]
	[InlineData("tools_set_enabled", BridgeCommandDomain.Tools)]
	[InlineData("plugin_list", BridgeCommandDomain.Plugins)]
	[InlineData("get_diagnostic_info", BridgeCommandDomain.Diagnostics)]
	[InlineData("chat_start", BridgeCommandDomain.Application)]
	[InlineData("future_command", BridgeCommandDomain.Other)]
	public void 命令按稳定领域分类(string command, BridgeCommandDomain expected)
	{
		Assert.Equal(expected, BridgeCommandRouter.Classify(command));
	}

	[Fact]
	public void 视觉探测结果使用reason字段()
	{
		string json = JsonSerializer.Serialize(new AutomationVisionProbeSnapshot(false, "测试原因"), BridgeJson.Options);

		Assert.Contains("\"reason\":\"测试原因\"", json, StringComparison.Ordinal);
		Assert.DoesNotContain("\"message\"", json, StringComparison.Ordinal);
	}
}
