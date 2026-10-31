using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Core.Logging;
using Nori.Core.Platform;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tray;

/// <summary>图标重注册由 Avalonia 负责；宿主跟随托盘宿主的变化调整入口，确保主窗口入口不会丢失。</summary>
internal static class TrayHostMonitor
{
	/// <summary>
	/// 订阅平台的托盘宿主变化。监听失败时按无托盘处理，并在 <paramref name="retryDelay"/> 后重新订阅；
	/// 平台只报告一次时结束。
	/// </summary>
	internal static Task RunAsync(AppServices services, IPlatformServices platform, TimeSpan retryDelay, CancellationToken cancellationToken) =>
		// 监听涉及 IPC，从线程池开始枚举，不占用调用方所在的 UI 线程。
		Task.Run(() => RunCoreAsync(services, platform, retryDelay, cancellationToken), CancellationToken.None);

	private static async Task RunCoreAsync(AppServices services, IPlatformServices platform, TimeSpan retryDelay, CancellationToken cancellationToken)
	{
		bool failing = false;
		try
		{
			while (true)
			{
				Exception? failure = null;
				try
				{
					await foreach (bool available in platform.WatchTrayAvailabilityAsync(cancellationToken).ConfigureAwait(false))
					{
						failing = false;
						await ApplyOnUiThreadAsync(services, available, cancellationToken).ConfigureAwait(false);
					}
					return;
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
				catch (Exception exception) { failure = exception; }

				if (!failing)
					services.Logger.Write(LogSource.Backend, "warn", "系统托盘状态监听失败，保留任务栏入口", exception: failure);
				failing = true;
				await ApplyOnUiThreadAsync(services, false, cancellationToken).ConfigureAwait(false);
				await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// 宿主退出时结束监听，不再访问窗口与已释放的服务。
		}
	}

	/// <summary>
	/// 在 UI 线程应用托盘状态。退出阶段 UI 线程可能正在同步等待清理，取消必须立即生效，
	/// 且不再执行已排队的调度；续延异步执行，保证监听循环回到线程池，D-Bus 查询不在 UI 线程上进行。
	/// </summary>
	private static Task ApplyOnUiThreadAsync(AppServices services, bool available, CancellationToken cancellationToken)
	{
		TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		CancellationTokenRegistration registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
		Dispatcher.UIThread.Post(() =>
		{
			try
			{
				if (!cancellationToken.IsCancellationRequested) ApplyAvailability(services, available);
				completion.TrySetResult();
			}
			catch (Exception exception) { completion.TrySetException(exception); }
			finally { registration.Dispose(); }
		});
		return completion.Task;
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
