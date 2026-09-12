using System.Text.Json;
using Avalonia.Controls;
using Nori.Desktop.Settings;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativeSecretSnapshotNeverBecomesPasswordText() => WithSettingsUiAsync(() =>
	{
		using SettingsService service = new(_services, new Window());
		using AiSettingsPage page = new(service);
		SettingsFieldViewModel secret = page.Sections.SelectMany(section => section.Fields)
			.First(field => field.EditorKind == SettingsEditorKind.Password);
		using JsonDocument missing = JsonDocument.Parse("""{"ai":{"hasApiKey":false},"embedding":{"hasApiKey":false}}""");
		page.ApplySnapshot(missing.RootElement);
		Assert.Empty(secret.Text);
		Assert.False(secret.IsConfigured);
		using JsonDocument configured = JsonDocument.Parse("""{"ai":{"hasApiKey":true},"embedding":{"hasApiKey":true}}""");
		page.ApplySnapshot(configured.RootElement);
		Assert.Empty(secret.Text);
		Assert.True(secret.IsConfigured);
		Assert.False(secret.IsDirty);
		return Task.CompletedTask;
	});

	[Fact]
	public Task NativeGeneralHonorsPlatformCapability() => WithSettingsUiAsync(() =>
	{
		using SettingsService service = new(_services, new Window());
		using GeneralSettingsPage page = new(service);
		SettingsFieldViewModel clickThrough = page.Sections.SelectMany(section => section.Fields).Single(field => field.Key == "clickThrough");
		using JsonDocument unavailable = JsonDocument.Parse("""{"platform":{"supportsHitThrough":false}}""");
		page.ApplySnapshot(unavailable.RootElement);
		Assert.True(clickThrough.IsReadOnly);
		using JsonDocument available = JsonDocument.Parse("""{"platform":{"supportsHitThrough":true}}""");
		page.ApplySnapshot(available.RootElement);
		Assert.False(clickThrough.IsReadOnly);
		return Task.CompletedTask;
	});

	[Fact]
	public Task NativeUpdaterShowsProgressAndGatesActions() => WithSettingsUiAsync(() =>
	{
		using SettingsService service = new(_services, new Window());
		using UpdatesSettingsPage page = new(service);
		Dictionary<string, SettingsFieldViewModel> fields = page.Sections.SelectMany(section => section.Fields).ToDictionary(field => field.Key);
		using JsonDocument download = JsonDocument.Parse("""{"updater":{"state":"downloading","progress":0.42,"downloadedBytes":1048576,"totalBytes":2097152}}""");
		page.ApplySnapshot(download.RootElement);
		Assert.Equal(42, fields["progress"].Number);
		Assert.True(fields["progress"].IsVisible);
		Assert.True(fields["check"].IsReadOnly);
		Assert.True(fields["cancel"].IsVisible);
		Assert.False(fields["restart"].IsVisible);
		using JsonDocument ready = JsonDocument.Parse("""{"updater":{"state":"readytorestart","availableVersion":"next"}}""");
		page.ApplySnapshot(ready.RootElement);
		Assert.False(fields["progress"].IsVisible);
		Assert.True(fields["restart"].IsVisible);
		Assert.False(fields["restart"].IsReadOnly);
		Assert.False(fields["cancel"].IsVisible);
		using JsonDocument manual = JsonDocument.Parse("""{"updater":{"state":"idle","unavailableReason":"测试环境","manualDownloadUrl":"https://github.com/MF-Dust/Nori.Desktop/releases"}}""");
		page.ApplySnapshot(manual.RootElement);
		Assert.True(fields["unavailableReason"].IsVisible);
		Assert.True(fields["manual"].IsVisible);
		Assert.True(fields["check"].IsReadOnly);
		return Task.CompletedTask;
	});
}
