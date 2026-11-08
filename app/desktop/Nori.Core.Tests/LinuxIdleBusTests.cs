using Nori.Core.Platform;
using Tmds.DBus.Protocol;

namespace Nori.Core.Tests;

/// <summary>用两条私有总线确认屏保留在会话总线，logind 只走系统总线。</summary>
public sealed class LinuxIdleBusTests
{
	private const string SessionPath = "/org/freedesktop/login1/session/self";

	private sealed class ScreenSaverStub : IPathMethodHandler
	{
		internal int Calls;
		public string Path => "/org/freedesktop/ScreenSaver";
		public bool HandlesChildPaths => false;

		public ValueTask HandleMethodAsync(MethodContext context)
		{
			Interlocked.Increment(ref Calls);
			if (context.Request.MemberAsString == "GetSessionIdleTime")
				context.ReplyError("org.freedesktop.DBus.Error.NotSupported", "Wayland");
			else
				context.ReplyUnknownMethodError();
			return ValueTask.CompletedTask;
		}
	}

	private sealed class LogindStub
	{
		internal int Calls;
		internal ManagerHandler Manager { get; }
		internal SessionHandler Session { get; }

		internal LogindStub()
		{
			Manager = new ManagerHandler(this);
			Session = new SessionHandler(this);
		}

		internal sealed class ManagerHandler : IPathMethodHandler
		{
			private readonly LogindStub _owner;
			internal ManagerHandler(LogindStub owner) => _owner = owner;
			public string Path => "/org/freedesktop/login1";
			public bool HandlesChildPaths => false;

			public ValueTask HandleMethodAsync(MethodContext context)
			{
				Interlocked.Increment(ref _owner.Calls);
				if (context.Request.MemberAsString is "GetSession" or "GetSessionByPID")
				{
					using MessageWriter writer = context.CreateReplyWriter("o");
					writer.WriteObjectPath(SessionPath);
					context.Reply(writer.CreateMessage());
				}
				else
				{
					context.ReplyUnknownMethodError();
				}
				return ValueTask.CompletedTask;
			}
		}

		internal sealed class SessionHandler : IPathMethodHandler
		{
			private readonly LogindStub _owner;
			internal SessionHandler(LogindStub owner) => _owner = owner;
			public string Path => SessionPath;
			public bool HandlesChildPaths => false;

			public ValueTask HandleMethodAsync(MethodContext context)
			{
				Interlocked.Increment(ref _owner.Calls);
				if (context.Request.MemberAsString != "Get")
				{
					context.ReplyUnknownMethodError();
					return ValueTask.CompletedTask;
				}
				Reader reader = context.Request.GetBodyReader();
				reader.ReadString();
				string property = reader.ReadString();
				if (property != "IdleHint")
				{
					context.ReplyUnknownMethodError();
					return ValueTask.CompletedTask;
				}
				using MessageWriter writer = context.CreateReplyWriter("v");
				writer.WriteVariant(false);
				context.Reply(writer.CreateMessage());
				return ValueTask.CompletedTask;
			}
		}
	}

	private static async Task RegisterAsync(DBusConnection connection, params IPathMethodHandler[] handlers)
	{
		await connection.ConnectAsync();
		foreach (IPathMethodHandler handler in handlers) connection.AddMethodHandler(handler);
	}

	[PrivateDBusFact]
	public async Task 屏保不支持时空闲只从系统总线的logind读取()
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
		await using PrivateDBus session = await PrivateDBus.StartAsync();
		await using PrivateDBus system = await PrivateDBus.StartAsync();
		using DBusConnection sessionConnection = new(session.Address);
		using DBusConnection systemConnection = new(system.Address);
		ScreenSaverStub saver = new();
		LogindStub sessionLogind = new();
		LogindStub systemLogind = new();
		await RegisterAsync(sessionConnection, saver, sessionLogind.Manager, sessionLogind.Session);
		await RegisterAsync(systemConnection, systemLogind.Manager, systemLogind.Session);
		await sessionConnection.RequestNameAsync("org.freedesktop.ScreenSaver");
		await sessionConnection.RequestNameAsync("org.freedesktop.login1");
		await systemConnection.RequestNameAsync("org.freedesktop.login1");

		double? idle = await LinuxIdleTime.QueryBusesAsync(session.Address, system.Address, timeout.Token);

		Assert.Equal(0, idle);
		Assert.True(saver.Calls > 0);
		Assert.True(systemLogind.Calls > 0);
		Assert.Equal(0, sessionLogind.Calls);
	}

	[PrivateDBusFact]
	public async Task 只挂在会话总线上的logind不会被采用()
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
		await using PrivateDBus session = await PrivateDBus.StartAsync();
		await using PrivateDBus system = await PrivateDBus.StartAsync();
		using DBusConnection sessionConnection = new(session.Address);
		using DBusConnection systemConnection = new(system.Address);
		LogindStub sessionLogind = new();
		await RegisterAsync(sessionConnection, new ScreenSaverStub(), sessionLogind.Manager, sessionLogind.Session);
		await systemConnection.ConnectAsync();
		await sessionConnection.RequestNameAsync("org.freedesktop.ScreenSaver");
		await sessionConnection.RequestNameAsync("org.freedesktop.login1");

		Assert.Null(await LinuxIdleTime.QueryBusesAsync(session.Address, system.Address, timeout.Token));
		Assert.Equal(0, sessionLogind.Calls);
	}
}
