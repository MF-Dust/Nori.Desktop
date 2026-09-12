using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task NativeSettingsTransitionPreservesDraftAndResetsOnlyOnPageChange() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SettingsWindow window = new() {Width = 720, Height = 480};
		using SettingsService service = new(fixture._services, window);
		using SettingsViewModel viewModel = new(service);
		window.DataContext = viewModel;
		try
		{
			window.Show();
			window.UpdateLayout();
			SettingsPagePresenter presenter = window.FindControl<SettingsPagePresenter>("PagePresenter")!;
			ScrollViewer scroll = window.FindControl<ScrollViewer>("SettingsPageScroll")!;
			Grid body = window.FindControl<Grid>("SettingsPageBody")!;
			SettingsFieldViewModel draft = viewModel.CurrentPage!.Sections.SelectMany(section => section.Fields)
				.First(field => field.EditorKind == SettingsEditorKind.Multiline);
			draft.Text = "保留尚在编辑的角色设定";
			object? content = presenter.Content;
			scroll.Offset = new Vector(0, 120);
			Assert.True(scroll.Offset.Y > 0);

			viewModel.Navigate("ai");
			presenter.RefreshPage();
			Assert.Same(content, presenter.Content);
			Assert.Equal(120, scroll.Offset.Y);
			Assert.Equal(1, body.Opacity);

			viewModel.Navigate("voice");
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			await Task.Delay(30);
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			Assert.Equal(0, scroll.Offset.Y);
			Assert.InRange(body.Opacity, 0, 0.99);
			// 连续切换普通页与复杂页，过期动画不能把最后一页留在透明状态。
			foreach (string page in new[] {"general", "mcp", "ai"}) viewModel.Navigate(page);
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			await Task.Delay(240);
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			Assert.Equal(1, body.Opacity);
			Assert.Equal("ai", viewModel.CurrentPage?.Key);
			Assert.Equal("保留尚在编辑的角色设定", draft.Text);
			Assert.Same(viewModel.CurrentPage, presenter.DataContext);

			// 关闭中断尚未完成的过渡，同样恢复基础可见状态。
			viewModel.Navigate("voice");
			window.Close();
			await Task.Delay(20);
			Assert.Equal(1, body.Opacity);
		}
		finally
		{
			window.DataContext = null;
			window.Close();
		}
	});
}
