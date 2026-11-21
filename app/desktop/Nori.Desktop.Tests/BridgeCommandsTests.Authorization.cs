using System.Text.Json;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	// ---- 来源授权 ----

	[Fact]
	public async Task approval_respond未匹配请求返回false()
	{
		BridgeCommands commands = CreateCommands();
		object? result = await commands.InvokeAsync(
			new FakeBridgeSource("main"), "approval_respond", Args(new {requestId = "missing", approved = true}));
		Assert.Equal(false, result);
	}

	[Fact]
	public async Task 内置市场安装正确接线并返回脱敏技能DTO()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		int before = _runtime.SnapshotVersion;

		object? result = await commands.InvokeAsync(main, "skills_install_marketplace", Args(new {skillId = "gaming-partner"}));
		string json = JsonSerializer.Serialize(result, BridgeJson.Options);

		Assert.Contains("\"id\":\"gaming-partner\"", json, StringComparison.Ordinal);
		Assert.Contains("\"enabled\":true", json, StringComparison.Ordinal);
		Assert.Contains("\"instructions\":\"\"", json, StringComparison.Ordinal);
		Assert.DoesNotContain("【技能：二次元游戏陪玩与攻略解说】", json, StringComparison.Ordinal);
		Assert.DoesNotContain("searchWeb", json, StringComparison.Ordinal);
		Assert.True(_runtime.SnapshotVersion > before);
		var installed = Assert.Single(_runtime.Skills.GetInstalled(), skill => skill.Id == "gaming-partner");
		Assert.Equal("market", installed.Source);
		Assert.True(installed.Enabled);
	}

	[Fact]
	public async Task 内置市场安装拒绝未知ID并保持稳定错误()
	{
		BridgeCommands commands = CreateCommands();
		const string skillId = "not-a-market-skill";
		int before = _runtime.SnapshotVersion;

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "skills_install_marketplace",
				Args(new {skillId})));

		Assert.Equal($"未在市场中找到技能 ID: {skillId}", error.Message);
		Assert.Equal(before, _runtime.SnapshotVersion);
		Assert.DoesNotContain(_runtime.Skills.GetInstalled(), skill => skill.Id == skillId);
	}

	[Fact]
	public async Task 内置市场安装不接受自定义技能正文或SaveCustom()
	{
		BridgeCommands commands = CreateCommands();
		const string skillId = "custom-bypass";

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "skills_install_marketplace",
				Args(new
				{
					skillId,
					skill = new
					{
						id = skillId,
						name = "伪造技能",
						instructions = "不应被保存的隐藏正文",
					},
				})));

		Assert.Equal($"未在市场中找到技能 ID: {skillId}", error.Message);
		Assert.DoesNotContain(_runtime.Skills.GetInstalled(), skill => skill.Id == skillId);
	}

	[Fact]
	public Task 原生窗口仍限制跨领域命令() => WithSettingsUiAsync(async () =>
	{
		using Nori.Desktop.Settings.SettingsService settings = new(_services, new Avalonia.Controls.Window());
		InvalidOperationException denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			settings.ExecuteAsync("memory_clear", new {confirmToken = "CLEAR_PERSONAL_MEMORY"}));
		Assert.Contains("当前窗口不允许", denied.Message, StringComparison.Ordinal);
	});

	[Fact]
	public async Task tools_execute_manual非safe工具拒绝()
	{
		BridgeCommands commands = CreateCommands();
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource("main"), "tools_execute_manual",
				Args(new {name = "setClipboardText", arguments = new {text = "x"}})));
	}

	[Fact]
	public async Task 旧版通用config命令已下线()
	{
		BridgeCommands commands = CreateCommands();
		string[] legacyCommands = ["get_config", "set_config", "fetch_remote_text", "search_anysearch"];
		foreach (string cmd in legacyCommands)
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				commands.InvokeAsync(new FakeBridgeSource("main"), cmd, Args(new {key = "x"})));
		}
	}
}
