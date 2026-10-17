using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Nori.Desktop.Appearance;

namespace Nori.SplashHost;

internal sealed partial class SplashWindow
{
	private Control BuildStandard(bool artwork, string version)
	{
		Grid layout = new() { ColumnDefinitions = new ColumnDefinitions(artwork ? "2.2*,8,2.8*" : "*") };
		Grid information = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(32) };
		information.Children.Add(Brand(version));
		StackPanel description = new() { Spacing = NoriMetrics.Spacing3, Margin = new Thickness(0, 24, 0, 24) };
		description.Children.Add(Label("你的 AI 桌面伴侣", NoriMetrics.FontBase, "text-body"));
		description.Children.Add(Label("从深海，来到你的日常。", NoriMetrics.FontSm, "text-muted"));
		Grid.SetRow(description, 1);
		information.Children.Add(description);
		StackPanel status = Status();
		status.VerticalAlignment = VerticalAlignment.Top;
		Grid.SetRow(status, 2);
		information.Children.Add(status);
		StackPanel footer = new() { Spacing = NoriMetrics.Spacing3 };
		footer.Children.Add(new Border
		{
			Background = NoriThemeTokens.Brush("bg-card"), CornerRadius = new CornerRadius(NoriMetrics.RadiusSm), Padding = new Thickness(NoriMetrics.Spacing3),
			Child = Label("正在准备你的桌面陪伴。", NoriMetrics.FontXs, "text-muted"),
		});
		DockPanel bottom = new();
		Button dismiss = Dismiss();
		DockPanel.SetDock(dismiss, Dock.Right);
		bottom.Children.Add(dismiss);
		TextBlock powered = Label(".NET · Avalonia", NoriMetrics.FontXs, "text-faint");
		powered.VerticalAlignment = VerticalAlignment.Center;
		bottom.Children.Add(powered);
		footer.Children.Add(bottom);
		Grid.SetRow(footer, 3);
		information.Children.Add(footer);
		layout.Children.Add(information);
		if (artwork)
		{
			Border divider = new() { Width = 1, Background = NoriThemeTokens.Brush("line-subtle"), Margin = new Thickness(0, 32) };
			Grid.SetColumn(divider, 1);
			layout.Children.Add(divider);
			// 右侧作品区保持空白，等待后续引入画师作品。
			Border panel = new() { CornerRadius = new CornerRadius(NoriMetrics.RadiusXl), ClipToBounds = true, Background = NoriThemeTokens.Brush("bg-deep"), Margin = new Thickness(24), Child = _background };
			Grid.SetColumn(panel, 2);
			layout.Children.Add(panel);
		}
		return layout;
	}

	private Control BuildAndroid(string version)
	{
		Grid layout = new() { RowDefinitions = new RowDefinitions("*,Auto"), ColumnDefinitions = new ColumnDefinitions("3*,2*") };
		Border artwork = new() { Background = NoriThemeTokens.Brush("bg-deep"), Child = _background, ClipToBounds = true };
		layout.Children.Add(artwork);
		StackPanel information = new() { Margin = new Thickness(24, 32, 24, 24), Spacing = NoriMetrics.Spacing5 };
		information.Children.Add(Brand(version, 32));
		information.Children.Add(_stage);
		Grid.SetColumn(information, 1);
		layout.Children.Add(information);
		Button dismiss = Dismiss();
		dismiss.HorizontalAlignment = HorizontalAlignment.Right;
		dismiss.VerticalAlignment = VerticalAlignment.Bottom;
		dismiss.Margin = new Thickness(12);
		Grid.SetColumn(dismiss, 1);
		layout.Children.Add(dismiss);
		TextBlock powered = Label(".NET · Avalonia", NoriMetrics.FontXs, "text-faint");
		powered.Margin = new Thickness(24, 0, 0, 16);
		powered.VerticalAlignment = VerticalAlignment.Bottom;
		layout.Children.Add(powered);
		Grid.SetRow(_progress, 1);
		Grid.SetColumnSpan(_progress, 2);
		layout.Children.Add(_progress);
		return layout;
	}

	private Control Build365()
	{
		Grid layout = new() { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
		DockPanel header = new();
		Button dismiss = Dismiss();
		DockPanel.SetDock(dismiss, Dock.Right);
		header.Children.Add(dismiss);
		header.Children.Add(new Border
		{
			Background = NoriThemeTokens.Brush("bg-selection"), CornerRadius = new CornerRadius(NoriMetrics.RadiusXs), Padding = new Thickness(8, 4),
			HorizontalAlignment = HorizontalAlignment.Left, Child = Label("Nori", NoriMetrics.FontBase, "nori-teal-bright"),
		});
		layout.Children.Add(header);
		Image logo = Logo(80);
		Grid.SetRow(logo, 1);
		layout.Children.Add(logo);
		StackPanel status = Status();
		Grid.SetRow(status, 2);
		layout.Children.Add(status);
		return layout;
	}

	private Control BuildOffice(string version)
	{
		Grid layout = new() { Margin = new Thickness(28), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
		DockPanel header = new();
		Button dismiss = Dismiss();
		DockPanel.SetDock(dismiss, Dock.Right);
		header.Children.Add(dismiss);
		header.Children.Add(Label("NORI DESKTOP PET", NoriMetrics.FontXs, "nori-teal-bright"));
		layout.Children.Add(header);
		Control brand = Brand(version, 56);
		brand.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetRow(brand, 1);
		layout.Children.Add(brand);
		StackPanel status = Status();
		Grid.SetRow(status, 2);
		layout.Children.Add(status);
		return layout;
	}

	private Control BuildIsland(string version)
	{
		Grid layout = new() { RowDefinitions = new RowDefinitions("*,Auto") };
		Grid bar = new() { ColumnDefinitions = new ColumnDefinitions("148,*,Auto"), ColumnSpacing = NoriMetrics.Spacing5, Margin = new Thickness(16, 8) };
		bar.Children.Add(Brand(version, 24, compact: true));
		_stage.FontSize = NoriMetrics.FontSm;
		_stage.VerticalAlignment = VerticalAlignment.Center;
		_stage.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
		_stage.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
		Grid.SetColumn(_stage, 1);
		bar.Children.Add(_stage);
		Button dismiss = Dismiss();
		dismiss.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(dismiss, 2);
		bar.Children.Add(dismiss);
		layout.Children.Add(bar);
		Grid.SetRow(_progress, 1);
		layout.Children.Add(_progress);
		return layout;
	}

	private Control BuildAlls(string version)
	{
		Grid layout = new() { RowDefinitions = new RowDefinitions("3*,Auto,*,Auto,2*") };
		StackPanel brand = new() { Orientation = Orientation.Horizontal, Spacing = NoriMetrics.Spacing6, HorizontalAlignment = HorizontalAlignment.Center };
		brand.Children.Add(Logo(72));
		TextBlock title = Label("Nori", 60, "text-primary");
		title.FontWeight = Avalonia.Media.FontWeight.Bold;
		brand.Children.Add(title);
		Grid.SetRow(brand, 1);
		layout.Children.Add(brand);
		StackPanel information = new() { Width = 320, MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Center, Spacing = NoriMetrics.Spacing6 };
		TextBlock versionText = Label(version, NoriMetrics.FontLg, "text-muted");
		versionText.TextAlignment = Avalonia.Media.TextAlignment.Center;
		information.Children.Add(versionText);
		_stage.TextAlignment = Avalonia.Media.TextAlignment.Center;
		information.Children.Add(Status());
		Grid.SetRow(information, 3);
		layout.Children.Add(information);
		Button dismiss = Dismiss();
		dismiss.HorizontalAlignment = HorizontalAlignment.Right;
		dismiss.VerticalAlignment = VerticalAlignment.Top;
		dismiss.Margin = new Thickness(24);
		Grid.SetRowSpan(dismiss, 5);
		layout.Children.Add(dismiss);
		return layout;
	}
}
