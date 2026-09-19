using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nori.Core.Logging;
using Nori.Core.Security;

namespace Nori.PluginRuntime;

/// <summary>插件 WebView 与宿主之间的最小通信来源。</summary>
internal interface IPluginBridgeSource
{
	string PluginId { get; }
	string WindowId { get; }
	string Label { get; }
	bool IsVisible { get; }
	void PostResult(long id, object? value, string? error);
	Task CloseAsync(CancellationToken cancellationToken = default);
}

internal sealed record PluginBridgeMessage
{
	[JsonPropertyName("kind")]
	public string Kind { get; init; } = "";

	[JsonPropertyName("id")]
	public long Id { get; init; }

	[JsonPropertyName("cmd")]
	public string? Cmd { get; init; }

	[JsonPropertyName("args")]
	public JsonElement Args { get; init; }
}

internal sealed record PluginBridgeResult
{
	[JsonPropertyName("kind")]
	public required string Kind { get; init; }

	[JsonPropertyName("id")]
	public required long Id { get; init; }

	[JsonPropertyName("value")]
	public object? Value { get; init; }

	[JsonPropertyName("error")]
	public string? Error { get; init; }
}

internal static class PluginRuntimeJson
{
	public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
	{
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};
}

/// <summary>
/// 插件页面的隔离桥接内核。
///
/// 它不接入宿主 NoriBridge，只接受插件页面自己的最小白名单命令。
/// </summary>
internal sealed class PluginBridge : IAsyncDisposable
{
	private readonly string _pluginId;
	private readonly string _windowId;
	private readonly PluginDescriptorSummary _descriptor;
	private readonly Func<IReadOnlyList<string>>? _capabilityProvider;
	private readonly Func<CancellationToken, Task>? _closeSelfHandler;
	private readonly IPluginWebViewCommandHandler? _commandHandler;
	private readonly FileLogger? _logger;
	private readonly ConcurrentDictionary<Task, byte> _pendingInvokes = new();
	private readonly CancellationTokenSource _cts = new();
	private int _disposed;

	public PluginBridge(
		string pluginId,
		string windowId,
		PluginDescriptorSummary descriptor,
		Func<IReadOnlyList<string>>? capabilityProvider = null,
		Func<CancellationToken, Task>? closeSelfHandler = null,
		IPluginWebViewCommandHandler? commandHandler = null,
		FileLogger? logger = null)
	{
		PluginWindowHost.ValidatePluginId(pluginId, nameof(pluginId));
		PluginWindowHost.ValidateId(windowId, nameof(windowId));
		ArgumentNullException.ThrowIfNull(descriptor);

		_pluginId = pluginId;
		_windowId = windowId;
		_descriptor = descriptor;
		_capabilityProvider = capabilityProvider;
		_closeSelfHandler = closeSelfHandler;
		_commandHandler = commandHandler;
		_logger = logger;
	}

	public void Handle(IPluginBridgeSource source, string raw)
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		PluginBridgeMessage? message;
		try
		{
			message = JsonSerializer.Deserialize<PluginBridgeMessage>(raw, PluginRuntimeJson.Options);
		}
		catch (JsonException exception)
		{
			_logger?.Write(LogSource.Backend, "warn", $"插件桥接消息解析失败 [{_pluginId}:{_windowId}]: {exception.GetType().Name}");
			return;
		}

		if (message is null) return;
		if (!string.Equals(message.Kind, "invoke", StringComparison.Ordinal))
		{
			_logger?.Write(LogSource.Backend, "warn", "未知的插件桥接消息种类", "Plugin", "plugin.unknown_kind");
			return;
		}

		TrackInvoke(source, message);
	}

	private void TrackInvoke(IPluginBridgeSource source, PluginBridgeMessage message)
	{
		Task task = Task.Run(() => HandleInvokeObservedAsync(source, message), CancellationToken.None);
		_pendingInvokes.TryAdd(task, 0);
		_ = task.ContinueWith(
			completed => _pendingInvokes.TryRemove(completed, out _),
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private async Task HandleInvokeObservedAsync(IPluginBridgeSource source, PluginBridgeMessage message)
	{
		try
		{
			await HandleInvokeAsync(source, message, _cts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (_cts.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			_logger?.Write(LogSource.Backend, "error", $"插件桥接执行发生未捕获异常 [{_pluginId}:{_windowId}]: {SensitiveDataRedactor.ExceptionSummary(exception)}");
		}
	}

	private async Task HandleInvokeAsync(IPluginBridgeSource source, PluginBridgeMessage message, CancellationToken cancellationToken)
	{
		string command = message.Cmd ?? "";
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			object? result = await ExecuteCommandAsync(source, command, message.Args, cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			source.PostResult(message.Id, result, null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			string error = SensitiveDataRedactor.Redact(exception.Message);
			_logger?.Write(LogSource.Backend, "warn", $"插件命令执行失败 code={GetErrorCode(exception)}", "Plugin", "plugin.command_failed", exception);
			source.PostResult(message.Id, null, error);
		}
	}

	public async Task<object?> ExecuteCommandAsync(
		IPluginBridgeSource source,
		string command,
		JsonElement args,
		CancellationToken cancellationToken)
	{
		if (!string.Equals(source.PluginId, _pluginId, StringComparison.Ordinal) ||
			!string.Equals(source.WindowId, _windowId, StringComparison.Ordinal) ||
			!string.Equals(source.Label, PluginWindowHost.BuildLabel(_pluginId, _windowId), StringComparison.Ordinal))
			throw new PluginException(PluginErrorCodes.BridgeDenied, "插件桥接来源身份无效。");

		return command switch
		{
			"plugin_get_info" => new
			{
				id = _descriptor.Id,
				name = _descriptor.Name,
				version = _descriptor.Version,
				description = _descriptor.Description,
				author = _descriptor.Author,
				capabilities = _descriptor.Capabilities,
			},
			"plugin_get_capabilities" => new { capabilities = _capabilityProvider?.Invoke() ?? _descriptor.Capabilities },
			"window_get_info" => new
			{
				pluginId = _pluginId,
				windowId = _windowId,
				label = PluginWindowHost.BuildLabel(_pluginId, _windowId),
				isVisible = source.IsVisible,
			},
			"window_close" => await CloseAsync(source, cancellationToken).ConfigureAwait(false),
			"ping" => new
			{
				pong = true,
				timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			},
			_ when _commandHandler is not null => await _commandHandler
				.HandleAsync(command, args, cancellationToken)
				.ConfigureAwait(false),
			_ => throw new PluginException(PluginErrorCodes.BridgeDenied, $"命令 '{command}' 未被允许或不在插件 Web 视图安全白名单中。"),
		};
	}

	private async Task<object> CloseAsync(IPluginBridgeSource source, CancellationToken cancellationToken)
	{
		if (_closeSelfHandler is not null)
			await _closeSelfHandler(cancellationToken).ConfigureAwait(false);
		else
			await source.CloseAsync(cancellationToken).ConfigureAwait(false);
		return new { closed = true };
	}

	private static string GetErrorCode(Exception exception) =>
		exception is PluginException pluginException ? pluginException.Code : PluginErrorCodes.BridgeFailed;

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_cts.Cancel();
		Task[] pending = _pendingInvokes.Keys.ToArray();
		if (pending.Length > 0)
		{
			Task all = Task.WhenAll(pending);
			await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
		}
		_cts.Dispose();
	}
}
