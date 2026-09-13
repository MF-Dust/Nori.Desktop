using System.Collections.Frozen;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Core.Logging;
using Nori.Core.Security;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Chat;

/// <summary>原生对话宿主服务，复用桥接业务并限制为固定的对话命令域。</summary>
public sealed class NativeChatService : IDisposable
{
	private static readonly FrozenSet<string> AllowedCommands = new[]
	{
		"chat_start", "chat_cancel", "chat_history_page", "chat_clear",
		"approval_respond", "approval_extend", "stt_start", "stt_stop", "tts_stop",
		"clipboard_write_text", "open_url",
	}.ToFrozenSet(StringComparer.Ordinal);

	private readonly AppServices _services;
	private readonly NativeChatContext _context;
	private readonly BridgeCommandRouter _router;
	private int _disposed;

	/// <summary>定向宿主事件，可能从工作线程发出；订阅方负责切回 UI 线程。</summary>
	public event Action<string, JsonElement>? EventReceived;

	/// <summary>脱敏快照变化通知。</summary>
	public event Action? StateChanged;

	/// <summary>将服务与真实的原生对话窗口绑定。</summary>
	public NativeChatService(AppServices services, Window owner)
	{
		_services = services ?? throw new ArgumentNullException(nameof(services));
		_context = new NativeChatContext(owner ?? throw new ArgumentNullException(nameof(owner)), RaiseEvent);
		_router = new BridgeCommandRouter(services);
		if (services.Runtime is { } runtime)
		{
			runtime.StateChanged += RaiseStateChanged;
		}
	}

	/// <summary>原生对话允许的完整命令白名单。</summary>
	public static IReadOnlySet<string> Commands => AllowedCommands;

	/// <summary>检查是否属于原生对话权限范围。</summary>
	public static bool IsCommandAllowed(string command) => !string.IsNullOrWhiteSpace(command) && AllowedCommands.Contains(command);

	/// <summary>服务、路由和命令实现共用的权限边界，拒绝标签伪装与未来命令。</summary>
	internal static void ValidateSourceCommand(IBridgeSource source, string command)
	{
		if (source is not INativeChatSource)
		{
			if (source.Label == WindowLabels.Chat) throw new InvalidOperationException("原生对话来源身份无效");
			return;
		}
		if (source.Label != WindowLabels.Chat || !IsCommandAllowed(command))
			throw new InvalidOperationException("原生对话窗口不允许执行此命令");
		if (!source.IsVisible && command is not ("chat_history_page" or "chat_cancel" or "approval_respond" or "stt_stop" or "tts_stop"))
			throw new InvalidOperationException("对话窗口不可见");
	}

	/// <summary>后台执行对话命令；剪贴板等原生操作仍由桥接切回 UI 线程。</summary>
	public async Task<JsonElement> ExecuteAsync(string command, object? args = null, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		try
		{
			ValidateSourceCommand(_context, command);
			JsonElement commandArgs = args is JsonElement element && element.ValueKind != JsonValueKind.Undefined
				? element.Clone()
				: JsonSerializer.SerializeToElement(args is JsonElement ? new { } : args ?? new { }, BridgeJson.Options);
			using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _services.ShutdownToken, _context.LifetimeToken);
			object? result = await Task.Run(() => _router.InvokeAsync(_context, command, commandArgs, linked.Token), linked.Token).ConfigureAwait(false);
			return JsonSerializer.SerializeToElement(result, BridgeJson.Options);
		}
		catch (OperationCanceledException) { throw; }
		catch (Exception exception) { throw new InvalidOperationException(SensitiveDataRedactor.Redact(exception.Message)); }
	}

	/// <summary>后台读取与 WebView 同源的脱敏快照，chat 字段反映实际启用的对话后端。</summary>
	public async Task<JsonElement> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		try
		{
			using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _services.ShutdownToken, _context.LifetimeToken);
			return await Task.Run(() => JsonSerializer.SerializeToElement(
				(_services.Runtime ?? throw new InvalidOperationException("应用运行时尚未就绪")).BuildSnapshot(), BridgeJson.Options), linked.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) { throw; }
		catch (Exception exception) { throw new InvalidOperationException(SensitiveDataRedactor.Redact(exception.Message)); }
	}

	/// <summary>仅打开 AI 设置，不给对话 UI 通用窗口命令权限。</summary>
	public void OpenSettings() => OpenWindow(() => _services.Windows.ShowSettings("ai"));

	/// <summary>返回已有主窗口，音频宿主继续保留在该 WebView。</summary>
	public void OpenMain() => OpenWindow(() => _services.Windows.Show(WindowLabels.Main));

	private void OpenWindow(Action show)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		Dispatcher.UIThread.VerifyAccess();
		try { show(); }
		catch (Exception exception) { throw new InvalidOperationException(SensitiveDataRedactor.Redact(exception.Message)); }
	}

	private void RaiseEvent(string name, object? payload)
	{
		if (Volatile.Read(ref _disposed) != 0 || EventReceived is not { } handlers) return;
		JsonElement value = JsonSerializer.SerializeToElement(payload, BridgeJson.Options);
		foreach (Action<string, JsonElement> handler in handlers.GetInvocationList().Cast<Action<string, JsonElement>>())
		{
			try { handler(name, value); }
			catch (Exception exception) { LogNotificationFailure(exception); }
		}
	}

	private void RaiseStateChanged()
	{
		if (Volatile.Read(ref _disposed) != 0 || StateChanged is not { } handlers) return;
		foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
		{
			try { handler(); }
			catch (Exception exception) { LogNotificationFailure(exception); }
		}
	}

	private void LogNotificationFailure(Exception exception)
	{
		try { _services.Logger.Write(LogSource.Backend, "warn", $"原生对话通知失败: {SensitiveDataRedactor.ExceptionSummary(exception)}"); }
		catch { }
	}

	/// <summary>退订并取消本来源尚未结束的请求，不影响其他窗口。</summary>
	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		if (_services.Runtime is { } runtime)
		{
			runtime.StateChanged -= RaiseStateChanged;
		}
		_context.Dispose();
		EventReceived = null;
		StateChanged = null;
	}
}
