using Nori.Core.Assets;
using Nori.Core.Configuration;
using Nori.Desktop.Audio;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData("auto")]
	[InlineData("webview")]
	public Task 音频兼容宿主独立于原生主窗口(string backend) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new();
		fixture._config.Set(ConfigStore.KeyAudioBackend, new ConfigValue.Text(backend));
		await using AssetServer assets = await AssetServer.StartAsync(new AssetServerOptions
		{
			AppRoot = fixture._tempDir,
			ResourcesRoot = fixture._tempDir,
		});
		WindowManager manager = new(assets, new NativeWindowLifetime(), fixture._services.Paths);
		manager.CreateAudioHost(new NoriBridge(fixture._services), fixture._services);
		NoriWindow? host = manager.GetNoriWindow(WindowLabels.AudioHost);
		bool needsHost = backend == "webview" || !System.OperatingSystem.IsWindows();
		try
		{
			Assert.Equal(needsHost, host is not null);
			Assert.Null(manager.GetNoriWindow(WindowLabels.Main));
			Assert.Empty(manager.All);
			Assert.False(manager.IsWindowVisible(WindowLabels.AudioHost));
			if (host is null) return;
			Assert.False(host.ShowInTaskbar);
			Assert.False(host.ShowActivated);
			using AudioHostChannel channel = new(() => manager.GetNoriWindow(WindowLabels.AudioHost));
			Assert.True(channel.IsAvailable);
			Task ready = channel.WaitUntilReadyAsync();
			Assert.False(ready.IsCompleted);
			channel.MarkReady();
			await ready;
		}
		finally
		{
			if (host is not null) { host.AllowClose = true; host.Close(); }
		}
	});

	[Fact]
	public async Task 专用音频来源仅能回报音频不能调用业务命令()
	{
		BridgeCommandRouter router = new(_services);
		FakeBridgeSource audio = new(WindowLabels.AudioHost, isVisible: false);
		await router.InvokeAsync(audio, "audio_host_ready", Args(new { }));
		await router.InvokeAsync(audio, "audio_level", Args(new {level = 0.25}));
		foreach (string command in new[] {"ui_get_snapshot", "window_show", "plugin_action", "settings_update_voice"})
			await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.InvokeAsync(audio, command, Args(new { })));
		await Assert.ThrowsAsync<InvalidOperationException>(() => router.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "audio_host_ready", Args(new { })));
	}
}
