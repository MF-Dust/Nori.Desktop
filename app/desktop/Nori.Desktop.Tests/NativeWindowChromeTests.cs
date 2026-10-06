using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public sealed class NativeWindowChromeTests
{
	[Theory]
	[InlineData(1, 1, WindowEdge.NorthWest)]
	[InlineData(799, 1, WindowEdge.NorthEast)]
	[InlineData(1, 599, WindowEdge.SouthWest)]
	[InlineData(799, 599, WindowEdge.SouthEast)]
	[InlineData(400, 1, WindowEdge.North)]
	[InlineData(400, 599, WindowEdge.South)]
	[InlineData(1, 300, WindowEdge.West)]
	[InlineData(799, 300, WindowEdge.East)]
	[InlineData(100, 20, null)]
	[InlineData(-1, 20, null)]
	public void BorderlessWindowsKeepAllEightResizeDirections(double x, double y, WindowEdge? expected)
		=> Assert.Equal(expected, NativeWindowChrome.ResizeEdge(new Point(x, y), new Size(800, 600)));
}

public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData(WindowDecorations.None)]
	[InlineData(WindowDecorations.Full)]
	public Task NativeCloseOnlyChromePreservesCallerDecorations(WindowDecorations decorations) => WithSettingsUiAsync(() =>
	{
		Window window = new() { WindowDecorations = decorations, CanResize = false };
		NativeWindowChrome bar = new(window, closeOnly: true);
		window.Content = bar;
		try
		{
			window.Show();
			window.CanResize = true;
			bar.RefreshLabels();
			Assert.Equal(decorations, window.WindowDecorations);
			Assert.Equal("WindowClose", Assert.Single(bar.GetVisualDescendants().OfType<Button>(), button => button.IsVisible).Name);
		}
		finally { window.Close(); }
		return Task.CompletedTask;
	});

	[Fact]
	public Task NativeChromePreservesCloseLifecycleAndCapabilities() => WithSettingsUiAsync(() =>
	{
		Window window = new() { Width = 800, Height = 560, Title = "Test", Content = new TextBox { Text = "draft" } };
		bool english = false;
		NativeWindowChrome bar = NativeWindowChrome.Attach(window, () => english);
		bool closeObserved = false, allowClose = false;
		window.Closing += (_, args) => { closeObserved = true; args.Cancel = !allowClose; if (!allowClose) window.Hide(); };
		try
		{
			window.Show();
			Assert.Equal(WindowDecorations.None, window.WindowDecorations);
			Button close = bar.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "WindowClose");
			Button zoom = bar.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "WindowMaximize");
			Button minimize = bar.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "WindowMinimize");
			Assert.Equal("关闭窗口", AutomationProperties.GetName(close));
			english = true; bar.RefreshLabels();
			Assert.Equal("Close window", AutomationProperties.GetName(close));
			window.CanResize = false; window.CanMinimize = false;
			Assert.False(zoom.IsEnabled); Assert.False(minimize.IsEnabled);
			NativeWindowChrome.ToggleMaximized(window);
			Assert.Equal(WindowState.Normal, window.WindowState);
			window.CanResize = true;
			NativeWindowChrome.ToggleMaximized(window);
			Assert.Equal(WindowState.Maximized, window.WindowState);
			NativeWindowChrome.ToggleMaximized(window);
			Assert.Equal(WindowState.Normal, window.WindowState);
			close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.True(closeObserved); Assert.False(window.IsVisible);
			Assert.Equal("draft", window.GetVisualDescendants().OfType<TextBox>().Single().Text);
		}
		finally { allowClose = true; window.Close(); }
		return Task.CompletedTask;
	});
}
