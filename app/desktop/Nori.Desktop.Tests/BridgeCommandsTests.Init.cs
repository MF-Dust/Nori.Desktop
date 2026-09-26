using System.Text.Json;
using Nori.Core.Configuration;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public void EnterMainFromInit按有效模型和自动唤出切换窗口()
	{
		InstallKnownModel("arg-nori");
		_config.Set(ConfigStore.KeySelectedModel, new ConfigValue.Text("arg-nori"));
		_config.Set("pet_auto_summon", new ConfigValue.Boolean(true));
		_windows.Show(WindowLabels.Init);

		_runtime.EnterMainFromInit();

		Assert.True(_windows.IsWindowVisible(WindowLabels.Main));
		Assert.True(_windows.IsWindowVisible(WindowLabels.Pet));
		Assert.False(_windows.IsWindowVisible(WindowLabels.Init));
	}

	[Fact]
	public void EnterMainFromInit无效模型时不显示伴侣但仍进入主界面()
	{
		_config.Set(ConfigStore.KeySelectedModel, new ConfigValue.Text("other"));
		_windows.Show(WindowLabels.Init);
		_windows.Show(WindowLabels.Pet);

		_runtime.EnterMainFromInit();

		Assert.True(_windows.IsWindowVisible(WindowLabels.Main));
		Assert.False(_windows.IsWindowVisible(WindowLabels.Pet));
		Assert.False(_windows.IsWindowVisible(WindowLabels.Init));
	}

	[Fact]
	public void ConsumeInitStartPending只能取一次()
	{
		Assert.False(_runtime.ConsumeInitStartPending());
		_runtime.MarkInitStartPending();
		Assert.True(_runtime.ConsumeInitStartPending());
		Assert.False(_runtime.ConsumeInitStartPending());
	}

	[Fact]
	public async Task 快照包含伴侣可见性与侧边栏折叠态()
	{
		BridgeCommands commands = CreateCommands();

		string hidden = JsonSerializer.Serialize(
			_runtime.BuildSnapshot());
		using JsonDocument hiddenDocument = JsonDocument.Parse(hidden);
		Assert.False(hiddenDocument.RootElement.GetProperty("pet").GetProperty("visible").GetBoolean());
		Assert.False(hiddenDocument.RootElement.GetProperty("general").GetProperty("sidebarCollapsed").GetBoolean());

		_windows.Show(WindowLabels.Pet);
		await commands.InvokeAsync(new FakeBridgeSource("main"), "settings_update_general", Args(new {sidebarCollapsed = true}));

		string shown = JsonSerializer.Serialize(
			_runtime.BuildSnapshot());
		using JsonDocument shownDocument = JsonDocument.Parse(shown);
		Assert.True(shownDocument.RootElement.GetProperty("pet").GetProperty("visible").GetBoolean());
		Assert.True(shownDocument.RootElement.GetProperty("general").GetProperty("sidebarCollapsed").GetBoolean());
	}

	[Fact]
	public async Task 快照包含遥测状态且通用设置可即时关闭()
	{
		_config.SetTelemetryConsent(TelemetryConsent.Granted);
		BridgeCommands commands = CreateCommands();
		string before = JsonSerializer.Serialize(
			_runtime.BuildSnapshot());
		using JsonDocument beforeDocument = JsonDocument.Parse(before);
		JsonElement beforeTelemetry = beforeDocument.RootElement.GetProperty("telemetry");
		Assert.True(beforeTelemetry.GetProperty("enabled").GetBoolean());
		Assert.False(beforeTelemetry.GetProperty("available").GetBoolean());
		Assert.Equal("granted", beforeTelemetry.GetProperty("consent").GetString());

		await commands.InvokeAsync(new FakeBridgeSource("main"), "settings_update_general", Args(new {telemetryEnabled = false}));
		Assert.Equal(TelemetryConsent.Denied, _config.GetTelemetryConsent());
		string after = JsonSerializer.Serialize(
			_runtime.BuildSnapshot());
		using JsonDocument afterDocument = JsonDocument.Parse(after);
		JsonElement afterTelemetry = afterDocument.RootElement.GetProperty("telemetry");
		Assert.False(afterTelemetry.GetProperty("enabled").GetBoolean());
		Assert.False(afterTelemetry.GetProperty("available").GetBoolean());
		Assert.Equal("denied", afterTelemetry.GetProperty("consent").GetString());
	}

	[Fact]
	public void 同版本快照复用缓存且失效后重建()
	{
		FakeBridgeSource source = new(WindowLabels.Main);
		object first = _runtime.BuildSnapshot(source);
		object cached = _runtime.BuildSnapshot(source);
		Assert.Same(first, cached);

		_runtime.InvalidateSnapshot();
		object rebuilt = _runtime.BuildSnapshot(source);
		Assert.NotSame(first, rebuilt);
	}

	[Fact]
	public void 伴侣显隐变化作废快照()
	{
		// 显隐会递增快照版本。
		int before = _runtime.SnapshotVersion;

		_windows.TogglePet();

		Assert.True(_runtime.SnapshotVersion > before);
		Assert.True(_windows.IsWindowVisible(WindowLabels.Pet));
	}

	[Fact]
	public void Snapshot_ContainsUpdaterState()
	{
		string json = JsonSerializer.Serialize(_runtime.BuildSnapshot());
		using JsonDocument doc = JsonDocument.Parse(json);
		Assert.True(doc.RootElement.TryGetProperty("updater", out JsonElement updaterEl));
		Assert.Equal("idle", updaterEl.GetProperty("state").GetString());
	}
	[Fact]
	public async Task UpdaterCancel_ReturnsTrue()
	{
		BridgeCommands commands = CreateCommands();
		object? result = await commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), "updater_cancel", Args(new { }));
		Assert.Equal(true, result);
	}

	[Fact]
	public async Task UpdaterCommands_NonMainSource_Throws()
	{
		BridgeCommands commands = CreateCommands();
		// 从 pet 窗口调用 updater_check 必须拒绝
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Pet), "updater_check", Args(new { })));

		// 从 first-run 窗口调用 updater_install 必须拒绝
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			commands.InvokeAsync(new FakeBridgeSource(WindowLabels.FirstRun), "updater_install", Args(new { })));
	}
}
