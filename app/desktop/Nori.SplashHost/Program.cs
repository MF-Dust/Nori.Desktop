using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Nori.Startup;

namespace Nori.SplashHost;

/// <summary>一次性启动展示进程；管道断开、宿主退出或启动超时后自动关闭。</summary>
internal static class Program
{
	internal static NamedPipeServerStream Pipe { get; private set; } = null!;
	internal static Process Host { get; private set; } = null!;

	[STAThread]
	private static int Main(string[] args)
	{
		try
		{
			if (args.Length != 3 || !int.TryParse(args[1], out int pid) || !long.TryParse(args[2], out long ticks)) return 2;
			using Process host = Process.GetProcessById(pid);
			if (host.StartTime.ToUniversalTime().Ticks != ticks || host.HasExited) return 0;
			Host = host;
			using NamedPipeServerStream pipe = new(args[0], PipeDirection.In, 1,
				PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			Pipe = pipe;
			return AppBuilder.Configure<SplashApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"启动画面不可用：{exception.GetType().Name}");
			return 1;
		}
	}
}

internal sealed class SplashApp : Application
{
	private bool _finished;
	private SplashWindow? _window;

	public override void Initialize()
	{
		RequestedThemeVariant = ThemeVariant.Dark;
		Styles.Add(new FluentTheme());
		Styles.Add(new StyleInclude(new Uri("avares://Nori.SplashHost/"))
		{
			Source = new Uri("avares://Nori.SplashHost/Appearance/NoriControls.axaml"),
		});
		FontFamily font = new("avares://Nori.SplashHost/Assets/Fonts#Plus Jakarta Sans, Noto Sans SC, Noto Sans JP, Microsoft YaHei UI, PingFang SC");
		Resources["DefaultFontFamily"] = font;
		Resources["ContentControlThemeFontFamily"] = font;
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
			_ = Task.Run(() => ReceiveAsync(desktop));
			_ = WatchHostAsync(desktop);
		}
		base.OnFrameworkInitializationCompleted();
	}

	private void Finish(IClassicDesktopStyleApplicationLifetime desktop)
	{
		if (_finished) return;
		_finished = true;
		_window?.Close();
		desktop.Shutdown();
	}

	private async Task WatchHostAsync(IClassicDesktopStyleApplicationLifetime desktop)
	{
		try { await Program.Host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
		catch (TimeoutException) { }
		await Dispatcher.UIThread.InvokeAsync(() => Finish(desktop));
	}

	private async Task ReceiveAsync(IClassicDesktopStyleApplicationLifetime desktop)
	{
		try
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
			await Program.Pipe.WaitForConnectionAsync(timeout.Token);
			using StreamReader reader = new(Program.Pipe);
			while (await reader.ReadLineAsync(timeout.Token) is { } line)
			{
				if (line.Length > 8192) break;
				SplashMessage? message = JsonSerializer.Deserialize<SplashMessage>(line);
				if (message is null) continue;
				if (message.Done || message.Enabled == false) break;
				await Dispatcher.UIThread.InvokeAsync(() =>
				{
					if (_finished) return;
					if (message.Enabled == true && _window is null)
					{
						_window = new SplashWindow(message.Theme, message.Background, message.Version);
						// 快速启动不强制展示，也不为动画延长宿主启动时间。
						_ = ShowLaterAsync(desktop);
					}
					if (_window is not null && message.Stage.Length > 0)
						_window.UpdateStage(message.Progress, message.Stage);
				});
			}
		}
		catch (Exception exception)
		{
			// 展示进程的边界兜底；宿主错误仍由 Desktop 展示。
			Console.Error.WriteLine($"启动画面已关闭：{exception.GetType().Name}");
		}
		finally { await Dispatcher.UIThread.InvokeAsync(() => Finish(desktop)); }
	}

	private async Task ShowLaterAsync(IClassicDesktopStyleApplicationLifetime desktop)
	{
		await Task.Delay(450);
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			if (_finished || _window is null) return;
			desktop.MainWindow = _window;
			_window.Show();
		});
	}
}
