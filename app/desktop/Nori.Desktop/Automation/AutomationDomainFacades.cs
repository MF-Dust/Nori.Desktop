using Nori.Core.Automation;
using Nori.Desktop.Automation.Desktop;

namespace Nori.Desktop.Automation;

/// <summary>
/// Desktop Vision Automation 的窄接口。
/// </summary>
public sealed class DesktopAutomationHost(AutomationRuntime runtime)
{
	private readonly AutomationRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

	public AutomationCapabilitiesSnapshot GetCapabilities() => _runtime.GetCapabilities();

	public AutomationVisionProbeSnapshot ProbeVision() => _runtime.ProbeVision();

	public IReadOnlyList<AutomationDesktopWindowSnapshot> ListWindows() => _runtime.ListDesktopWindows();

	public AutomationDesktopTaskStartSnapshot StartTask(string task, string targetToken) =>
		_runtime.StartDesktopTask(task, targetToken);

	public bool StopTask(Guid taskId) => _runtime.StopDesktopTask(taskId);
}

/// <summary>
/// 自动化审批与审计结果登记的窄接口。
/// </summary>
public sealed class AutomationApprovalHost(AutomationRuntime runtime)
{
	private readonly AutomationRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

	public void Set(AutomationApprovalRequest request) => _runtime.SetAutomationApproval(request);

	public void Clear(Guid requestId) => _runtime.ClearAutomationApproval(requestId);

	public void RecordOutcome(AutomationApprovalRequest request, AutomationApprovalOutcome outcome) =>
		_runtime.RecordApprovalOutcome(request, outcome);

	public void RecordCancellation(AutomationApprovalRequest request) =>
		_runtime.RecordApprovalCancellation(request);
}
