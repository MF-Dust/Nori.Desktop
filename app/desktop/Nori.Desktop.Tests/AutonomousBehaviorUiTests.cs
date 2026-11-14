using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Desktop.Settings;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public Task 自主行为设置在中英文和两种窗口尺寸下可见() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		ModelsWindow models = new(fixture._services);
		try
		{
			models.Show();
			foreach (string language in new[] { "zh-CN", "en-US" })
			{
				bool english = language == "en-US";
				fixture._config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text(language));
				fixture._runtime.InvalidateSnapshot();
				SettingsLocalization.SetLanguage(language);
				foreach ((double width, double height) in new[] { (720d, 480d), (1920d, 1080d) })
				{
					models.Width = width;
					models.Height = height;
					await models.RefreshAsync();
					models.Navigate("behaviors");
					models.UpdateLayout();
					await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
					models.UpdateLayout();

					ToggleSwitch master = models.PageContent.GetLogicalDescendants().OfType<ToggleSwitch>()
						.Single(item => item.Name == "ModelsBehavior_autonomousBehavior");
					ToggleSwitch random = models.PageContent.GetLogicalDescendants().OfType<ToggleSwitch>()
						.Single(item => item.Name == "ModelsBehavior_autonomousRandom");
					ComboBox frequency = models.PageContent.GetLogicalDescendants().OfType<ComboBox>()
						.Single(item => item.Name == "ModelsBehavior_autonomousFrequency");
					Assert.True(master.IsChecked);
					Assert.True(random.IsChecked);
					Assert.Equal(1, frequency.SelectedIndex);
					Assert.Contains(
						models.PageContent.GetLogicalDescendants().OfType<TextBlock>(),
						text => text.Text == (english ? "Autonomous behavior" : "自主行为"));
					Assert.Contains(
						models.PageContent.GetLogicalDescendants().OfType<TextBlock>(),
						text => text.Text == (english ? "Behavior frequency" : "行为频率"));

					frequency.BringIntoView();
					await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
					models.UpdateLayout();
					AssertOnScreen(models, master);
					AssertOnScreen(models, frequency);
					CaptureUi(models, $"autonomous-behaviors-{language}-{width:0}x{height:0}");
				}
			}
		}
		finally
		{
			models.AllowClose = true;
			models.Close();
			SettingsLocalization.SetLanguage("zh-CN");
		}
	});

	private static void AssertOnScreen(Visual window, Visual control)
	{
		Matrix? matrix = control.TransformToVisual(window);
		Assert.NotNull(matrix);
		Point origin = matrix.Value.Transform(new Point(0, 0));
		Rect bounds = control.Bounds;
		Assert.True(bounds.Width > 0 && bounds.Height > 0);
		Assert.True(origin.X >= 0 && origin.Y >= 0, $"控件原点 ({origin.X:0},{origin.Y:0}) 不在窗口内");
		Assert.True(origin.Y + bounds.Height <= window.Bounds.Height + 1, $"控件底部 {origin.Y + bounds.Height:0} 超出窗口 {window.Bounds.Height:0}");
		Assert.True(origin.X + bounds.Width <= window.Bounds.Width + 1, $"控件右侧 {origin.X + bounds.Width:0} 超出窗口 {window.Bounds.Width:0}");
	}
}
