using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Nori.Desktop.Appearance;
using Nori.Desktop.Diagnostics;

namespace Nori.Desktop;

/// <summary>桌面应用生命周期适配器；启动装配委托给 DesktopBootstrapper。</summary>
public sealed class App : Application
{
	private DesktopBootstrapper? _bootstrapper;

	public override void Initialize()
	{
		// 控件模板复用 Avalonia Fluent；配色、圆角与字体由 Nori 令牌覆盖，对齐 Nori.Web 的 theme-nori。
		RequestedThemeVariant = ThemeVariant.Dark;
		Styles.Add(new FluentTheme());
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/"))
		{
			Source = new Uri("avares://Nori.Desktop/Appearance/NoriControls.axaml"),
		});
		Resources["DefaultFontFamily"] = NoriTypography.System;
		Resources["ContentControlThemeFontFamily"] = NoriTypography.System;
		Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Nori.Desktop/"))
		{
			Source = new Uri("avares://Nori.Desktop/Appearance/NoriThemeResources.g.axaml"),
		});
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
			CrashReporter.Register(desktop);
			DesktopBootstrapper bootstrapper = new(this);
			_bootstrapper = bootstrapper;
			desktop.Exit += (_, _) => bootstrapper.RequestShutdown();
			_ = StartAsync(bootstrapper, desktop);
		}
		base.OnFrameworkInitializationCompleted();
	}

	private static async Task StartAsync(DesktopBootstrapper bootstrapper, IClassicDesktopStyleApplicationLifetime desktop)
	{
		try { await bootstrapper.StartAsync(desktop); }
		catch (OperationCanceledException) { }
		catch (Exception exception) { CrashReporter.Report(exception, critical: true); }
	}

	internal void ActivateMainWindow() => _bootstrapper?.ActivateMainWindow();
}
