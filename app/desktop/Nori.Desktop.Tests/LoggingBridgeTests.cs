using System.Text.Json;
using Nori.Core.Logging;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;
using Nori.Desktop.Diagnostics;
using Nori.Core.Telemetry;
using Avalonia.Logging;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private bool _databaseReleasedByServices;
	private sealed class NoPumpLogContext : SynchronizationContext
	{
		public override void Post(SendOrPostCallback callback, object? state) { }
	}

	private sealed class DelayedLogTelemetry : ITelemetry
	{
		public bool IsAvailable => false;
		public bool IsEnabled => false;
		public bool Disposed { get; private set; }
		public void Configure(bool enabled) { }
		public void CaptureException(Exception exception, string operation, bool handled = true, bool terminal = false, IReadOnlyDictionary<string, string>? tags = null) { }
		public ITelemetryTransaction StartTransaction(string operation) => NoopTelemetry.Instance.StartTransaction(operation);
		public Task FlushAsync(TimeSpan timeout) => Task.Delay(30);
		public void Dispose() => Disposed = true;
	}

	[Fact]
	public async Task 服务释放不依赖退出中已停止调度的界面上下文()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		DelayedLogTelemetry telemetry = new(); fixture._services.Telemetry = telemetry;
		await Task.Run(() =>
		{
			SynchronizationContext.SetSynchronizationContext(new NoPumpLogContext());
			try { return fixture._services.DisposeAsync().AsTask(); }
			finally { SynchronizationContext.SetSynchronizationContext(null); }
		}).WaitAsync(TimeSpan.FromSeconds(3));
		Assert.True(telemetry.Disposed);
		Assert.True(fixture._services.Logger.GetStatus().Stopped);
		fixture._databaseReleasedByServices = true;
		await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture._services.ModelCatalog.GetIndexAsync());
	}

	[Fact]
	public void 框架日志丢弃控件内容并遵守临时记录级别()
	{
		AvaloniaLogSink sink = new(_services.Logger);
		Assert.False(sink.IsEnabled(LogEventLevel.Debug, "Binding"));
		Assert.False(sink.IsEnabled(LogEventLevel.Information, "Layout"));
		Assert.True(sink.IsEnabled(LogEventLevel.Warning, "Layout"));
		_services.Logger.SetMinimumLevel("debug");
		Assert.True(sink.IsEnabled(LogEventLevel.Debug, "Binding"));
		sink.Log(LogEventLevel.Error, "Binding", null, "聊天正文 {Value}", new object?[] { "凭据原文", new InvalidOperationException("敏感错误正文") });
		LogEntry entry = _services.Logger.RecentLogs().Last();
		Assert.Equal("Avalonia.Binding", entry.Category);
		Assert.Equal("System.InvalidOperationException", entry.ExceptionType);
		Assert.Equal("Avalonia Binding 诊断事件", entry.Message);
		Assert.DoesNotContain("正文", JsonSerializer.Serialize(entry));
	}

	[Fact]
	public async Task 日志管理应用有效级别并拒绝非法级别()
	{
		BridgeCommands commands = CreateCommands();
		await commands.InvokeAsync(new FakeBridgeSource("main"), "set_logging_level", Args(new { level = "trace" }));
		Assert.Equal("trace", _services.Logger.GetStatus().MinimumLevel);
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(new FakeBridgeSource("main"), "set_logging_level", Args(new { level = "invalid" })));
	}
}
