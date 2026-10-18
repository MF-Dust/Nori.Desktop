using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Nori.Desktop.Appearance;
using Nori.Desktop.Settings;
using Nori.PluginRuntime;

namespace Nori.Desktop.Windows;

/// <summary>插件页面外壳；关闭后清空控件树并释放插件资源。</summary>
internal sealed class PluginPageWindow : Window
{
	private readonly PluginPageHost _body;

	internal PluginPageWindow(string title, IPluginPage page, Action<Exception> report)
		: this(title, new PluginPageHost(page, report))
	{
	}

	internal PluginPageWindow(string title, PluginPageHost body)
	{
		_body = body;
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
		Content = _body;
		NativeWindowChrome.Attach(this, () => SettingsLocalization.IsEnglish);
		NativeWindowSizing.ConstrainOnFirstOpen(this, new Size(720, 480));
		Closed += OnClosed;
	}

	private void OnClosed(object? sender, EventArgs args)
	{
		_body.Dispose();
		Content = null;
	}
}
