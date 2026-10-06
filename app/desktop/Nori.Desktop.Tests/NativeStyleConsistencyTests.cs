using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nori.Desktop.Appearance;
using Nori.Desktop.Settings;
using Nori.Desktop.Settings.Pages;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Theory]
	[InlineData(720, 480, "zh-CN")]
	[InlineData(1920, 1080, "zh-CN")]
	[InlineData(720, 480, "en-US")]
	[InlineData(1920, 1080, "en-US")]
	public Task NativeStyleControlsUseNoriPalette(int width, int height, string language) => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		bool english = language == "en-US";
		NumericUpDown number = new() { Value = 0.8m, Minimum = 0, Maximum = 1, Increment = 0.05m, Width = 144, HorizontalAlignment = HorizontalAlignment.Left };
		Button hint = new() { Content = english ? "Hover for help" : "悬停查看提示", HorizontalAlignment = HorizontalAlignment.Left };
		ToolTip tip = new() { Content = english ? "Close window" : "关闭窗口" };
		ToolTip.SetTip(hint, tip);
		Window window = new()
		{
			Width = width, Height = height,
			Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = { new TextBlock { Text = english ? "Importance" : "重要程度" }, number, hint } },
		};
		window.Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/")) { Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml") });
		// 只借用真实桌宠菜单，不打开 GL 窗口或加载模型。
		PetWindow pet = new(WindowDefinition.All.Single(definition => definition.Label == WindowLabels.Pet), fixture._services);
		ContextMenu menu = Assert.IsType<ContextMenu>(pet.ContextMenu);
		pet.ContextMenu = null;
		window.ContextMenu = menu;
		string suffix = $"{width}x{height}-{language}";
		try
		{
			window.Show();
			window.UpdateLayout();
			RepeatButton increase = number.GetVisualDescendants().OfType<RepeatButton>().Single(button => button.Name == "PART_IncreaseButton");
			AssertNoriBrush(increase.Background, "bg-input");
			AssertNoriBrush(increase.BorderBrush, "line-strong");
			CaptureUi(window, "number-" + suffix);
			Point point = increase.TranslatePoint(new Point(increase.Bounds.Width / 2, increase.Bounds.Height / 2), window)!.Value;
			window.MouseDown(point, MouseButton.Left);
			ContentPresenter presenter = increase.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_ContentPresenter");
			AssertNoriBrush(presenter.Background, "bg-card-hover");
			CaptureUi(window, "number-pressed-" + suffix);
			window.MouseUp(point, MouseButton.Left);

			ToolTip.SetIsOpen(hint, true);
			Dispatcher.UIThread.RunJobs();
			AssertNoriBrush(tip.Background, "bg-tooltip");
			AssertNoriBrush(tip.Foreground, "text-primary");
			AssertNoriBrush(tip.BorderBrush, "line-strong");
			await WaitForTooltipAsync(tip);
			CaptureUi(Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(tip)), "tooltip-" + suffix);
			ToolTip.SetIsOpen(hint, false);

			menu.Open(window);
			Dispatcher.UIThread.RunJobs();
			TopLevel popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(menu));
			foreach (MenuItem item in new[] { menu.Items.OfType<MenuItem>().First(), menu.Items.OfType<MenuItem>().Last() })
			{
				popup.MouseMove(item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), popup)!.Value);
				Border surface = item.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_LayoutRoot");
				AssertNoriBrush(surface.Background, "bg-card-hover");
				bool danger = item == menu.Items.OfType<MenuItem>().Last();
				AssertNoriBrush(item.Foreground, danger ? "danger-text" : "text-body");
				CaptureUi(popup, $"pet-menu-{(danger ? "danger" : "normal")}-{suffix}");
			}
			AssertNoriBrush(Assert.IsAssignableFrom<IBrush>(menu.Resources["LayoutBackgroundMidBrush"]), "bg-selection");
		}
		finally
		{
			ToolTip.SetIsOpen(hint, false);
			menu.Close();
			window.Close();
			pet.AllowClose = true;
			pet.Close();
		}
	});

	[Fact]
	public Task NativeMcpFeedbackUsesErrorColorAndResetsOnRetry() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		Window owner = new() { Width = 720, Height = 480 };
		using SettingsService service = new(fixture._services, owner);
		McpSettingsViewModel viewModel = new(service);
		owner.Show();
		try
		{
			Task editing = NativeMcpServerEditor.ShowAsync(owner, viewModel, McpSettingsViewModel.NewDraft("test") with { Arguments = [] }, false, false);
			Window dialog = Assert.Single(owner.OwnedWindows);
			TextBlock feedback = dialog.GetLogicalDescendants().OfType<TextBlock>().Single(block => !block.IsVisible);
			TextBox arguments = dialog.GetLogicalDescendants().OfType<TextBox>().Single(editor => editor.Text == "[]");
			Button test = dialog.GetLogicalDescendants().OfType<Button>().Single(button => (string?)button.Content == NativeSettingsResources.Get("common.test"));
			// 本地解析失败即返回，不执行网络连接测试。
			arguments.Text = "{";
			test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.True(feedback.IsVisible);
			Assert.False(string.IsNullOrWhiteSpace(feedback.Text));
			AssertNoriBrush(feedback.Foreground, "danger-text");
			CaptureUi(dialog, "mcp-feedback-error");
			bool reset = false;
			feedback.PropertyChanged += (_, args) =>
			{
				if (args.Property == TextBlock.ForegroundProperty && feedback.Foreground is ISolidColorBrush brush && brush.Color == NoriThemeTokens.Color("text-muted")) reset = true;
			};
			test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.True(reset);
			AssertNoriBrush(feedback.Foreground, "danger-text");
			dialog.Close();
			await editing;
		}
		finally
		{
			foreach (Window dialog in owner.OwnedWindows.ToArray()) dialog.Close();
			owner.Close();
		}
	});

	// 主题提示有 150ms 淡入，等待动画完成后才把帧当作视觉证据。
	private static Task WaitForTooltipAsync(ToolTip tip) => WaitUntilAsync(() =>
	{
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		return tip.Opacity >= 0.99;
	});

	private static void AssertNoriBrush(IBrush? brush, string token) =>
		Assert.Equal(NoriThemeTokens.Color(token), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
}
