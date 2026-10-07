using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Nori.Desktop.Appearance;
using Nori.Desktop.Settings;
using Nori.PluginRuntime;

namespace Nori.Desktop.Windows;

/// <summary>插件页面外壳；关闭后清空控件树并释放插件资源。</summary>
internal sealed class PluginPageWindow : Window
{
	private IPluginPage? _page;
	private readonly ContentControl _body = new() { HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch };
	private readonly Action<Exception> _report;

	internal PluginPageWindow(string title, IPluginPage page, Action<Exception> report)
	{
		_report = report;
		Title = title;
		Width = 720;
		Height = 480;
		MinWidth = 360;
		MinHeight = 240;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		RequestedThemeVariant = ThemeVariant.Dark;
		Background = NoriThemeTokens.Brush("bg-base");
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/"))
		{
			Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml"),
		});
		Control control = page.Control ?? throw new InvalidOperationException("插件页面未提供控件");
		if (control is TopLevel || control.Parent is not null || control.GetVisualParent() is not null)
			throw new InvalidOperationException("插件页面必须提供独占、未挂载且非窗口的控件");
		_body.Content = control;
		Content = _body;
		NativeWindowChrome.Attach(this, () => SettingsLocalization.IsEnglish);
		NativeWindowSizing.ConstrainOnFirstOpen(this, new Size(720, 480));
		_page = page;
		Closed += OnClosed;
	}

	private void OnClosed(object? sender, EventArgs args)
	{
		_body.Content = null;
		Content = null;
		IPluginPage? page = _page;
		_page = null;
		try { page?.Dispose(); }
		catch (Exception exception) { _report(exception); }
	}
}
