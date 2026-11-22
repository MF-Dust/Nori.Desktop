using Avalonia.Controls;
using Nori.Desktop.Appearance;
using Nori.Desktop.Platform;

namespace Nori.Desktop.Tests;

public class WindowBackdropControllerTests
{
	[Fact]
	public void X11只有根窗口公告模糊原子才支持KWin模糊()
	{
		Assert.True(X11KWinBlur.SupportsBlur([10, 20, 30], 20));
		Assert.False(X11KWinBlur.SupportsBlur([10, 30], 20));
		Assert.False(X11KWinBlur.SupportsBlur([], 20));
		Assert.False(X11KWinBlur.SupportsBlur([0], 0));
	}

	[Fact]
	public Task X11画刷随模糊确认关闭和不支持切换() => BridgeCommandsTests.WithSettingsUiAsync(() =>
	{
		Window window = new();
		List<(bool Enabled, Action<bool> Complete)> requests = [];
		using WindowBackdropController controller = new(requestX11Blur: (target, enabled, complete) =>
		{
			Assert.Same(window, target);
			requests.Add((enabled, complete));
		});
		try
		{
			window.Show();
			controller.Register(window);
			Assert.Equal([WindowTransparencyLevel.Transparent], window.TransparencyLevelHint);
			Assert.True(requests[^1].Enabled);
			AssertBrush("bg-base");
			Action<bool> initial = requests[^1].Complete;
			initial(true);
			AssertBrush("bg-glass");

			controller.SetEnabled(false);
			Assert.False(requests[^1].Enabled);
			AssertBrush("bg-base");

			controller.SetEnabled(true);
			Assert.True(requests[^1].Enabled);
			initial(true);
			AssertBrush("bg-base");
			Action<bool> latest = requests[^1].Complete;
			latest(false);
			AssertBrush("bg-base");

			window.Close();
			Assert.False(requests[^1].Enabled);
			latest(true);
			AssertBrush("bg-base");
		}
		finally { if (window.IsVisible) window.Close(); }
		return Task.CompletedTask;

		void AssertBrush(string name)
		{
			Assert.Same(NoriThemeTokens.Brush(name), window.Background);
			Assert.Same(window.Background, window.Resources["NoriWindowSurfaceBrush"]);
		}
	});
}
