using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Core.Logging;
using Nori.Core.Platform;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tray;

/// <summary>图标重注册由 Avalonia 负责；宿主复查实际托盘状态，确保主窗口入口不会丢失。</summary>
internal static class TrayHostMonitor
{
	internal static async Task RunAsync(AppServices services, IPlatformServices platform, TimeSpan interval, CancellationToken cancellationToken)
	{
		using PeriodicTimer timer = new(interval);
		bool probeFailed = false;
		try
		{
			while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
			{
				bool available;
				try
				{
					available = await platform.IsTrayAvailableAsync(cancellationToken).ConfigureAwait(false);
					probeFailed = false;
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
				catch (Exception exception)
				{
					available = false;
					if (!probeFailed)
						services.Logger.Write(LogSource.Backend, "warn", "系统托盘状态检查失败，保留任务栏入口", exception: exception);
					probeFailed = true;
				}
				// 退出阶段 UI 线程可能正在同步等待清理，必须允许取消尚未执行的 UI 调度。
				await Dispatcher.UIThread.InvokeAsync(() => ApplyAvailability(services, available),
					DispatcherPriority.Normal, cancellationToken);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// 宿主退出时结束复查，不再访问窗口与已释放的服务。
		}
	}

	internal static void ApplyAvailability(AppServices services, bool available)
	{
		Dispatcher.UIThread.VerifyAccess();
		if (services.Runtime is not { } runtime || runtime.TrayAvailable == available) return;
		runtime.TrayAvailable = available;
		services.Logger.Write(LogSource.Backend, "info", available ? "系统托盘宿主已恢复" : "系统托盘宿主不可用，保留任务栏入口");
		if (available || services.Windows.IsWindowVisible(WindowLabels.FirstRun)
			|| services.Windows.IsWindowVisible(WindowLabels.Init)
			|| services.Windows.Get(WindowLabels.Main) is not { IsVisible: false } main) return;

		// 只补回任务栏入口，不弹到用户当前工作前，也不在托盘恢复时擅自藏起窗口。
		bool showActivated = main.ShowActivated;
		try
		{
			main.ShowActivated = false;
			main.WindowState = WindowState.Minimized;
			main.Show();
		}
		finally { main.ShowActivated = showActivated; }
	}
}
