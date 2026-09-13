using System.Collections.Frozen;
using System.Text.Json;
using Avalonia.Controls;
using Nori.Core.Logging;
using Nori.Desktop.Bridge;

namespace Nori.Desktop.Memory;

/// <summary>原生记忆窗口的宿主服务，复用既有业务命令并严格隔离来源权限。</summary>
public sealed class MemoryService : IDisposable
{
	private static readonly FrozenSet<string> AllowedCommands = new[]
	{
		"memory_add", "memory_list", "memory_update", "memory_delete", "memory_clear",
		"memory_archive", "memory_restore", "memory_overview", "memory_list_page", "memory_get",
		"memory_atom_list", "memory_knowledge_status", "memory_knowledge_reindex", "memory_knowledge_open",
		"memory_recall_debug", "memory_get_settings", "memory_update_settings", "memory_search_hybrid",
		"memory_reembed_all", "memory_export", "memory_import_preview", "memory_import_commit",
		"clipboard_write_text",
	}.ToFrozenSet(StringComparer.Ordinal);

	private readonly AppServices _services;
	private readonly MemoryContext _context;
	private readonly BridgeCommandRouter _router;
	private readonly object _operationSync = new();
	private CancellationTokenSource _backgroundCts = new();
	private readonly List<CancellationTokenSource> _retiredBackgroundCts = [];
	private TaskCompletionSource _idle = CompletedSignal();
	private int _activeOperations;
	private int _disposed;

	/// <summary>记忆或运行时状态变化通知，订阅方负责切回 UI 线程。</summary>
	public event Action? StateChanged;

	/// <summary>创建绑定到记忆窗口的宿主服务。</summary>
	public MemoryService(AppServices services, Window owner)
	{
		_services = services ?? throw new ArgumentNullException(nameof(services));
		_context = new MemoryContext(owner ?? throw new ArgumentNullException(nameof(owner)));
		_router = new BridgeCommandRouter(services);
		if (services.Runtime is { } runtime) runtime.StateChanged += RaiseStateChanged;
	}

	/// <summary>原生记忆窗口允许执行的完整命令集合。</summary>
	public static IReadOnlySet<string> Commands => AllowedCommands;

	/// <summary>检查命令是否属于原生记忆权限范围。</summary>
	public static bool IsCommandAllowed(string command) => !string.IsNullOrWhiteSpace(command) && AllowedCommands.Contains(command);

	/// <summary>校验可信来源的领域边界，供服务和宿主入口共同使用。</summary>
	internal static void ValidateSourceCommand(IBridgeSource source, string command)
	{
		if (source is INativeMemorySource && !IsCommandAllowed(command))
			throw new InvalidOperationException($"原生记忆窗口不允许执行命令: {command}");
	}

	/// <summary>后台执行记忆命令，返回与既有桥接一致的 JSON。</summary>
	public async Task<JsonElement> ExecuteAsync(string command, object? args = null, CancellationToken cancellationToken = default)
	{
		ValidateSourceCommand(_context, command);
		CancellationToken backgroundToken = BeginOperation();
		try
		{
			JsonElement commandArgs = args is JsonElement element
				? element.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement(new { }) : element.Clone()
				: JsonSerializer.SerializeToElement(args ?? new Dictionary<string, object?>(), BridgeJson.Options);
			int snapshotVersion = _services.Runtime?.SnapshotVersion ?? 0;
			using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken, _services.ShutdownToken, IsBackgroundCommand(command) ? backgroundToken : CancellationToken.None);
			object? result = await Task.Run(() => _router.InvokeAsync(_context, command, commandArgs, linked.Token), linked.Token).ConfigureAwait(false);
			// 已完成的写入按成功返回；关闭期间取消查询不能掩盖已经提交的修改。
			if (IsStateChangingCommand(command) && (_services.Runtime is null || _services.Runtime.SnapshotVersion == snapshotVersion))
				RaiseStateChanged();
			return JsonSerializer.SerializeToElement(result, BridgeJson.Options);
		}
		finally { EndOperation(); }
	}

	/// <summary>后台读取脱敏快照以同步语言、安全模式和运行时设置。</summary>
	public async Task<JsonElement> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		CancellationToken backgroundToken = BeginOperation();
		try
		{
			using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _services.ShutdownToken, backgroundToken);
			return await Task.Run(() => JsonSerializer.SerializeToElement(
				(_services.Runtime ?? throw new InvalidOperationException("应用运行时尚未就绪")).BuildSnapshot(), BridgeJson.Options), linked.Token).ConfigureAwait(false);
		}
		finally { EndOperation(); }
	}

	/// <summary>等待已经启动的宿主调用真正结束，确保窗口关闭不会提前释放写入上下文。</summary>
	public Task WaitForPendingOperationsAsync(CancellationToken cancellationToken = default)
	{
		lock (_operationSync) return _idle.Task.WaitAsync(cancellationToken);
	}

	/// <summary>取消当前查询与可恢复的索引维护，保留普通保存，并允许隐藏后重新查询。</summary>
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

	private CancellationToken BeginOperation()
	{
		lock (_operationSync)
		{
			ObjectDisposedException.ThrowIf(_disposed != 0, this);
			if (_activeOperations++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			return _backgroundCts.Token;
		}
	}

	private void EndOperation()
	{
		lock (_operationSync)
		{
			if (--_activeOperations != 0) return;
			if (_disposed != 0) DisposeContext();
			else DisposeRetiredBackgroundSources();
			_idle.TrySetResult();
		}
	}

	private static TaskCompletionSource CompletedSignal()
	{
		TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
		signal.SetResult();
		return signal;
	}

	internal static bool IsStateChangingCommand(string command) => command is
		"memory_add" or "memory_update" or "memory_delete" or "memory_clear" or "memory_archive"
		or "memory_restore" or "memory_knowledge_reindex" or "memory_update_settings" or "memory_reembed_all" or "memory_import_commit";

	internal static bool IsBackgroundCommand(string command) => command is
		"memory_list" or "memory_overview" or "memory_list_page" or "memory_get" or "memory_atom_list"
		or "memory_knowledge_status" or "memory_knowledge_reindex" or "memory_recall_debug"
		or "memory_get_settings" or "memory_search_hybrid" or "memory_reembed_all" or "memory_export" or "memory_import_preview";

	private void DisposeContext()
	{
		_context.Dispose();
		_backgroundCts.Dispose();
		DisposeRetiredBackgroundSources();
	}

	private void DisposeRetiredBackgroundSources()
	{
		foreach (CancellationTokenSource retired in _retiredBackgroundCts) retired.Dispose();
		_retiredBackgroundCts.Clear();
	}

	private void RaiseStateChanged()
	{
		if (Volatile.Read(ref _disposed) != 0 || StateChanged is not { } handlers) return;
		foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
		{
			try { handler(); }
			catch (Exception exception)
			{
				try { _services.Logger.Write(LogSource.Backend, "warn", $"记忆窗口状态通知失败: {exception.GetType().Name}"); }
				catch { }
			}
		}
	}

	/// <summary>停止接受调用并退订通知；在途调用结束后才释放来源上下文。</summary>
	public void Dispose()
	{
		lock (_operationSync)
		{
			if (_disposed != 0) return;
			Volatile.Write(ref _disposed, 1);
			if (_services.Runtime is { } runtime) runtime.StateChanged -= RaiseStateChanged;
			StateChanged = null;
			if (_activeOperations == 0) DisposeContext();
		}
	}
}
