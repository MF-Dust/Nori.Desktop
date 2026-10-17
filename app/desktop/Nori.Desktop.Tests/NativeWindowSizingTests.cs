using Avalonia;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public sealed class NativeWindowSizingTests
{
	[Theory]
	[InlineData(1920, 1040, 1, 1040, 720)]
	[InlineData(1920, 1040, 1.5, 1040, 613)]
	[InlineData(1366, 728, 1, 1040, 648)]
	[InlineData(1280, 960, 1.25, 976, 688)]
	[InlineData(800, 600, 1, 752, 520)]
	public void WorkAreaConvertsPhysicalPixelsAndReservesDecorations(int width, int height, double scaling, double expectedWidth, double expectedHeight)
	{
		Size actual = NativeWindowSizing.FitToWorkArea(NativeWindowSizing.DefaultSize,
			new PixelRect(-width, 0, width, height), scaling, new Size(16, 48), NativeWindowSizing.MinimumSize);
		Assert.Equal(new Size(expectedWidth, expectedHeight), actual);
	}

	[Fact]
	public void TinyWorkAreaPreservesMinimumAndLargeAreaNeverEnlargesPreferredSize()
	{
		Assert.Equal(NativeWindowSizing.MinimumSize, NativeWindowSizing.FitToWorkArea(NativeWindowSizing.DefaultSize,
			new PixelRect(0, 0, 800, 600), 2, new Size(16, 48), NativeWindowSizing.MinimumSize));
		Assert.Equal(NativeWindowSizing.FirstRunSize, NativeWindowSizing.FitToWorkArea(NativeWindowSizing.FirstRunSize,
			new PixelRect(0, 0, 3840, 2160), 1, default, NativeWindowSizing.MinimumSize));
		Assert.Equal(NativeWindowSizing.DefaultSize, NativeWindowSizing.FitToWorkArea(NativeWindowSizing.DefaultSize,
			new PixelRect(0, 0, 1920, 1080), 0, default, NativeWindowSizing.MinimumSize));
	}
}

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativeWindowReopeningPreservesUserSize() => WithSettingsUiAsync(() =>
	{
		Avalonia.Controls.Window window = new();
		NativeWindowSizing.Apply(window, NativeWindowSizing.DefaultSize);
		window.Width = 850;
		window.Height = 600;
		try
		{
			window.Show();
			Assert.Equal(new Size(850, 600), new Size(window.Width, window.Height));
			window.Hide();
			window.Width = 900;
			window.Height = 650;
			window.Show();
			Assert.Equal(new Size(900, 650), new Size(window.Width, window.Height));
		}
		finally { window.Close(); }
		return Task.CompletedTask;
	});
}
