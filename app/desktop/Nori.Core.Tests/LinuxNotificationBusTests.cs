using System.Threading.Channels;
using Nori.Core.Notifications;
using Nori.Core.Platform;
using Tmds.DBus.Protocol;

namespace Nori.Core.Tests;

/// <summary>在私有 dbus-daemon 上验证通知协议，不占用桌面会话的真实通知服务。</summary>
public sealed class LinuxNotificationBusTests
{
	private const string Service = "org.freedesktop.Notifications";

	private sealed record NotifyCall(string AppName, string Icon, string Summary, string Body, string[] Actions, string? DesktopEntry, int Timeout);

	private sealed class Server : IPathMethodHandler
	{
		public string Path => "/org/freedesktop/Notifications";
		public bool HandlesChildPaths => false;
		internal Channel<NotifyCall> Received { get; } = Channel.CreateUnbounded<NotifyCall>();
		internal int Closed => Volatile.Read(ref _closed);
		private int _closed;

		public ValueTask HandleMethodAsync(MethodContext context)
		{
			switch (context.Request.MemberAsString)
			{
				case "GetCapabilities":
					using (var writer = context.CreateReplyWriter("as"))
					{
						writer.WriteArray(new[] { "actions", "body-markup" });
						context.Reply(writer.CreateMessage());
					}
					break;
				case "Notify":
				{
					// 处理器只记录内容，断言放在测试线程，避免失败原因被协议层吞掉。
					var reader = context.Request.GetBodyReader();
					string appName = reader.ReadString();
					reader.ReadUInt32();
					string icon = reader.ReadString();
					string summary = reader.ReadString();
					string body = reader.ReadString();
					string[] actions = reader.ReadArrayOfString();
					var hints = reader.ReadDictionaryOfStringToVariantValue();
					int timeout = reader.ReadInt32();
					string? desktopEntry = hints.TryGetValue("desktop-entry", out VariantValue entry) ? entry.GetString() : null;
					Received.Writer.TryWrite(new NotifyCall(appName, icon, summary, body, actions, desktopEntry, timeout));
					using var writer = context.CreateReplyWriter("u");
					writer.WriteUInt32(7);
					context.Reply(writer.CreateMessage());
					break;
				}
				case "CloseNotification":
					if (context.Request.GetBodyReader().ReadUInt32() == 7) Interlocked.Increment(ref _closed);
					using (var writer = context.CreateReplyWriter(null)) context.Reply(writer.CreateMessage());
					break;
				default:
					context.ReplyUnknownMethodError();
					break;
			}
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>
	/// 模拟被总线拉起的服务进程：先脱离继承的输出管道，写下标记后一直等到标记随总线目录删除（最多约 30 秒）。
	/// </summary>
	private static string ActivationHelper(string marker) =>
		$"/bin/sh -c \"exec </dev/null >/dev/null 2>&1; echo activated > '{marker}'; i=0; " +
		$"while [ -e '{marker}' ] && [ $i -lt 150 ]; do sleep 0.2; i=$((i+1)); done\"";

	private static void Emit(DBusConnection connection, string action)
	{
		using var writer = connection.GetMessageWriter();
		writer.WriteSignalHeader(path: "/org/freedesktop/Notifications", @interface: Service, member: "ActionInvoked", signature: "us");
		writer.WriteUInt32(7);
		writer.WriteString(action);
		Assert.True(connection.TrySendMessage(writer.CreateMessage()));
	}

	[PrivateDBusFact]
	public async Task 真实协议收发过滤伪造信号且撤回绑定原服务而非重用编号的新服务()
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
		await using PrivateDBus bus = await PrivateDBus.StartAsync();
		using DBusConnection original = new(bus.Address), replacement = new(bus.Address);
		await original.ConnectAsync();
		await replacement.ConnectAsync();
		Server first = new(), second = new();
		original.AddMethodHandler(first);
		replacement.AddMethodHandler(second);
		await original.RequestNameAsync(Service);
		using LinuxApprovalNotification notification = new(bus.Address, iconPath: null);
		Channel<LinuxNotificationSignal> signals = Channel.CreateUnbounded<LinuxNotificationSignal>();
		uint id = await notification.SendAsync(new ApprovalNotice
		{
			RequestId = "approval-bus", ToolName = "<tool>", ArgumentSummary = "secret", Description = "private",
		}, true, signal => signals.Writer.TryWrite(signal), timeout.Token);
		Assert.Equal(7u, id);
		Assert.True(notification.SupportsActions);
		NotifyCall received = await first.Received.Reader.ReadAsync(timeout.Token);
		Assert.Equal("Nori", received.AppName);
		Assert.Equal("", received.Icon);
		Assert.Equal("Nori · Approval required", received.Summary);
		Assert.Equal(LinuxApprovalNotification.DesktopEntryId, received.DesktopEntry);
		Assert.Equal(60000, received.Timeout);
		Assert.Contains("&lt;tool&gt;", received.Body);
		Assert.DoesNotContain("secret", received.Body);
		Assert.DoesNotContain("private", received.Body);
		Assert.Contains("action=allow&id=approval-bus", received.Actions);
		Emit(replacement, "forged");
		Emit(original, "default");
		Assert.Equal(new LinuxNotificationSignal(7, "default"), await signals.Reader.ReadAsync(timeout.Token));

		await original.ReleaseNameAsync(Service);
		await replacement.RequestNameAsync(Service);
		Emit(replacement, "forged-after-restart");
		Emit(original, "action=deny&id=approval-bus");
		Assert.Equal(new LinuxNotificationSignal(7, "action=deny&id=approval-bus"), await signals.Reader.ReadAsync(timeout.Token));
		await notification.CloseAsync(id, timeout.Token);
		Assert.Equal(1, first.Closed);
		Assert.Equal(0, second.Closed);
		Assert.False(signals.Reader.TryRead(out _));
	}

	[PrivateDBusFact]
	public async Task 按需启动的通知服务会被唤起且后续调用只发给应答的实例()
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
		// 总线只负责拉起进程；进程运行后由测试用另一条连接接管名称，模拟服务就绪。
		await using PrivateDBus bus = await PrivateDBus.StartAsync(directory => new Dictionary<string, string>
		{
			[Service] = ActivationHelper(Path.Combine(directory, "activated")),
		});
		using LinuxApprovalNotification notification = new(bus.Address, "/opt/nori/nori.png");
		Task<uint> sending = notification.SendAsync(new ApprovalNotice { RequestId = "approval-activation", ToolName = "readFile" },
			false, _ => { }, timeout.Token);
		string marker = Path.Combine(bus.Directory, "activated");
		while (!File.Exists(marker)) await Task.Delay(20, timeout.Token);
		Assert.False(sending.IsCompleted);

		using DBusConnection service = new(bus.Address);
		await service.ConnectAsync();
		Server server = new();
		service.AddMethodHandler(server);
		await service.RequestNameAsync(Service);
		Assert.Equal(7u, await sending.WaitAsync(timeout.Token));
		NotifyCall received = await server.Received.Reader.ReadAsync(timeout.Token);
		Assert.Equal("/opt/nori/nori.png", received.Icon);
		Assert.Equal("Nori · 需要授权", received.Summary);
		Assert.Equal(LinuxApprovalNotification.DesktopEntryId, received.DesktopEntry);
		await notification.CloseAsync(7, timeout.Token);
		Assert.Equal(1, server.Closed);
	}
}
