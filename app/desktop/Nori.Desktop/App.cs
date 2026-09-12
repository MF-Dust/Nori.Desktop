using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Devolutions.AvaloniaTheme.MacOS;
using Nori.Desktop.Diagnostics;

namespace Nori.Desktop;

/// <summary>桌面应用生命周期适配器；启动装配委托给 DesktopBootstrapper。</summary>
public sealed class App : Application
{
	private DesktopBootstrapper? _bootstrapper;

	public override void Initialize()
	{
		// 统一使用 Devolutions 主题，RequestedThemeVariant.Default 让系统决定深浅色。
		DevolutionsMacOsTheme theme = new();
		// 代码创建主题时不会经过 XAML 的初始化流程，必须显式加载内部样式。
		theme.BeginInit();
		theme.EndInit();
		Styles.Add(theme);
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
