using Avalonia.Controls;
using Avalonia.Platform;
using Nori.Desktop.Diagnostics;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Tests;

/// <summary>NORI-31 / NORI-32：GPU 故障后的退避与渲染表面重建。</summary>
public sealed class GpuRecoveryBackoffTests
{
	[Fact]
	public void 连续故障逐级拉长等待并封顶()
	{
		GpuRecoveryBackoff backoff = new();
		TimeSpan now = TimeSpan.FromMinutes(10);
		List<TimeSpan> delays = [];
		for (int attempt = 0; attempt < GpuRecoveryBackoff.Delays.Length + 2; attempt++)
		{
			delays.Add(backoff.NextDelay(now));
			now += delays[^1];
		}

		Assert.Equal(GpuRecoveryBackoff.Delays, delays.Take(GpuRecoveryBackoff.Delays.Length));
		Assert.All(delays.Skip(GpuRecoveryBackoff.Delays.Length), delay => Assert.Equal(GpuRecoveryBackoff.Delays[^1], delay));
	}

	[Fact]
	public void 稳定超过窗口后重新从最短等待开始()
	{
		GpuRecoveryBackoff backoff = new();
		TimeSpan now = TimeSpan.FromMinutes(10);
		Assert.False(backoff.IsRecovering(now));
		backoff.NextDelay(now);
		backoff.NextDelay(now + TimeSpan.FromSeconds(1));
		Assert.True(backoff.IsRecovering(now + TimeSpan.FromSeconds(30)));

		TimeSpan stable = now + TimeSpan.FromSeconds(1) + GpuRecoveryBackoff.StableWindow;

		Assert.False(backoff.IsRecovering(stable));
		Assert.Equal(GpuRecoveryBackoff.Delays[0], backoff.NextDelay(stable));
	}
}

public partial class BridgeCommandsTests
{
	[Fact]
	public Task GPU故障暂停帧请求并原位重建渲染表面() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		PetGlControl control = new(new PetRuntime(fixture._services, previewMode: true));
		Border sibling = new();
		Grid host = new() { Children = { control, sibling } };
		Window window = new() { Content = host, Width = 160, Height = 160 };
		window.Show();
		CrashReporter.ResetGpuFailureReports();
		try
		{
			int detached = 0, attached = 0;
			control.DetachedFromVisualTree += (_, _) => detached++;
			control.AttachedToVisualTree += (_, _) => attached++;

			Assert.True(CrashReporter.TryHandleGpuFailure(new PlatformGraphicsContextLostException(), "gpu_render", requestRecovery: true));
			Assert.True(control.GpuRecoveryPending);
			// 挂起期间 Avalonia 自身触发的失败不叠加重建，也不重置退避计时。
			Assert.True(CrashReporter.TryHandleGpuFailure(new PlatformGraphicsContextLostException(), "gpu_render", requestRecovery: true));

			control.RecoverFromGpuFailureNow();

			Assert.False(control.GpuRecoveryPending);
			Assert.Equal(1, detached);
			Assert.Equal(1, attached);
			Assert.Equal(0, host.Children.IndexOf(control));
			Assert.Same(sibling, host.Children[1]);
			Assert.Same(window, TopLevel.GetTopLevel(control));

			// 重建后仍保持订阅，下一次故障照常安排恢复。
			Assert.True(CrashReporter.TryHandleGpuFailure(new PlatformGraphicsContextLostException(), "gpu_render", requestRecovery: true));
			Assert.True(control.GpuRecoveryPending);
		}
		finally
		{
			window.Close();
			CrashReporter.ResetGpuFailureReports();
		}
		return Task.CompletedTask;
	});

	[Fact]
	public Task 移出界面的渲染控件取消并不再响应GPU故障() => WithSettingsUiAsync(() =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		PetGlControl control = new(new PetRuntime(fixture._services, previewMode: true));
		Window window = new() { Content = control, Width = 160, Height = 160 };
		window.Show();
		CrashReporter.ResetGpuFailureReports();
		try
		{
			Assert.True(CrashReporter.TryHandleGpuFailure(new PlatformGraphicsContextLostException(), "gpu_render", requestRecovery: true));
			Assert.True(control.GpuRecoveryPending);

			window.Content = null;

			Assert.False(control.GpuRecoveryPending);
			Assert.True(CrashReporter.TryHandleGpuFailure(new PlatformGraphicsContextLostException(), "gpu_render", requestRecovery: true));
			Assert.False(control.GpuRecoveryPending);
		}
		finally
		{
			window.Close();
			CrashReporter.ResetGpuFailureReports();
		}
		return Task.CompletedTask;
	});
}
