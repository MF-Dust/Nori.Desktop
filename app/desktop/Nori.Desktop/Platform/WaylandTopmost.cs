using System.Net.Sockets;
using System.Text.Json;

namespace Nori.Desktop.Platform;

internal static class WaylandTopmost
{
	private static readonly string? HyprlandSignature = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
	private static readonly string? SwaySocket = Environment.GetEnvironmentVariable("SWAYSOCK");
	private static readonly bool IsPlasma = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?.Split(':')
		.Any(desktop => desktop.Equals("KDE", StringComparison.OrdinalIgnoreCase) || desktop.Equals("Plasma", StringComparison.OrdinalIgnoreCase)) == true;
	private static int _running;

	internal static bool HasKnownCompositor => !string.IsNullOrWhiteSpace(HyprlandSignature) || !string.IsNullOrWhiteSpace(SwaySocket) || IsPlasma;

	internal static void Request(string title, Action<Exception> onFailure)
	{
		if (!string.IsNullOrWhiteSpace(HyprlandSignature))
			RequestIpc(token => HyprlandTopmost.TryPinAsync(
				HyprlandTopmost.ResolveSocketPath(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), HyprlandSignature),
				Environment.ProcessId, title, token), onFailure);
		else if (!string.IsNullOrWhiteSpace(SwaySocket))
			RequestIpc(token => SwayTopmost.TryStickAsync(SwaySocket, Environment.ProcessId, title, token), onFailure);
		else if (IsPlasma)
			KdePlasmaTopmost.TryStart(onFailure);
	}

	private static void RequestIpc(Func<CancellationToken, Task<bool>> apply, Action<Exception> onFailure)
	{
		if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
		_ = Task.Run(async () =>
		{
			using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(2));
			try
			{
				// 窗口显示事件可能早于合成器登记顶层窗口，短暂等待后重新查询。
				while (!await apply(deadline.Token)) await Task.Delay(50, deadline.Token);
			}
			catch (OperationCanceledException) when (deadline.IsCancellationRequested)
			{
				// 合成器未及时报告窗口或回复，保持当前窗口状态。
			}
			catch (Exception exception) when (exception is SocketException or IOException or JsonException or ArgumentOutOfRangeException)
			{
				onFailure(exception);
			}
			finally
			{
				Interlocked.Exchange(ref _running, 0);
			}
		});
	}
}
