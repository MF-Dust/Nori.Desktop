using System.Net.WebSockets;

namespace Nori.Core.Voice.Stt;

/// <summary>
/// 轻量级 Realtime WebSocket 传输抽象，用于隔离 <see cref="ClientWebSocket"/> 与测试假实现。
/// </summary>
public interface IRealtimeWebSocketTransport : IAsyncDisposable
{
	/// <summary>当前传输连接状态。</summary>
	WebSocketState State { get; }

	/// <summary>在建连前设置请求头。</summary>
	void SetRequestHeader(string name, string value);

	/// <summary>在建连前设置心跳保活间隔。</summary>
	void ConfigureKeepAlive(TimeSpan keepAliveInterval);

	/// <summary>建立 WebSocket 连接。</summary>
	Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

	/// <summary>发送一帧完整 UTF-8 文本消息。</summary>
	ValueTask SendTextAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken);

	/// <summary>接收一帧服务端消息或分片。</summary>
	ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);

	/// <summary>发送关闭握手帧。</summary>
	Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken);

	/// <summary>立即中止传输。</summary>
	void Abort();
}

/// <summary>基于系统 <see cref="ClientWebSocket"/> 的默认传输实现。</summary>
internal sealed class ClientWebSocketTransport : IRealtimeWebSocketTransport
{
	private readonly ClientWebSocket _socket = new();

	public WebSocketState State => _socket.State;

	public void SetRequestHeader(string name, string value) =>
		_socket.Options.SetRequestHeader(name, value);

	public void ConfigureKeepAlive(TimeSpan keepAliveInterval) =>
		_socket.Options.KeepAliveInterval = keepAliveInterval;

	public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
		_socket.ConnectAsync(uri, cancellationToken);

	public ValueTask SendTextAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken) =>
		_socket.SendAsync(utf8Json, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

	public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
		_socket.ReceiveAsync(buffer, cancellationToken);

	public Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
		_socket.CloseAsync(closeStatus, statusDescription, cancellationToken);

	public void Abort() => _socket.Abort();

	public ValueTask DisposeAsync()
	{
		_socket.Dispose();
		return ValueTask.CompletedTask;
	}
}
