using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Devolutions.AvaloniaTheme.MacOS;
using Nori.Desktop.Appearance;
using Nori.Desktop.Diagnostics;

namespace Nori.Desktop;

/// <summary>桌面应用生命周期适配器；启动装配委托给 DesktopBootstrapper。</summary>
public sealed class App : Application
{
	private DesktopBootstrapper? _bootstrapper;

	public override void Initialize()
	{
		// 控件模板复用 Devolutions，应用统一使用 Nori 深色设计令牌。
		RequestedThemeVariant = ThemeVariant.Dark;
		DevolutionsMacOsTheme theme = new();
		// 代码创建主题时不会经过 XAML 的初始化流程，必须显式加载内部样式。
		theme.BeginInit();
		theme.EndInit();
		Styles.Add(theme);
		Resources["DefaultFontFamily"] = NoriTypography.System;
		Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Nori.Desktop/"))
		{
			Source = new Uri("avares://Nori.Desktop/Appearance/NoriThemeResources.g.axaml"),
		});
		// 原生主题的动态强调色也使用 Nori 令牌，避免落回系统默认蓝色。
		Resources["SystemAccentColor"] = NoriThemeTokens.Color("nori-teal");
		Resources["SystemAccentColorDark1"] = NoriThemeTokens.Color("nori-teal-pressed");
		Resources["SystemAccentColorLight1"] = NoriThemeTokens.Color("nori-teal-bright");
		Resources["AccentForegroundColor"] = NoriThemeTokens.Color("on-teal");
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
