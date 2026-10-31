using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Tmds.DBus.Protocol;

namespace Nori.Core.Platform;

/// <summary>Linux 的托盘是会话 D-Bus 服务，不随 X11/Wayland 或图标创建成功而必然存在。</summary>
internal static class LinuxTrayAvailability
{
	private const string Watcher = "org.kde.StatusNotifierWatcher";
	private const string WatcherPath = "/StatusNotifierWatcher";
	private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

	internal static Task<bool> CheckAsync(CancellationToken cancellationToken) => CheckAsync(DBusAddress.Session, cancellationToken);

	/// <summary>单次查询；没有 StatusNotifierWatcher 是常见的桌面状态，返回 false 而不是报错。</summary>
	internal static async Task<bool> CheckAsync(string? address, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(address)) return false;
		using DBusConnection connection = new(address);
		await connection.ConnectAsync().AsTask().WaitObservedAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
		return await QueryAsync(connection, cancellationToken).ConfigureAwait(false);
	}

	internal static IAsyncEnumerable<bool> WatchAsync(CancellationToken cancellationToken) => WatchAsync(DBusAddress.Session, cancellationToken);

	/// <summary>
	/// 在同一条连接上先报告当前值，之后只在监视器易主或宿主注册变化时重新查询，不轮询。
	/// 连接断开时抛出异常，由调用方决定何时重试。
	/// </summary>
	internal static async IAsyncEnumerable<bool> WatchAsync(string? address, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(address))
		{
			yield return false;
			yield break;
		}

		using DBusConnection connection = new(address);
		await connection.ConnectAsync().AsTask().WaitObservedAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
		// 只记录「需要重查」：连续信号合并成一次查询，连接断开时带异常完成。
		Channel<bool> changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
		{
			FullMode = BoundedChannelFullMode.DropWrite,
			SingleReader = true,
		});
		void OnSignal(Notification notification)
		{
			if (notification.Type == NotificationType.Value) changes.Writer.TryWrite(true);
			else if (notification.Type is NotificationType.ConnectionClosed or NotificationType.ConnectionFailed)
				changes.Writer.TryComplete(notification.Exception ?? new DBusConnectionClosedException("会话 D-Bus 连接已断开"));
		}

		// 信号只触发重查，结果始终以监视器自身的属性为准，因此不按发送方过滤。
		using IDisposable ownerChanged = await connection.AddMatchAsync(new MatchRule
		{
			Type = MessageType.Signal, Path = "/org/freedesktop/DBus", Interface = "org.freedesktop.DBus",
			Member = "NameOwnerChanged", Arg0 = Watcher,
		}, OnSignal, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnConnectionFailed)
			.AsTask().WaitObservedAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
		using IDisposable hostRegistered = await WatchWatcherSignalAsync(connection, "StatusNotifierHostRegistered", OnSignal, cancellationToken).ConfigureAwait(false);
		using IDisposable hostUnregistered = await WatchWatcherSignalAsync(connection, "StatusNotifierHostUnregistered", OnSignal, cancellationToken).ConfigureAwait(false);

		yield return await QueryAsync(connection, cancellationToken).ConfigureAwait(false);
		while (await changes.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
		{
			while (changes.Reader.TryRead(out _)) { }
			yield return await QueryAsync(connection, cancellationToken).ConfigureAwait(false);
		}
		throw new DBusConnectionClosedException("会话 D-Bus 连接已关闭");
	}

	private static Task<IDisposable> WatchWatcherSignalAsync(DBusConnection connection, string member, Action<Notification> handler, CancellationToken cancellationToken) =>
		connection.AddMatchAsync(new MatchRule
		{
			Type = MessageType.Signal, Path = WatcherPath, Interface = Watcher, Member = member,
		}, handler, emitOnCapturedContext: false).AsTask().WaitObservedAsync(CallTimeout, cancellationToken);

	private static async Task<bool> QueryAsync(DBusConnection connection, CancellationToken cancellationToken)
	{
		try
		{
			return await connection.CallMethodAsync(CreateRequest(connection), static (message, _) =>
			{
				var reader = message.GetBodyReader();
				reader.ReadSignature("b");
				return reader.ReadBool();
			}).WaitObservedAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
		}
		catch (DBusErrorReplyException exception) when (exception.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown"
			or "org.freedesktop.DBus.Error.NameHasNoOwner")
		{
			return false;
		}
	}

	private static MessageBuffer CreateRequest(DBusConnection connection)
	{
		using var writer = connection.GetMessageWriter();
		writer.WriteMethodCallHeader(destination: Watcher, path: WatcherPath,
			@interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss", flags: MessageFlags.NoAutoStart);
		writer.WriteString(Watcher);
		writer.WriteString("IsStatusNotifierHostRegistered");
		return writer.CreateMessage();
	}
}
