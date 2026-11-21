using Nori.Core.Automation;
using Nori.Desktop.Automation.Windows;

namespace Nori.Desktop.Automation.Desktop;

/// <summary>把现有 Windows 截图服务适配为桌面视觉截图源。</summary>
public sealed class WindowsDesktopVisionScreenshotSource : IDesktopVisionScreenshotSource
{
	private readonly WindowsWindowService _windows;
	private readonly WindowsScreenshotService _screenshots;

	/// <summary>创建 Windows 截图适配器。</summary>
	public WindowsDesktopVisionScreenshotSource(WindowsWindowService? windows = null, WindowsScreenshotService? screenshots = null)
	{
		_windows = windows ?? new WindowsWindowService();
		_screenshots = screenshots ?? new WindowsScreenshotService(_windows);
	}

	/// <inheritdoc />
	public async Task<DesktopVisionScreenshotResult> CaptureAsync(nint targetWindow, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		WindowsTargetValidationResult validation = _windows.ValidateTarget(targetWindow);
		if (validation.Rejection == WindowsTargetRejection.NotForeground)
			return DesktopVisionScreenshotResult.TargetNotForeground;
		if (!validation.IsValid) return DesktopVisionScreenshotResult.Failed;

		try
		{
			DesktopVisionScreenshotResult result = await Task.Run(() =>
			{
				bool captured = _screenshots.TryCapture(
					targetWindow,
					new WindowsScreenshotRequest(),
					out WindowsScreenshot? screenshot,
					out _);
				if (!captured || screenshot is null) return DesktopVisionScreenshotResult.Failed;
				string mimeType = screenshot.Format == WindowsScreenshotFormat.Png ? "image/png" : "image/jpeg";
				return DesktopVisionScreenshotResult.Succeeded(new DesktopVisionScreenshot(screenshot.Data, mimeType));
			}, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
			if (result.Status == DesktopVisionScreenshotStatus.Succeeded) return result;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			return DesktopVisionScreenshotResult.Failed;
		}

		WindowsTargetValidationResult afterCapture = _windows.ValidateTarget(targetWindow);
		return afterCapture.Rejection == WindowsTargetRejection.NotForeground
			? DesktopVisionScreenshotResult.TargetNotForeground
			: DesktopVisionScreenshotResult.Failed;
	}
}

/// <summary>把现有 Windows 输入服务适配为桌面视觉动作执行器。</summary>
public sealed class WindowsDesktopVisionActionExecutor : IDesktopVisionActionExecutor
{
	private readonly WindowsWindowService _windows;
	private readonly WindowsInputService _input;

	/// <summary>创建 Windows 输入适配器。</summary>
	public WindowsDesktopVisionActionExecutor(WindowsWindowService? windows = null, WindowsInputService? input = null)
	{
		_windows = windows ?? new WindowsWindowService();
		_input = input ?? new WindowsInputService(_windows);
	}

	/// <inheritdoc />
	public async Task<DesktopVisionActionResult> ExecuteAsync(
		nint targetWindow,
		AutomationAction action,
		AutomationPolicy policy,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		WindowsTargetValidationResult validation = _windows.ValidateTarget(targetWindow);
		if (validation.Rejection == WindowsTargetRejection.NotForeground)
			return DesktopVisionActionResult.TargetNotForeground;
		if (!validation.IsValid) return DesktopVisionActionResult.Failed;

		WindowsAutomationResult result;
		try
		{
			result = await Task.Run(() => _input.Execute(targetWindow, action, policy), cancellationToken)
				.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			return DesktopVisionActionResult.Failed;
		}

		if (result.Succeeded) return DesktopVisionActionResult.Succeeded;
		WindowsTargetValidationResult afterExecution = _windows.ValidateTarget(targetWindow);
		return afterExecution.Rejection == WindowsTargetRejection.NotForeground
			? DesktopVisionActionResult.TargetNotForeground
			: DesktopVisionActionResult.Failed;
	}
}
