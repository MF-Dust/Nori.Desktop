using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Core.Logging;
using Nori.Core.Voice.Realtime;
using Nori.Core.Security;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Chat;

/// <summary>原生对话宿主服务。</summary>
public sealed class NativeChatService : NativeBridgeServiceBase
{
	private readonly NativeChatContext _chatContext;

	/// <summary>定向宿主事件，可能从工作线程发出；订阅方负责切回 UI 线程。</summary>
	public event Action<string, JsonElement>? EventReceived;

	/// <summary>将服务与真实的原生对话窗口绑定。</summary>
	public NativeChatService(AppServices services, Window owner)
		: this(services, owner, NativeChatSurface.Full) { }

	internal NativeChatService(AppServices services, Window owner, NativeChatSurface surface)
		: this(services, new NativeChatContext(owner, surface)) { }

	private NativeChatService(AppServices services, NativeChatContext context) : base(services, context)
	{
		_chatContext = context;
		context.EventReceived += RaiseEvent;
	}

	internal bool SupportsLiveVoice => _chatContext.Surface == NativeChatSurface.Full;

	internal Runtime.ChatVoiceSnapshot? ReadLiveVoice() => SupportsLiveVoice ? Services.Runtime?.ReadChatVoice() : null;

	internal long HistoryRevision => Services.Runtime?.ChatHistoryRevision ?? 0;

	internal bool IsRecording => Services.Runtime?.Voice.IsRecording == true;

	protected override string StateNotificationFailureMessage => "原生对话通知失败";

	public override async Task<JsonElement> ExecuteAsync(string command, object? args = null, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		return await RedactAsync(base.ExecuteAsync(command, args, cancellationToken)).ConfigureAwait(false);
	}

	public override async Task<JsonElement> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		return await RedactAsync(base.GetSnapshotAsync(cancellationToken)).ConfigureAwait(false);
	}

	private static async Task<JsonElement> RedactAsync(Task<JsonElement> task)
	{
		try { return await task.ConfigureAwait(false); }
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			throw new InvalidOperationException(SensitiveDataRedactor.Redact(exception.Message));
		}
	}

	/// <summary>仅供真实原生聊天窗口的实时语音入口使用，返回既有 Agent 会话及完成任务。</summary>
	internal (string SessionId, Task Completion) StartRealtimeChat(RealtimeVoiceTurn turn)
	{
		ThrowIfDisposed();
		return (Services.Runtime ?? throw new InvalidOperationException("应用运行时尚未就绪"))
			.StartRealtimeChat(_chatContext, turn);
	}

	/// <summary>打开 AI 设置。</summary>
	public void OpenSettings() => OpenWindow(() => Services.Windows.ShowSettings("ai"));

	/// <summary>显示已有主窗口。</summary>
	public void OpenMain() => OpenWindow(() => Services.Windows.Show(WindowLabels.Main));

	/// <summary>打开完整对话查看历史。</summary>
	public void OpenChat() => OpenWindow(Services.Windows.ShowChat);

	private void OpenWindow(Action show)
	{
		ThrowIfDisposed();
		Dispatcher.UIThread.VerifyAccess();
		try { show(); }
		catch (Exception exception) { throw new InvalidOperationException(SensitiveDataRedactor.Redact(exception.Message)); }
	}

	private void RaiseEvent(string name, object? payload)
	{
		if (IsDisposed || EventReceived is not { } handlers) return;
		JsonElement value = JsonSerializer.SerializeToElement(payload, BridgeJson.Options);
		foreach (Action<string, JsonElement> handler in handlers.GetInvocationList().Cast<Action<string, JsonElement>>())
		{
			try { handler(name, value); }
			catch (Exception exception)
			{
				Services.Logger.Write(LogSource.Backend, "warn", $"原生对话通知失败: {SensitiveDataRedactor.ExceptionSummary(exception)}");
			}
		}
	}

	protected override void OnDisposing()
	{
		_chatContext.Dispose();
		EventReceived = null;
	}
}
