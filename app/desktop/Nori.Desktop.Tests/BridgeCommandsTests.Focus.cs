using System.Text.Json;
using Nori.Core.Configuration;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public async Task focus命令可暂停继续结束且不改主动互动开关()
	{
		string idle = _config.GetStringOr("proactive_idle_enabled", "");
		string greeting = _config.GetStringOr("proactive_daily_greeting", "");
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);

		InvalidOperationException invalid = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(main, "focus_start", Args(new { minutes = 25.5 })));
		Assert.Contains("1 到 180", invalid.Message, StringComparison.Ordinal);
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(main, "focus_start", Args(new { minutes = 181 })));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(main, "focus_pause", Args(new { })));

		string started = JsonSerializer.Serialize(await commands.InvokeAsync(
			main, "focus_start", Args(new { minutes = 25, notify = false, silentReminders = true })), BridgeJson.Options);
		Assert.Contains("\"phase\":\"running\"", started, StringComparison.Ordinal);
		Assert.True(File.Exists(_services.Paths.FocusStatePath));
		Assert.False(_config.Exists("focus_state"));
		Assert.Equal(idle, _config.GetStringOr("proactive_idle_enabled", ""));
		Assert.Equal(greeting, _config.GetStringOr("proactive_daily_greeting", ""));

		string paused = JsonSerializer.Serialize(await commands.InvokeAsync(main, "focus_pause", Args(new { })), BridgeJson.Options);
		Assert.Contains("\"phase\":\"paused\"", paused, StringComparison.Ordinal);
		string resumed = JsonSerializer.Serialize(await commands.InvokeAsync(main, "focus_resume", Args(new { })), BridgeJson.Options);
		Assert.Contains("\"phase\":\"running\"", resumed, StringComparison.Ordinal);
		string ended = JsonSerializer.Serialize(await commands.InvokeAsync(main, "focus_end", Args(new { })), BridgeJson.Options);
		Assert.Contains("\"phase\":\"idle\"", ended, StringComparison.Ordinal);

		JsonElement snapshot = JsonSerializer.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options);
		Assert.Equal("idle", snapshot.GetProperty("focus").GetProperty("phase").GetString());
		Assert.Equal(idle, _config.GetStringOr("proactive_idle_enabled", ""));
		Assert.Equal(greeting, _config.GetStringOr("proactive_daily_greeting", ""));
	}

	[Fact]
	public async Task 安全模式拒绝开始或继续专注()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		BridgeCommands commands = fixture.CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		InvalidOperationException started = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(main, "focus_start", Args(new { minutes = 25 })));
		Assert.Contains("安全模式", started.Message, StringComparison.Ordinal);
		InvalidOperationException resumed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(main, "focus_resume", Args(new { })));
		Assert.Contains("安全模式", resumed.Message, StringComparison.Ordinal);

		string view = JsonSerializer.Serialize(await commands.InvokeAsync(main, "focus_view", Args(new { })), BridgeJson.Options);
		Assert.Contains("\"phase\":\"idle\"", view, StringComparison.Ordinal);
	}
}
