using System.Threading.Channels;
using Nori.Core.Notifications;
using Nori.Core.Platform;
using Tmds.DBus.Protocol;

namespace Nori.Core.Tests;

/// <summary>仅在独立的 dbus-run-session 中启用，不能占用用户真实通知服务。</summary>
public sealed class LinuxNotificationBusFactAttribute : FactAttribute
{
	public LinuxNotificationBusFactAttribute()
	{
		if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("NORI_TEST_NOTIFICATION_BUS") != "1")
			Skip = "需要独立 D-Bus 会话及 NORI_TEST_NOTIFICATION_BUS=1";
	}
}

public sealed class LinuxNotificationBusTests
{
	private const string Service = "org.freedesktop.Notifications";

	private sealed class Server : IPathMethodHandler
	{
		public string Path => "/org/freedesktop/Notifications";
		public bool HandlesChildPaths => false;
		internal Channel<(string Body, string[] Actions)> Received { get; } = Channel.CreateUnbounded<(string, string[])>();
		internal int Closed { get; private set; }

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
					var reader = context.Request.GetBodyReader();
					Assert.Equal("Nori", reader.ReadString());
					Assert.Equal(0u, reader.ReadUInt32());
					Assert.Equal("nori", reader.ReadString());
					Assert.Equal("Nori · Approval required", reader.ReadString());
					string body = reader.ReadString();
					string[] actions = reader.ReadArrayOfString();
					Assert.Empty(reader.ReadDictionaryOfStringToVariantValue());
					Assert.Equal(60000, reader.ReadInt32());
					Received.Writer.TryWrite((body, actions));
					using var writer = context.CreateReplyWriter("u");
					writer.WriteUInt32(7);
					context.Reply(writer.CreateMessage());
					break;
				}
				case "CloseNotification":
					Assert.Equal(7u, context.Request.GetBodyReader().ReadUInt32());
					Closed++;
					using (var writer = context.CreateReplyWriter(null)) context.Reply(writer.CreateMessage());
					break;
				default:
					context.ReplyUnknownMethodError();
					break;
			}
			return ValueTask.CompletedTask;
		}
	}

	private static void Emit(DBusConnection connection, string action)
	{
		using var writer = connection.GetMessageWriter();
		writer.WriteSignalHeader(path: "/org/freedesktop/Notifications", @interface: Service, member: "ActionInvoked", signature: "us");
		writer.WriteUInt32(7);
		writer.WriteString(action);
		Assert.True(connection.TrySendMessage(writer.CreateMessage()));
	}

	[LinuxNotificationBusFact]
	public async Task 真实协议收发过滤伪造信号且撤回绑定原服务而非重用编号的新服务()
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
		using DBusConnection original = new(DBusAddress.Session!), replacement = new(DBusAddress.Session!);
		await original.ConnectAsync();
		await replacement.ConnectAsync();
		Server first = new(), second = new();
		original.AddMethodHandler(first);
		replacement.AddMethodHandler(second);
		await original.RequestNameAsync(Service);
		using LinuxApprovalNotification notification = new();
		Channel<LinuxNotificationSignal> signals = Channel.CreateUnbounded<LinuxNotificationSignal>();
		uint id = await notification.SendAsync(new ApprovalNotice
		{
			RequestId = "approval-bus", ToolName = "<tool>", ArgumentSummary = "secret", Description = "private",
		}, true, signal => signals.Writer.TryWrite(signal), timeout.Token);
		Assert.Equal(7u, id);
		Assert.True(notification.SupportsActions);
		var received = await first.Received.Reader.ReadAsync(timeout.Token);
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
}
