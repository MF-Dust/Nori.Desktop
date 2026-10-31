using Avalonia.Platform;
using Nori.Core.Telemetry;
using Nori.Desktop.Diagnostics;

namespace Nori.Desktop.Tests;

/// <summary>NORI-31 / NORI-32：GPU 失败不进崩溃窗，按类别只上报一次 warning，并请求控件重建。</summary>
public sealed class GpuFailureHandlingTests : IDisposable
{
	private readonly RecordingTelemetry _telemetry = new();
	private readonly List<GpuFailure> _recoveries = [];

	public GpuFailureHandlingTests()
	{
		CrashReporter.ResetGpuFailureReports();
		CrashReporter.AttachTelemetry(_telemetry);
		CrashReporter.GpuRenderFailureHandled += _recoveries.Add;
	}

	public void Dispose()
	{
		CrashReporter.GpuRenderFailureHandled -= _recoveries.Add;
		CrashReporter.AttachTelemetry(NoopTelemetry.Instance);
		CrashReporter.ResetGpuFailureReports();
	}

	[Fact]
	public void UI线程设备丢失每类只上报一次warning且每次都请求重建()
	{
		Assert.True(CrashReporter.TryHandleGpuFailure(new PlatformGraphicsContextLostException(), "gpu_render", requestRecovery: true));
		Assert.True(CrashReporter.TryHandleGpuFailure(new PlatformGraphicsContextLostException(), "gpu_render", requestRecovery: true));

		(string Operation, IReadOnlyDictionary<string, string>? Tags) warning = Assert.Single(_telemetry.Warnings);
		Assert.Equal("gpu_render", warning.Operation);
		Assert.Equal(GpuFailure.DeviceLost, warning.Tags?["exception_kind"]);
		Assert.Empty(_telemetry.Exceptions);
		Assert.Equal(2, _recoveries.Count);
	}

	[Fact]
	public void 未观察任务里的设备丢失只降级上报不请求重建()
	{
		AggregateException unobserved = new(new PlatformGraphicsContextLostException());

		Assert.True(CrashReporter.TryHandleGpuFailure(unobserved, "gpu_unobserved_task", requestRecovery: false));

		Assert.Equal("gpu_unobserved_task", Assert.Single(_telemetry.Warnings).Operation);
		Assert.Empty(_recoveries);
	}

	[Fact]
	public void 非GPU异常交回原有崩溃兜底()
	{
		Assert.False(CrashReporter.TryHandleGpuFailure(new InvalidOperationException("业务错误"), "gpu_render", requestRecovery: true));

		Assert.Empty(_telemetry.Warnings);
		Assert.Empty(_recoveries);
	}

	[Fact]
	public void 恢复订阅者异常不会让兜底失败()
	{
		Action<GpuFailure> broken = _ => throw new InvalidOperationException("订阅者失败");
		CrashReporter.GpuRenderFailureHandled += broken;
		try
		{
			Assert.True(CrashReporter.TryHandleGpuFailure(new PlatformGraphicsContextLostException(), "gpu_render", requestRecovery: true));
		}
		finally
		{
			CrashReporter.GpuRenderFailureHandled -= broken;
		}
	}

	private sealed class RecordingTelemetry : ITelemetry
	{
		public List<(string Operation, IReadOnlyDictionary<string, string>? Tags)> Warnings { get; } = [];
		public List<string> Exceptions { get; } = [];
		public bool IsAvailable => true;
		public bool IsEnabled => true;
		public void Configure(bool enabled) { }

		public void CaptureException(Exception exception, string operation, bool handled = true, bool terminal = false,
			IReadOnlyDictionary<string, string>? tags = null) => Exceptions.Add(operation);

		public void CaptureWarning(Exception exception, string operation, IReadOnlyDictionary<string, string>? tags = null) =>
			Warnings.Add((operation, tags));

		public ITelemetryTransaction StartTransaction(string operation) => NoopTelemetry.Instance.StartTransaction(operation);
		public Task FlushAsync(TimeSpan timeout) => Task.CompletedTask;
		public void Dispose() { }
	}
}
