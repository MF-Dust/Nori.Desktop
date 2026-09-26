using Nori.Core.Agent;
using Nori.Core.Automation;
using Nori.Core.Logging;
using Nori.Core.Tools;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Runtime;

public sealed partial class AppRuntime
{
	// ===================================================================
	// 工具授权
	// ===================================================================

	/// <summary>
	/// 授权档位。运行期只有这一份 —— 「本轮记住」的记忆挂在它身上，换一个实例等于失忆。
	/// </summary>
	public ToolPermissionPolicy Permissions { get; } = new();

	/// <summary>配置里存的档位（不看是否到期）。</summary>
	public PermissionGear StoredGear =>
		ToolPermissionPolicy.Parse(Services.Config.GetStringOr(ToolPermissionPolicy.KeyGear, ""));

	/// <summary>完全放行的到期时刻；没存过则为 null。</summary>
	public DateTimeOffset? BypassUntil =>
		ToolPermissionPolicy.ParseDeadline(Services.Config.GetStringOr(ToolPermissionPolicy.KeyBypassUntil, ""));

	/// <summary>真正生效的档位：存的是完全放行但已过期时，按完全授权走。</summary>
	public PermissionGear EffectiveGear =>
		ToolPermissionPolicy.Effective(StoredGear, BypassUntil, DateTimeOffset.UtcNow);

	internal async Task<bool> RequestApprovalAsync(IBridgeSource source, string sessionId, ToolApprovalRequest request, CancellationToken cancellationToken)
	{
		// 安全模式排在档位之前：它是「什么都不做」，不是「不用问」。把 bypass 打开也不能
		// 让安全模式放行 —— 那会让一个用来收拾残局的开关变成最宽的那一档。
		if (Services.SafeMode) return false;

		/* ── 档位 ──────────────────────────────────────────────────────────
		 * 判定在**发事件之前**。放在之后（发了卡片再自动点掉）会让界面闪一下
		 * 又消失，用户以为自己看漏了什么。 */
		if (Permissions.Decide(EffectiveGear, sessionId, request.ToolName, request.PermissionLevel)
			== PermissionDecision.Allow)
		{
			// 自动放行也要留痕：出事时要答得出「她什么时候做的、按的哪一档」。
			Services.Logger.Write(LogSource.Backend, "info",
				$"按档位自动放行：{request.ToolName}（{ToolPermissionPolicy.Format(EffectiveGear)}）");
			return true;
		}

		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken, request.CancellationToken, _lifetimeCts.Token,
			source is INativeChatSource native ? native.LifetimeToken : CancellationToken.None);
		PendingApproval approval = new(request.RequestId, request.ToolName, source, sessionId,
			request.DeadlineUtc ?? DateTimeOffset.UtcNow.AddSeconds(AgentEngine.CallTimeoutSeconds), linked.Token);
		lock (_approvalGate)
		{
			if (linked.IsCancellationRequested || approval.DeadlineUtc <= DateTimeOffset.UtcNow
				|| !_approvals.TryAdd(request.RequestId, approval)) return false;
			approval.ArmTimeout(() => ExpireApproval(approval));
			PostAgentEvent(source, new
			{
				type = "approval-request", sessionId, requestId = request.RequestId,
				toolName = request.ToolName, arguments = request.Arguments,
				description = request.Description, permissionLevel = request.PermissionLevel,
				category = request.Category, deadlineUtc = approval.DeadlineUtc,
			});
		}
		// 通知在锁外发：它要起 COM、要建快捷方式，不该把授权锁按住那么久。
		ShowApprovalNotice(request);
		// 工具轮次自身先超时或退出时，立即撤销授权卡，而不是留下一张已失效的可批准卡片。
		using CancellationTokenRegistration cancelled = linked.Token.Register(() =>
		{
			lock (_approvalGate) FinishApproval(approval, false, "cancelled");
		});
		return await approval.Tcs.Task.ConfigureAwait(false);
	}

	private void ExpireApproval(PendingApproval approval)
	{
		lock (_approvalGate)
		{
			if (!_approvals.TryGetValue(approval.RequestId, out PendingApproval? current) || !ReferenceEquals(current, approval)) return;
			// 延期前已排队的旧定时器回调不能让新期限提前失效。
			if (approval.DeadlineUtc > DateTimeOffset.UtcNow) { approval.RearmTimeout(); return; }
			FinishApproval(approval, false, "timeout");
		}
	}

	private bool FinishApproval(PendingApproval approval, bool approved, string reason)
	{
		if (!_approvals.TryRemove(new KeyValuePair<string, PendingApproval>(approval.RequestId, approval))) return false;
		approval.Dispose();
		// 无论从哪条路结束的，屏幕上那条都要收掉 —— 留一张点了没反应的卡片比不弹更糟。
		HideApprovalNotice(approval.RequestId);
		// 只有用户**真的按了允许**才记。超时、取消、拒绝都不是同意 —— 把它们也记进来，
		// 等于一次没人看见的超时换来后面整轮的静默放行。
		if (approved) Permissions.Remember(approval.SessionId, approval.ToolName);
		PostAgentEvent(approval.Source, new
		{
			type = "approval-result", sessionId = approval.SessionId, requestId = approval.RequestId, approved, reason,
		});
		return approval.Tcs.TrySetResult(approved);
	}

	/// <summary>延长原始来源的待决授权，返回不超过工具实际期限的服务端截止时间。</summary>
	public DateTimeOffset ExtendApproval(IBridgeSource source, string requestId)
	{
		lock (_approvalGate)
		{
			if (!_approvals.TryGetValue(requestId, out PendingApproval? approval) || !IsSameSource(source, approval.Source))
				throw new InvalidOperationException("授权请求不存在或不属于当前窗口");
			if (approval.DeadlineUtc <= DateTimeOffset.UtcNow)
			{
				FinishApproval(approval, false, "timeout");
				throw new InvalidOperationException("授权请求已超时");
			}
			if (approval.CancellationToken.IsCancellationRequested)
			{
				FinishApproval(approval, false, "cancelled");
				throw new InvalidOperationException("授权请求已取消");
			}
			if (source is INativeChatSource && !source.IsVisible)
				throw new InvalidOperationException("对话窗口不可见，无法延长工具授权");
			approval.Extend();
			PostAgentEvent(approval.Source, new
			{
				type = "approval-extended", sessionId = approval.SessionId, requestId, deadlineUtc = approval.DeadlineUtc,
			});
			return approval.DeadlineUtc;
		}
	}

	/// <summary>等待桌面或浏览器高风险动作的用户决定；未装配或取消时一律不自动放行。</summary>
	internal async Task<AutomationApprovalDecision> RequestAutomationApprovalAsync(
		AutomationApprovalRequest request,
		CancellationToken cancellationToken)
	{
		if (Services.SafeMode)
		{
			Services.Automation?.RecordApprovalOutcome(request, AutomationApprovalOutcome.Denied);
			return AutomationApprovalDecision.Create(request, AutomationApprovalOutcome.Denied, DateTimeOffset.UtcNow);
		}

		/* ── 档位 ──────────────────────────────────────────────────────────
		 * 接管鼠标键盘按 **dangerous** 算，不按 confirm：
		 *
		 * 它和「改一个文件」不是一个量级 —— 动的是你正在用的那套输入设备，出错时
		 * 你连夺回控制的动作都要和她抢。所以「完全授权」这一档仍然逐次问，只有
		 * 「完全放行」才免掉。这也正是那两档在今天唯一真实的差别：内置工具目前
		 * 没有一个注册成 dangerous。
		 *
		 * 自动化自己的那几个开关（allowPointer / allowKeyboard / allowScroll）在这
		 * 之外，档位放宽不了它们 —— 没打开的东西，哪一档都动不了。 */
		if (Permissions.Decide(EffectiveGear, request.RequestId.ToString("D"), "automation", "dangerous")
			== PermissionDecision.Allow)
		{
			Services.Logger.Write(LogSource.Backend, "info",
				$"按档位自动放行自动化：{string.Join('/', request.ActionKinds)}（{ToolPermissionPolicy.Format(EffectiveGear)}）");
			Services.Automation?.RecordApprovalOutcome(request, AutomationApprovalOutcome.Approved);
			return AutomationApprovalDecision.Create(request, AutomationApprovalOutcome.Approved, DateTimeOffset.UtcNow);
		}

		TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
		PendingDesktopApproval approval = new(request, tcs);
		if (!_desktopApprovals.TryAdd(request.RequestId.ToString("D"), approval))
		{
			Services.Automation?.RecordApprovalOutcome(request, AutomationApprovalOutcome.Denied);
			return AutomationApprovalDecision.Create(request, AutomationApprovalOutcome.Denied, DateTimeOffset.UtcNow);
		}

		Services.Automation?.SetAutomationApproval(request);
		approval.ArmTimeout(ApprovalTimeoutSeconds, () =>
		{
			if (_desktopApprovals.TryRemove(request.RequestId.ToString("D"), out PendingDesktopApproval? expired))
			{
				expired.Tcs.TrySetResult(false);
				expired.Dispose();
				Services.Automation?.ClearAutomationApproval(request.RequestId);
				Services.Automation?.RecordApprovalOutcome(request, AutomationApprovalOutcome.Expired);
			}
		});
		try
		{
			bool approved = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
			return AutomationApprovalDecision.Create(
				request,
				approved ? AutomationApprovalOutcome.Approved : AutomationApprovalOutcome.Denied,
				DateTimeOffset.UtcNow);
		}
		catch (OperationCanceledException)
		{
			Services.Automation?.RecordApprovalCancellation(request);
			throw;
		}
		finally
		{
			if (_desktopApprovals.TryRemove(request.RequestId.ToString("D"), out PendingDesktopApproval? removed))
			{
				removed.Dispose();
				Services.Automation?.ClearAutomationApproval(request.RequestId);
			}
		}
	}

	/// <summary>
	/// 回传授权决定; 只允许原始窗口响应。原生设置窗口可在明确可信上下文中
	/// 响应主窗口发起的自动化审批，未匹配的请求 fail-closed 忽略。
	/// </summary>
	public bool RespondApproval(IBridgeSource source, string requestId, bool approved)
	{
		bool allowNativeSettings = source is INativeSettingsSource;
		lock (_approvalGate)
		{
			if (_approvals.TryGetValue(requestId, out PendingApproval? approval)
				&& (IsSameSource(source, approval.Source)
					|| (allowNativeSettings && approval.Source is not INativeChatSource && approval.Source.Label == WindowLabels.Main)))
			{
				if (approval.DeadlineUtc <= DateTimeOffset.UtcNow)
				{
					FinishApproval(approval, false, "timeout");
					return false;
				}
				// 可见性及取消信号可能在命令入口校验后、等待授权锁期间改变。
				if (approval.CancellationToken.IsCancellationRequested)
				{
					FinishApproval(approval, false, "cancelled");
					return false;
				}
				if (approved && source is INativeChatSource && !source.IsVisible)
					throw new InvalidOperationException("对话窗口不可见，无法批准工具执行");
				return FinishApproval(approval, approved, approved ? "approved" : "denied");
			}
		}

		if (source.Label != WindowLabels.Main && !allowNativeSettings
			|| !_desktopApprovals.TryGetValue(requestId, out PendingDesktopApproval? desktopApproval)) return false;
		if (!_desktopApprovals.TryRemove(new KeyValuePair<string, PendingDesktopApproval>(requestId, desktopApproval))) return false;
		desktopApproval.Dispose();
		Services.Automation?.ClearAutomationApproval(desktopApproval.Request.RequestId);
		Services.Automation?.RecordApprovalOutcome(
			desktopApproval.Request,
			approved ? AutomationApprovalOutcome.Approved : AutomationApprovalOutcome.Denied);
		return desktopApproval.Tcs.TrySetResult(approved);
	}
}
