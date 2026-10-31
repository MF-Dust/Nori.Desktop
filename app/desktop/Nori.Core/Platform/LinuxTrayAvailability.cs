using Tmds.DBus.Protocol;

namespace Nori.Core.Platform;

/// <summary>Linux 的托盘是会话 D-Bus 服务，不随 X11/Wayland 或图标创建成功而必然存在。</summary>
internal static class LinuxTrayAvailability
{
	internal static async Task<bool> CheckAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(DBusAddress.Session)) return false;
		using DBusConnection connection = new(DBusAddress.Session);
		await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
		return await connection.CallMethodAsync(CreateRequest(connection), static (message, _) =>
		{
			var reader = message.GetBodyReader();
			reader.ReadSignature("b");
			return reader.ReadBool();
		}).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
	}

	private static MessageBuffer CreateRequest(DBusConnection connection)
	{
		using var writer = connection.GetMessageWriter();
		writer.WriteMethodCallHeader(destination: "org.kde.StatusNotifierWatcher", path: "/StatusNotifierWatcher",
			@interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss", flags: MessageFlags.NoAutoStart);
		writer.WriteString("org.kde.StatusNotifierWatcher");
		writer.WriteString("IsStatusNotifierHostRegistered");
		return writer.CreateMessage();
	}
}
