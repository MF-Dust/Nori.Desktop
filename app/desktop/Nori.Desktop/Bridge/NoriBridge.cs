using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Threading;
using Nori.Core.Logging;
using Nori.Core.Resources;
using Nori.Core.Security;
using Nori.Core.Telemetry;
using Nori.PluginRuntime;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Bridge;

/// <summary>
/// 前端 ↔ 宿主 桥接内核
///
/// NativeWebView 只提供 JS→宿主的 invokeCSharpAction(string) 与宿主→JS 的 InvokeScript,
/// 这里仅负责请求/响应关联、事件广播与传输层错误回写；命令策略由 BridgeCommandRouter 处理。
/// </summary>
public sealed class NoriBridge(AppServices services)
{
	private readonly AppServices _services = services;
	private readonly BridgeCommandRouter _router = new(services);
	private readonly CancellationTokenSource _shutdownCts =
		CancellationTokenSource.CreateLinkedTokenSource(services.ShutdownToken);
	private readonly ConcurrentDictionary<Task, byte> _pendingInvokes = new();
	private int _disposed;

	/// <summary>处理页面发来的一条消息。</summary>
	public void Handle(NoriWindow source, string raw)
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		BridgeMessage? message;
		try
		{
			message = JsonSerializer.Deserialize<BridgeMessage>(raw, BridgeJson.Options);
		}
		catch (JsonException exception)
		{
			_services.Logger.Write(LogSource.Backend, "warn", $"桥接消息解析失败: {exception.Message}");
			return;
		}
		if (message is null) return;

		switch (message.Kind)
		{
			case "invoke":
				TrackInvoke(source, message);
				break;
			case "emit":
				if (source.Label == WindowLabels.AudioHost) return;
				if (message.Event is { Length: > 0 } name)
				{
					object? payload = message.Payload.ValueKind == JsonValueKind.Undefined ? null : message.Payload.Clone();
					Dispatcher.UIThread.Post(() =>
					{
						if (Volatile.Read(ref _disposed) != 0) return;
						try { _services.Windows.Broadcast(name, payload); }
						catch { /* closing windows must not fault the dispatcher */ }
					});
				}
				break;
			default:
				_services.Logger.Write(LogSource.Backend, "warn", $"未知的桥接消息种类: {message.Kind}");
				break;
		}
	}

	private void TrackInvoke(NoriWindow source, BridgeMessage message)
	{
		Task task = Task.Run(() => HandleInvokeObservedAsync(source, message), CancellationToken.None);
		_pendingInvokes.TryAdd(task, 0);
		_ = task.ContinueWith(
			completed => _pendingInvokes.TryRemove(completed, out _),
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private async Task HandleInvokeObservedAsync(NoriWindow source, BridgeMessage message)
	{
		try
		{
			await HandleInvokeAsync(source, message, _shutdownCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			try
			{
				BridgeFailure failure = BridgeFailureClassifier.Classify(exception);
				if (failure.Telemetry) _services.Telemetry.CaptureException(exception, "bridge.invoke", tags: failure.Tags);
				_services.Logger.Write(LogSource.Backend, failure.LogLevel, $"桥接调用后台任务失败: {SensitiveDataRedactor.ExceptionSummary(exception)}");
			}
			catch
			{
				// Shutdown may already have released logging/telemetry dependencies.
			}
		}
	}

	private async Task HandleInvokeAsync(NoriWindow source, BridgeMessage message, CancellationToken cancellationToken)
	{
		string cmd = message.Cmd ?? "";
		using ITelemetryTransaction transaction = _services.Telemetry.StartTransaction($"bridge.{cmd}");
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			object? value = await _router.InvokeAsync(source, cmd, message.Args, cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			source.PostResult(message.Id, value, null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// 应用退出时不再向已经关闭的 WebView 回写结果。
		}
		catch (Exception exception)
		{
			BridgeFailure failure = BridgeFailureClassifier.Classify(exception);
			if (failure.Telemetry) _services.Telemetry.CaptureException(exception, $"bridge.{cmd}", tags: failure.Tags);
			_services.Logger.Write(LogSource.Backend, failure.LogLevel, $"命令执行失败: {cmd}: {SensitiveDataRedactor.ExceptionSummary(exception)}");
			source.PostResult(message.Id, null, SensitiveDataRedactor.Redact(exception.Message));
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_shutdownCts.Cancel();
		Task[] pending = _pendingInvokes.Keys.ToArray();
		if (pending.Length > 0)
		{
			Task all = Task.WhenAll(pending);
			await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
		}
		_shutdownCts.Dispose();
	}
}
