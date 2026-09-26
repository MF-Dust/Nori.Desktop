using Nori.Core.Assets;
using Nori.Core.Configuration;
using Nori.Core.Logging;
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
		WindowManager manager = new(assets, new NativeWindowLifetime().Shutdown, fixture._services.Paths);
		NoriBridge bridge = new(fixture._services);
		int created = 0;
		manager.CreateAudioHost(bridge, fixture._services, definition =>
		{
			created++;
			NoriWindow window = new(definition, bridge, assets.WindowUrl(WindowLabels.AudioHost), fixture._services.Paths);
			Assert.IsType<Avalonia.Controls.NativeWebView>(window.Content);
			// Headless 会话不承载系统浏览器；避免 Windows MTA 线程启动 WebView2 COM。
			window.Content = null;
			return window;
		});
		NoriWindow? host = manager.GetNoriWindow(WindowLabels.AudioHost);
		bool needsHost = backend == "webview" || !System.OperatingSystem.IsWindows();
		try
		{
			Assert.Equal(needsHost, host is not null);
			Assert.Equal(needsHost ? 1 : 0, created);
			Assert.Null(manager.GetNoriWindow(WindowLabels.Main));
			Assert.Empty(manager.All);
			Assert.False(manager.IsWindowVisible(WindowLabels.AudioHost));
			if (host is null) return;
			Assert.False(host.ShowInTaskbar);
			Assert.False(host.ShowActivated);
			Assert.True(host.IsVisible);
			Assert.Equal(0d, host.Opacity);
			Assert.Equal(new Avalonia.PixelPoint(-32000, -32000), host.Position);
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
		await router.InvokeAsync(audio, "write_log", Args(new
		{
			level = "error",
			message = "不应写入的异常正文",
			eventId = "audio.error",
			errorType = "TypeError",
			source = "backend",
		}));
		LogEntry LOG = _services.Logger.RecentLogs().Last();
		Assert.Equal(LogSource.Frontend, LOG.Source);
		Assert.Equal(WindowLabels.AudioHost, LOG.WindowLabel);
		Assert.Equal("audio.error", LOG.EventId);
		Assert.Equal("前端音频操作失败：TypeError", LOG.Message);
		Assert.DoesNotContain("异常正文", LOG.Message);
		foreach (string command in new[] {"ui_get_snapshot", "window_show", "plugin_action", "settings_update_voice"})
			await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.InvokeAsync(audio, command, Args(new { })));
		await Assert.ThrowsAsync<InvalidOperationException>(() => router.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "audio_host_ready", Args(new { })));
	}

	[Theory]
	[InlineData("pet.shown")]
	[InlineData("diagnostics.test")]
	[InlineData("")]
	public async Task 音频宿主不能伪造其它日志事件(string eventId)
	{
		BridgeCommandRouter router = new(_services);
		FakeBridgeSource audio = new(WindowLabels.AudioHost, isVisible: false);
		int before = _services.Logger.RecentLogs().Count;
		await Assert.ThrowsAsync<InvalidOperationException>(() => router.InvokeAsync(audio, "write_log", Args(new
		{
			level = "info",
			message = "伪造正文",
			eventId,
		})));
		Assert.Equal(before, _services.Logger.RecentLogs().Count);
	}

	[Fact]
	public async Task 音频宿主可以记录限流事件()
	{
		BridgeCommandRouter router = new(_services);
		FakeBridgeSource audio = new(WindowLabels.AudioHost, isVisible: false);
		await router.InvokeAsync(audio, "write_log", Args(new
		{
			level = "warn",
			message = "不应出现",
			eventId = "logging.suppressed",
			suppressedCount = 3,
		}));
		LogEntry log = _services.Logger.RecentLogs().Last();
		Assert.Equal("logging.suppressed", log.EventId);
		Assert.Equal(WindowLabels.AudioHost, log.WindowLabel);
		Assert.Equal("重复前端事件已被限流：3", log.Message);
		Assert.DoesNotContain("不应出现", log.Message);
	}
}
