using Nori.Desktop.Automation;

namespace Nori.Desktop.Bridge;

/// <summary>从应用服务容器取得自动化领域窄接口，避免新调用方继续依赖整个 AutomationRuntime。</summary>
public static class AppServicesAutomationExtensions
{
	public static DesktopAutomationHost DesktopAutomation(this AppServices services) =>
		new(RequireRuntime(services));

	public static AutomationApprovalHost AutomationApprovals(this AppServices services) =>
		new(RequireRuntime(services));

	private static AutomationRuntime RequireRuntime(AppServices services)
	{
		ArgumentNullException.ThrowIfNull(services);
		return services.Automation ?? throw new InvalidOperationException("自动化运行时未装配");
	}
}
