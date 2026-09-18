using Nori.Core.Configuration;
using Nori.Core.Logging;
using Nori.Core.Notifications;
using Nori.Core.Agent;
using Nori.Core.Tools;

namespace Nori.Desktop.Runtime;

/// <summary>
/// 待决授权的第二个呈现面：系统通知。
///
/// **不取代应用内的授权卡片。** 通知可能被系统设置关掉、被专注模式折叠、快捷方式
/// 可能建不出来 —— 授权是一条不能只剩一个入口的路。两条路解同一个
/// <c>TaskCompletionSource</c>，谁先到算谁的（<c>TryRemove</c> + <c>TrySetResult</c>
/// 本来就保证了这一点）。
///
/// 注册要往用户机器上留两样东西（开始菜单快捷方式、HKCU 的 COM 激活器），所以
/// **懒到第一次真的要弹才做**：一个从来不触发授权的用户不该在开始菜单里多出一项。
/// </summary>
public sealed partial class AppRuntime
{
	/// <summary>用户有没有把这条关掉。默认开。</summary>
	private bool ToastApprovalsEnabled =>
		Services.Config.GetBoolOr(ConfigStore.KeyToastApprovals, true);

	/// <summary>
	/// 换掉呈现层。**只给测试用。**
	///
	/// 真的那条要起 COM、要在开始菜单建快捷方式、要写注册表；把它跑进单元测试里
	/// 会在跑测试的机器上留下东西，而且 CI 的会话里根本弹不出来。
	/// </summary>
	internal void UseNotifierForTests(INativeNotifier notifier)
	{
		lock (_notifierGate)
		{
			_notifier = notifier;
			_notifierTried = true;
		}
	}

	/// <summary>
	/// 弹一条。任何一步失败都只记一行日志 —— 通知发不出去不该让工具调用失败。
	/// </summary>
	private void ShowApprovalNotice(ToolApprovalRequest request)
	{
		if (!ToastApprovalsEnabled) return;
		INativeNotifier notifier = EnsureNotifier();
		if (!notifier.Available) return;

		notifier.Show(new ApprovalNotice
		{
			RequestId = request.RequestId,
			ToolName = request.ToolName,
			Description = request.Description,
			PermissionLevel = request.PermissionLevel,
			ArgumentSummary = SummarizeArguments(request),
		});
	}

	/// <summary>
	/// 参数压成一行给通知用。
	///
	/// 这段文本是**模型写的**，可能很长、可能带换行、可能带引号 —— 转义和截断由
	/// <see cref="ApprovalToastXml"/> 负责，这里只做「取哪一段」。
	/// </summary>
	private static string? SummarizeArguments(ToolApprovalRequest request) =>
		request.Arguments?.ToJsonString(new System.Text.Json.JsonSerializerOptions
		{
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		});

	/// <summary>
	/// 第一次要用时才建。
	///
	/// 失败只试一次：注册失败通常是环境性的（比如策略禁掉了写开始菜单），
	/// 每次授权都重试一遍除了拖慢确认没有别的作用。
	/// </summary>
	private INativeNotifier EnsureNotifier()
	{
		lock (_notifierGate)
		{
			if (_notifierTried) return _notifier;
			_notifierTried = true;
			if (!OperatingSystem.IsWindows()) return _notifier;

			try
			{
				_notifier = StartWindowsNotifier();
			}
			catch (Exception failure)
			{
				Services.Logger.Write(LogSource.Backend, "warn",
					$"系统通知初始化失败：{failure.GetType().Name}");
			}
			return _notifier;
		}
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private INativeNotifier StartWindowsNotifier()
	{
		string executable = Notifications.Windows.ToastRegistrar.ResolveEntrypoint(Services.Paths.PackageRoot);
		string stamp = ToastRegistrationStamp.Compute(
			executable,
			Notifications.Windows.ToastRegistrar.AppUserModelId,
			Notifications.Windows.ToastRegistrar.ActivatorClsid);

		// 指纹没变就不重写：每次启动都重建快捷方式会让它在开始菜单的
		// 「最近添加」里反复冒头。
		if (ToastRegistrationStamp.NeedsWrite(
			Services.Config.GetStringOr(ToastRegistrationStamp.ConfigKey, ""), stamp))
		{
			Notifications.Windows.ToastRegistrar.Write(executable);
			Services.Config.Set(ToastRegistrationStamp.ConfigKey, new ConfigValue.Text(stamp));
			Services.Logger.Write(LogSource.Backend, "info", "已注册系统通知入口");
		}

		Notifications.Windows.ToastActivator.Register(OnToastActivated);

		Notifications.Windows.WindowsToastNotifier notifier = new(
			() => Services.Config.GetStringOr(ConfigStore.KeyLanguage, "zh-CN")
				.StartsWith("en", StringComparison.OrdinalIgnoreCase),
			(level, message) => Services.Logger.Write(LogSource.Backend, level, message));
		return notifier.TryStart() ? notifier : NullNativeNotifier.Instance;
	}

	/// <summary>
	/// 用户在通知上点了什么。
	///
	/// 这条回调跑在 COM 的线程上，而且那串参数**出过进程** —— 解析认不出来一律忽略，
	/// 绝不回落到允许（<see cref="ToastActivation.Parse"/> 里也有同一道）。
	/// 找不到对应的待决授权同样忽略：系统可能把 exe 拉起来送一条早已超时的点击。
	/// </summary>
	private void OnToastActivated(string arguments)
	{
		(ToastAction action, string? requestId) = ToastActivation.Parse(arguments);
		if (requestId is null) return;

		switch (action)
		{
			case ToastAction.Allow:
			case ToastAction.Deny:
				RespondApprovalFromNotification(requestId, action == ToastAction.Allow);
				break;
			case ToastAction.Open:
				// 点正文不是决定，只是把卡片带到前台。
				Services.Windows?.Show(Windows.WindowLabels.Main);
				break;
		}
	}

	/// <summary>
	/// 从系统通知回收一条授权。
	///
	/// 与 <see cref="RespondApproval"/> 的区别只在**不校验来源窗口** —— 通知不属于任何
	/// 一个 bridge 窗口。其余判定一条不少：过期、取消、以及只有真的按了允许才记住。
	/// 凭据是那个猜不出来的 <c>approval-{Guid:N}</c>。
	/// </summary>
	internal bool RespondApprovalFromNotification(string requestId, bool approved)
	{
		lock (_approvalGate)
		{
			if (!_approvals.TryGetValue(requestId, out PendingApproval? approval)) return false;
			if (approval.DeadlineUtc <= DateTimeOffset.UtcNow)
			{
				FinishApproval(approval, false, "timeout");
				return false;
			}
			if (approval.CancellationToken.IsCancellationRequested)
			{
				FinishApproval(approval, false, "cancelled");
				return false;
			}
			Services.Logger.Write(LogSource.Backend, "info",
				$"系统通知授权：{approval.ToolName} {(approved ? "允许" : "拒绝")}");
			return FinishApproval(approval, approved, approved ? "approved" : "denied");
		}
	}

	/// <summary>
	/// 设置变了之后对齐一次。
	///
	/// 开着就什么都不做（真正的注册懒到第一次要弹时）；关掉就把留在机器上的两样东西
	/// 清干净 —— 用户关的是「别在我机器上留东西」，只停止发送等于留了一半。
	/// </summary>
	internal void SyncNotificationRegistration()
	{
		if (ToastApprovalsEnabled) return;
		ForgetNotificationRegistration();
	}

	/// <summary>用户在设置里关掉这条时，把留在机器上的痕迹清掉。</summary>
	internal void ForgetNotificationRegistration()
	{
		lock (_notifierGate)
		{
			DisposeNotifierCore();
			if (!OperatingSystem.IsWindows()) return;
			Notifications.Windows.ToastRegistrar.Remove();
			Services.Config.Delete(ToastRegistrationStamp.ConfigKey);
			Services.Logger.Write(LogSource.Backend, "info", "已清除系统通知入口");
		}
	}

	private void DisposeNotifier()
	{
		lock (_notifierGate) DisposeNotifierCore();
	}

	private void DisposeNotifierCore()
	{
		(_notifier as IDisposable)?.Dispose();
		_notifier = NullNativeNotifier.Instance;
		_notifierTried = false;
		if (OperatingSystem.IsWindows()) Notifications.Windows.ToastActivator.Revoke();
	}
}
