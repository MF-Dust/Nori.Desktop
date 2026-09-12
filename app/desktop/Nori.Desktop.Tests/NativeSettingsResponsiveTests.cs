using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Desktop.Settings;
using Nori.Desktop.Settings.Pages;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData("skills", false)]
	[InlineData("skills", true)]
	[InlineData("mcp", false)]
	[InlineData("mcp", true)]
	public Task NativeSettingsListsReflowWithoutReplacingCards(string page, bool alternate) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: false);
		SettingsWindow window = new() {Width = 720, Height = 480};
		using SettingsService service = new(fixture._services, window);
		using SettingsViewModel viewModel = new(service);
		window.DataContext = viewModel;
		try
		{
			window.Show();
			viewModel.Navigate(page);
			NativeSettingsPageBase native = Assert.IsAssignableFrom<NativeSettingsPageBase>(viewModel.CurrentPage);
			if (native.ComplexViewModel is SkillsSettingsViewModel skills) skills.ShowMarketplace = alternate;
			if (native.ComplexViewModel is McpSettingsViewModel mcp)
			{
				mcp.ShowTools = alternate;
				if (!alternate) await SeedSettingsServersAsync(mcp, longName: true);
			}
			await viewModel.RefreshSnapshotAsync();
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Grid grid = window.GetVisualDescendants().OfType<Grid>().Single(control => control.Classes.Contains("settings-card-grid"));
			Assert.True(grid.Children.Count >= 3);
			Control[] cards = grid.Children.ToArray();
			Button action = cards[0].GetVisualDescendants().OfType<Button>().First(button => button.IsEnabled);
			action.Focus();
			Assert.True(action.IsFocused);

			foreach ((int width, int columns) in new[] {(720, 1), (960, 2), (1920, 3), (960, 2), (720, 1)})
			{
				window.Width = width;
				await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
				Assert.Equal(columns, grid.ColumnDefinitions.Count);
				Assert.Equal(cards, grid.Children.ToArray());
				Assert.True(action.IsFocused);
				for (int index = 0; index < cards.Length; index++)
				{
					Assert.Equal(index % columns, Grid.GetColumn(cards[index]));
					Assert.Equal(index / columns, Grid.GetRow(cards[index]));
					Assert.InRange(cards[index].Bounds.Right, 1, grid.Bounds.Width + 1);
					Assert.True(cards[index].Bounds.Height > 0);
				}
				ScrollViewer scroll = window.FindControl<ScrollViewer>("SettingsPageScroll")!;
				Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 2);
			}

			// 筛选走数据更新路径；缩放本身不应清空搜索框或替换它。
			TextBox search = window.FindControl<SettingsPagePresenter>("PagePresenter")!.GetVisualDescendants().OfType<TextBox>().Single(control => control.PlaceholderText == NativeSettingsResources.Get(page == "skills" ? "skills.search" : "mcp.search"));
			search.Text = "__nori_no_matching_entry__";
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Assert.DoesNotContain(window.GetVisualDescendants().OfType<Grid>(), control => control.Classes.Contains("settings-card-grid"));
			search.Text = "";
			await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
			Assert.Equal(cards.Length, window.GetVisualDescendants().OfType<Grid>().Single(control => control.Classes.Contains("settings-card-grid")).Children.Count);
			Assert.Empty(native.ComplexViewModel.ErrorMessage);
		}
		finally
		{
			window.DataContext = null;
			window.Close();
		}
	});

	private static async Task SeedSettingsServersAsync(McpSettingsViewModel viewModel, bool longName = false)
	{
		string[] names = ["Documentation", "Local workspace", "Research library", "Browser tools"];
		for (int index = 0; index < names.Length; index++)
		{
			// 只保存停用的保留域名配置，不启动进程、不访问外网。
			await viewModel.SaveServerAsync(new McpServerDraft($"visual-server-{index}",
				longName && index == 0 ? new string('W', 120) : names[index],
				"sse", "", [], new Dictionary<string, string>(), "https://example.test/sse", false, false));
		}
	}
}
