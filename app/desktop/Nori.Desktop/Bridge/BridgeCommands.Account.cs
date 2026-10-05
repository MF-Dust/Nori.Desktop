using Nori.Core.Logging;

namespace Nori.Desktop.Bridge;

// 账户与云端同步命令的实现。分发在 BridgeCommands.cs 的 switch 里；原生调用方是
// 设置页「账户」分区（AccountSettingsPage），窗口入口另见托盘菜单、AccountWindow 与 CloudSyncWindow。
public sealed partial class BridgeCommands
{
	/// <summary>
	/// 退出登录。
	///
	/// 退完要让快照失效：设置页上那行「已登录 someone@example.com」是从快照读的，
	/// 不失效的话人点了退出、界面却还显示着登录中的那个账户。
	/// </summary>
	private async Task<object?> SignOutAsync(CancellationToken cancellationToken)
	{
		await _services.SignIn.SignOutAsync(cancellationToken);
		_services.Runtime?.InvalidateSnapshot();
		await OnUi(() => Run(Tray.TrayMenu.Refresh));
		return new {ok = true};
	}

	/// <summary>
	/// 跑一次云端同步动作，把结果转成设置页能直接显示的形状。
	///
	/// <c>conflict</c> 单独给出去：它不是错误而是并发的正常结果，调用方要据此把选择权
	/// 交给用户，而不是当成失败去重试。
	/// </summary>
	private async Task<object?> CloudSyncAsync(
		Func<CancellationToken, Task<Nori.Core.Cloud.CloudSyncResult>> work,
		CancellationToken cancellationToken)
	{
		Nori.Core.Cloud.CloudSyncResult result;
		try
		{
			result = await work(cancellationToken);
		}
		catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
		{
			/*
			 * 这一层**不抛**。
			 *
			 * 预期内的失败（没登录、网络不通、超限、冲突）CloudSyncService 本来就用返回值
			 * 表达，走到这里说明是没预料到的。但结果要显示在设置页那行只读文字上，而那行
			 * 只从快照取值 —— 抛出去的话它永远是空的，用户点了按钮什么也没发生。
			 */
			_services.Logger.Write(LogSource.Backend, "warn",
				$"云端同步失败: {Nori.Core.Security.SensitiveDataRedactor.ExceptionSummary(error)}");
			result = new Nori.Core.Cloud.CloudSyncResult {Message = "同步失败：" + error.GetType().Name};
		}

		string message = result.Message;
		// 静默少传是这类功能最难发现的故障：备份显示成功，换台机器才发现少了一半。
		if (result.Skipped.Count > 0) message += "\n未包含：" + string.Join("；", result.Skipped);
		if (_services.Runtime is {} runtime)
		{
			runtime.LastCloudSyncMessage = message;
			// 恢复会改配置与记忆，快照必须重建；备份不改本机，但版本号变了，那一行也要刷。
			runtime.InvalidateSnapshot();
		}
		// 令牌失效时 CloudSyncService 会就地清掉本机登录态。托盘那一条不读快照，
		// 只能单独喊一次 —— 不喊的话它会一直停在「退出登录」。
		await OnUi(() => Run(Tray.TrayMenu.Refresh));
		return new
		{
			ok = result.Ok,
			message,
			conflict = result.Conflict,
			remoteSavedAt = result.RemoteSavedAt,
		};
	}
}
