using System.Text.Json;
using Nori.Core.Logging;
using Nori.Desktop.Bridge;
using Nori.Desktop.Settings;
using Nori.Desktop.Settings.Pages;
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
	}

	[Fact]
	public void 框架日志丢弃控件内容并遵守临时记录级别()
	{
		AvaloniaLogSink sink = new(_services.Logger);
		Assert.False(sink.IsEnabled(LogEventLevel.Debug, "Binding"));
		_services.Logger.SetMinimumLevel("debug");
		Assert.True(sink.IsEnabled(LogEventLevel.Debug, "Binding"));
		sink.Log(LogEventLevel.Error, "Binding", null, "聊天正文 {Value}", new object?[] { "凭据原文", new InvalidOperationException("敏感错误正文") });
		LogEntry entry = _services.Logger.RecentLogs().Last();
		Assert.Equal("Avalonia.Binding", entry.Category);
		Assert.Equal("System.InvalidOperationException", entry.ExceptionType);
		Assert.DoesNotContain("正文", JsonSerializer.Serialize(entry));
	}

	[Fact]
	public async Task 前端日志不接受正文与伪造来源()
	{
		BridgeCommands commands = CreateCommands();
		foreach (string eventId in new[] { "window.error", "user_private_content", "", "diagnostics.test" })
		{
			int before = _services.Logger.RecentLogs().Count;
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(new FakeBridgeSource("first-run"), "write_log", Args(new
			{ level = "error", message = "聊天正文和请求正文", eventId, source = "backend", category = "私人内容", windowLabel = "settings", errorType = "私密错误名" })));
			Assert.Equal(before, _services.Logger.RecentLogs().Count);
		}
		int legacyBefore = _services.Logger.RecentLogs().Count;
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
			new FakeBridgeSource("main"), "write_log", Args(new { level = "warn", message = "旧参数正文" })));
		Assert.Equal(legacyBefore, _services.Logger.RecentLogs().Count);

		await commands.InvokeAsync(new FakeBridgeSource("first-run"), "write_log", Args(new
		{ level = "error", message = "聊天正文和请求正文", eventId = "audio.error", source = "backend", category = "私人内容", windowLabel = "settings", errorType = "TypeError" }));
		LogEntry entry = _services.Logger.RecentLogs().Last();
		Assert.Equal(LogSource.Frontend, entry.Source);
		Assert.Equal("first-run", entry.WindowLabel);
		Assert.Equal("Frontend", entry.Category);
		Assert.Equal("audio.error", entry.EventId);
		Assert.Equal("前端音频操作失败：TypeError", entry.Message);
		string json = JsonSerializer.Serialize(entry);
		Assert.DoesNotContain("正文", json);
		Assert.DoesNotContain("私人", json);
		Assert.DoesNotContain("user_private", json);
	}

	[Fact]
	public async Task 日志管理只允许宿主授权的管理来源()
	{
		BridgeCommands commands = CreateCommands();
		foreach (string source in new[] { "first-run", "init", "plugin-test" })
		foreach (string command in new[] { "get_logging_status", "set_logging_level" })
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(new FakeBridgeSource(source), command, Args(new { level = "trace" })));
		await commands.InvokeAsync(new FakeBridgeSource("main"), "set_logging_level", Args(new { level = "trace" }));
		Assert.Equal("trace", _services.Logger.GetStatus().MinimumLevel);
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(new FakeBridgeSource("main"), "set_logging_level", Args(new { level = "invalid" })));
	}

	[Fact]
	public Task 原生诊断测试日志来源与筛选一致() => WithSettingsUiAsync(async () =>
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		SettingsWindow window = new();
		using SettingsService service = new(fixture._services, window);
		DebugSettingsViewModel viewModel = new(service);
		try
		{
			window.Show();
			await viewModel.WriteTestLogAsync();
			LogEntry entry = fixture._services.Logger.RecentLogs().Last();
			Assert.Equal(LogSource.Backend, entry.Source); Assert.Equal("settings", entry.WindowLabel);
			viewModel.LevelFilter = "warn"; viewModel.SourceFilter = "backend"; viewModel.CategoryFilter = "NativeSettings"; viewModel.SearchText = "logging.suppressed";
			Assert.Single(viewModel.FilteredLogs);
			viewModel.SourceFilter = "frontend"; Assert.Empty(viewModel.FilteredLogs);
			await viewModel.SetMinimumLevelAsync("debug"); Assert.Equal("debug", fixture._services.Logger.GetStatus().MinimumLevel);
			await viewModel.ClearLogsAsync(); Assert.Empty(fixture._services.Logger.RecentLogs());
		}
		finally { window.Close(); }
	});
}
