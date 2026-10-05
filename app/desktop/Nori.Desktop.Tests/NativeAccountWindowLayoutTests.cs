using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Nori.Core.Cloud;
using Nori.Desktop.Account;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData(false, 720, 480)]
	[InlineData(true, 720, 480)]
	[InlineData(false, 1920, 1080)]
	[InlineData(true, 1920, 1080)]
	public Task NativeAccountWindowsKeepChromeAndBadgeOutsideScroll(bool cloud, int width, int height) => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		Window window = cloud
			? new CloudSyncWindow(fixture._services.CloudSync)
			: new AccountWindow(_ => Task.FromResult(new SignInOutcome {Ok = false}), _ => { }, SignInMethod.Password);
		Size size = NativeWindowSizing.FitToWorkArea(new Size(window.Width, window.Height),
			new PixelRect(0, 0, width, height), 1, default, default);
		window.Width = size.Width;
		window.Height = size.Height;
		try
		{
			window.Show();
			window.UpdateLayout();
			Assert.Equal(size, window.ClientSize);
			Grid root = Assert.IsType<Grid>(window.Content);
			ScrollViewer scroll = window.GetLogicalDescendants().OfType<ScrollViewer>().First();
			if (cloud)
			{
				// 操作结果可能带多行未包含项；增长正文不能把标题栏与归属标识挤出窗口。
				StackPanel inner = Assert.IsType<StackPanel>(scroll.Content);
				inner.Children.Add(new TextBlock {Text = string.Join("\n", Enumerable.Repeat("未包含：本机资源路径", 30))});
				window.UpdateLayout();
			}
			Assert.Equal(3, root.Children.Count);
			Control chrome = root.Children[0];
			Control badge = root.Children[2];
			Assert.Equal(40, chrome.Bounds.Height);
			Assert.True(badge.Bounds.Bottom <= window.ClientSize.Height);
			Assert.True(badge.Bounds.Height > 0);
			Assert.DoesNotContain(badge.GetVisualAncestors(), ancestor => ancestor is ScrollViewer);
			Assert.True(scroll.Viewport.Height > 0);
			if (cloud || height == 480) Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
		}
		finally { window.Close(); }
		return Task.CompletedTask;
	});
}
