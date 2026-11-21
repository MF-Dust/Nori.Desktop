using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Logging;

namespace Nori.Desktop.Bridge;

/// <summary>
/// 原生窗口桥接服务基类。
/// 统一参数归一化、后台执行、快照读取、状态通知与在途后台操作的取消/等待。
/// </summary>
public abstract class NativeBridgeServiceBase : IDisposable
{
	private readonly object _operationSync = new();
	private CancellationTokenSource _backgroundCts = new();
	private readonly List<CancellationTokenSource> _retiredBackgroundCts = [];
	private TaskCompletionSource _idle = CompletedSignal();
	private int _activeOperations;
	private int _disposed;

	protected AppServices Services { get; }
	protected IBridgeSource Context { get; }
	protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

	/// <summary>状态变化通知，订阅方负责切回 UI 线程。</summary>
	public event Action? StateChanged;

	protected NativeBridgeServiceBase(AppServices services, IBridgeSource context)
	{
		Services = services;
		Context = context;
		if (services.Runtime is { } runtime) runtime.StateChanged += RaiseStateChanged;
	}

	protected virtual string StateNotificationFailureMessage => "原生窗口状态通知失败";

	protected virtual bool IsStateChangingCommand(string command) => false;

	protected virtual bool IsBackgroundCommand(string command) => false;

	/// <summary>后台执行桥接命令，返回统一的 JSON 结果。</summary>
	public virtual async Task<JsonElement> ExecuteAsync(
		string command,
		object? args = null,
		CancellationToken cancellationToken = default)
	{
		CancellationToken backgroundToken = BeginOperation();
		try
		{
			JsonElement commandArgs = NormalizeArgs(args);
			int snapshotVersion = Services.Runtime?.SnapshotVersion ?? 0;
			using CancellationTokenSource linked = CreateLinkedTokenSource(
				cancellationToken,
				IsBackgroundCommand(command) ? backgroundToken : CancellationToken.None);
			object? result = await Task.Run(
				() => Services.Commands.InvokeAsync(Context, command, commandArgs, linked.Token),
				linked.Token).ConfigureAwait(false);
			// 已完成的写入按成功返回；避免同一次保存因运行时已发通知而重复刷新。
			if (IsStateChangingCommand(command)
				&& (Services.Runtime is null || Services.Runtime.SnapshotVersion == snapshotVersion))
				RaiseStateChanged();
			return JsonSerializer.SerializeToElement(result, BridgeJson.Options);
		}
		finally { EndOperation(); }
	}

	/// <summary>后台读取脱敏运行时快照。</summary>
	public virtual async Task<JsonElement> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		CancellationToken backgroundToken = BeginOperation();
		try
		{
			using CancellationTokenSource linked = CreateLinkedTokenSource(cancellationToken, backgroundToken);
			return await Task.Run(
				() => JsonSerializer.SerializeToElement(
					(Services.Runtime ?? throw new InvalidOperationException("应用运行时尚未就绪")).BuildSnapshot(),
					BridgeJson.Options),
				linked.Token).ConfigureAwait(false);
		}
		finally { EndOperation(); }
	}

	/// <summary>等待已经启动的宿主调用结束，确保窗口关闭不会提前释放上下文。</summary>
	public Task WaitForPendingOperationsAsync(CancellationToken cancellationToken = default)
	{
		lock (_operationSync) return _idle.Task.WaitAsync(cancellationToken);
	}

	/// <summary>取消当前后台查询并允许窗口再次显示后重新查询。</summary>
	public void CancelBackgroundOperations()
	{
		CancellationTokenSource previous;
		lock (_operationSync)
		{
			if (_disposed != 0) return;
			previous = _backgroundCts;
			_backgroundCts = new CancellationTokenSource();
			_retiredBackgroundCts.Add(previous);
			if (_activeOperations == 0) DisposeRetiredBackgroundSources();
		}
		try { previous.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	protected async Task<T> RunBackgroundAsync<T>(Func<CancellationToken, T> operation, CancellationToken cancellationToken)
	{
		CancellationToken backgroundToken = BeginOperation();
		try
		{
			using CancellationTokenSource linked = CreateLinkedTokenSource(cancellationToken, backgroundToken);
			return await Task.Run(() => operation(linked.Token), linked.Token).ConfigureAwait(false);
		}
		finally { EndOperation(); }
	}

	protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

	private static JsonElement NormalizeArgs(object? args)
	{
		if (args is JsonElement element)
			return element.ValueKind == JsonValueKind.Undefined
				? JsonSerializer.SerializeToElement(new { }, BridgeJson.Options)
				: element.Clone();
		if (args is JsonNode node)
			return JsonSerializer.SerializeToElement(node, BridgeJson.Options);
		return JsonSerializer.SerializeToElement(args ?? new { }, BridgeJson.Options);
	}

	protected void RaiseStateChanged()
	{
		if (IsDisposed || StateChanged is not { } handlers) return;
		foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
		{
			try { handler(); }
			catch (Exception exception)
			{
				Services.Logger.Write(LogSource.Backend, "warn", StateNotificationFailureMessage, exception: exception);
			}
		}
	}

	protected virtual void OnDisposing() { }

	private CancellationTokenSource CreateLinkedTokenSource(CancellationToken callerToken, CancellationToken backgroundToken)
	{
		CancellationToken contextToken = Context.LifetimeToken;
		if (contextToken.CanBeCanceled && backgroundToken.CanBeCanceled)
		{
			return CancellationTokenSource.CreateLinkedTokenSource(
				[callerToken, Services.ShutdownToken, contextToken, backgroundToken]);
		}
		if (contextToken.CanBeCanceled)
			return CancellationTokenSource.CreateLinkedTokenSource(callerToken, Services.ShutdownToken, contextToken);
		if (backgroundToken.CanBeCanceled)
			return CancellationTokenSource.CreateLinkedTokenSource(callerToken, Services.ShutdownToken, backgroundToken);
		return CancellationTokenSource.CreateLinkedTokenSource(callerToken, Services.ShutdownToken);
	}

	private CancellationToken BeginOperation()
	{
		lock (_operationSync)
		{
			ObjectDisposedException.ThrowIf(_disposed != 0, this);
			if (_activeOperations++ == 0)
				_idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			return _backgroundCts.Token;
		}
	}

	private void EndOperation()
	{
		lock (_operationSync)
		{
			if (--_activeOperations != 0) return;
			if (_disposed != 0) DisposeResources();
			else DisposeRetiredBackgroundSources();
			_idle.TrySetResult();
		}
	}

	private void DisposeResources()
	{
		_backgroundCts.Dispose();
		DisposeRetiredBackgroundSources();
	}

	private void DisposeRetiredBackgroundSources()
	{
		foreach (CancellationTokenSource retired in _retiredBackgroundCts) retired.Dispose();
		_retiredBackgroundCts.Clear();
	}

	private static TaskCompletionSource CompletedSignal()
	{
		TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
		signal.SetResult();
		return signal;
	}

	/// <summary>停止接受调用并退订通知；在途调用结束后释放后台取消资源。</summary>
	public void Dispose()
	{
		lock (_operationSync)
		{
			if (_disposed != 0) return;
			Volatile.Write(ref _disposed, 1);
			if (Services.Runtime is { } runtime) runtime.StateChanged -= RaiseStateChanged;
			OnDisposing();
			StateChanged = null;
			if (_activeOperations == 0) DisposeResources();
		}
	}
}
