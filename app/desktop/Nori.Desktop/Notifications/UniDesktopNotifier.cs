using Nori.Core.Notifications;
using Nori.Core.Platform;

namespace Nori.Desktop.Notifications;

/// <summary>Linux 的只读授权提醒；UDA C ABI 没有通知撤销或动作回调，审批仍在应用内完成。</summary>
internal sealed class UniDesktopNotifier(IDesktopIntegration integration, Action<Exception> onFailure) : INativeNotifier, IDisposable
{
	private int _disposed;
	public bool Available => Volatile.Read(ref _disposed) == 0 && integration.IsAvailable;

	public void Show(ApprovalNotice notice) => _ = SendAsync(notice);

	internal Task SendAsync(ApprovalNotice notice) => Task.Run(() =>
	{
		try
		{
			if (!Available) return;
			string tool = System.Security.SecurityElement.Escape(notice.ToolName);
			integration.Notify("Nori 有待确认的操作", $"工具：{tool}\n请打开 Nori 主界面确认。操作可能已过期，请以应用内状态为准。");
		}
		catch (Exception failure)
		{
			try { onFailure(failure); }
			catch { /* 辅助通知与日志故障都不能阻断审批。 */ }
		}
	});

	public void Hide(string requestId) { }
	public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
