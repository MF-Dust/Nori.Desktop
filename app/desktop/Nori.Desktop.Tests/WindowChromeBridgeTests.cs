using System.Text.Json;
using Avalonia.Controls;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private sealed class WindowChromeProxySource(Window window) : IBridgeSource
	{
		public string Label => WindowLabels.Main;
		public bool IsVisible => true;
		public Window? Self => window;
		public void PostEvent(string name, object? payload) { }
		public void PostResult(long id, object? value, string? error) { }
	}

	[Theory]
	[InlineData("window_get_state")]
	[InlineData("window_minimize")]
	[InlineData("window_toggle_maximized")]
	public Task WindowChromeBridgeRejectsNonWindowAndProxySources(string command) => WithSettingsUiAsync(async () =>
	{
		var commands = CreateCommands();
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(new FakeBridgeSource(WindowLabels.Main), command, Args(new { })));
		Window unrelated = new();
		try
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(new WindowChromeProxySource(unrelated), command, Args(new { })));
			Assert.Equal(WindowState.Normal, unrelated.WindowState);
		}
		finally { unrelated.Close(); }
	});

	[Fact]
	public Task WindowChromeBridgeOnlyControlsSourceAndReturnsActualState() => WithSettingsUiAsync(async () =>
	{
		await using NoriBridge bridge = new(_services);
		NoriWindow source = CreateWindow(WindowLabels.Main, true);
		NoriWindow other = CreateWindow(WindowLabels.FirstRun, false);
		try
		{
			source.Show();
			var commands = CreateCommands();
			JsonElement initial = JsonSerializer.SerializeToElement(await commands.InvokeAsync(source, "window_get_state", Args(new { })));
			Assert.False(initial.GetProperty("maximized").GetBoolean());
			Assert.True(initial.GetProperty("canResize").GetBoolean());
			JsonElement maximized = JsonSerializer.SerializeToElement(await commands.InvokeAsync(source, "window_toggle_maximized", Args(new { label = WindowLabels.FirstRun })));
			Assert.True(maximized.GetProperty("maximized").GetBoolean());
			Assert.Equal(WindowState.Maximized, source.WindowState);
			Assert.Equal(WindowState.Normal, other.WindowState);
			JsonElement restored = JsonSerializer.SerializeToElement(await commands.InvokeAsync(source, "window_toggle_maximized", Args(new { })));
			Assert.False(restored.GetProperty("maximized").GetBoolean());
			Assert.Equal(WindowState.Normal, source.WindowState);
			await commands.InvokeAsync(source, "window_minimize", Args(new { label = WindowLabels.FirstRun }));
			Assert.Equal(WindowState.Minimized, source.WindowState);
			Assert.Equal(WindowState.Normal, other.WindowState);
		}
		finally { source.AllowClose = other.AllowClose = true; source.Close(); other.Close(); }

		NoriWindow CreateWindow(string label, bool canResize) => new(new WindowDefinition
		{
			Label = label, Title = "窗口状态测试", Width = 720, Height = 480, CanResize = canResize,
		}, bridge, "about:blank", _services.Paths)
		{
			// Headless 测试不挂接系统浏览器，状态操作仍使用真实 NoriWindow。
			Content = null,
		};
	});

	[Fact]
	public Task WindowChromeBridgeRejectsUnavailableWindowActions() => WithSettingsUiAsync(async () =>
	{
		await using NoriBridge bridge = new(_services);
		NoriWindow source = new(new WindowDefinition
		{
			Label = WindowLabels.FirstRun, Title = "固定窗口测试", Width = 720, Height = 480, CanResize = false,
		}, bridge, "about:blank", _services.Paths) { Content = null, CanMinimize = false };
		try
		{
			var commands = CreateCommands();
			JsonElement state = JsonSerializer.SerializeToElement(await commands.InvokeAsync(source, "window_get_state", Args(new { })));
			Assert.False(state.GetProperty("canResize").GetBoolean());
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(source, "window_toggle_maximized", Args(new { })));
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(source, "window_minimize", Args(new { })));
			Assert.Equal(WindowState.Normal, source.WindowState);
		}
		finally { source.AllowClose = true; source.Close(); }
	});
}
