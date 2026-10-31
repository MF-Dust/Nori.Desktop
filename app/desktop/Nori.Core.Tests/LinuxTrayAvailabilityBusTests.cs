using System.Globalization;
using Nori.Core.Platform;
using Tmds.DBus.Protocol;

namespace Nori.Core.Tests;

/// <summary>在私有 dbus-daemon 上验证托盘宿主监听：信号驱动、复用单条连接、缺少监视器不报错。</summary>
public sealed class LinuxTrayAvailabilityBusTests
{
	private const string Watcher = "org.kde.StatusNotifierWatcher";

	private sealed class FakeWatcher : IPathMethodHandler
	{
		private int _hostRegistered;
		private int _queries;
		public string Path => "/StatusNotifierWatcher";
		public bool HandlesChildPaths => false;
		internal bool HostRegistered
		{
			get => Volatile.Read(ref _hostRegistered) != 0;
			set => Volatile.Write(ref _hostRegistered, value ? 1 : 0);
		}
		internal int Queries => Volatile.Read(ref _queries);

		public ValueTask HandleMethodAsync(MethodContext context)
		{
			var reader = context.Request.GetBodyReader();
			if (context.Request.InterfaceAsString == "org.freedesktop.DBus.Properties" && context.Request.MemberAsString == "Get"
				&& reader.ReadString() == Watcher && reader.ReadString() == "IsStatusNotifierHostRegistered")
			{
				Interlocked.Increment(ref _queries);
				using var writer = context.CreateReplyWriter("v");
				writer.WriteVariantBool(HostRegistered);
				context.Reply(writer.CreateMessage());
			}
			else context.ReplyUnknownMethodError();
			return ValueTask.CompletedTask;
		}
	}

	private static void Emit(DBusConnection connection, string member)
	{
		using var writer = connection.GetMessageWriter();
		writer.WriteSignalHeader(path: "/StatusNotifierWatcher", @interface: Watcher, member: member);
		Assert.True(connection.TrySendMessage(writer.CreateMessage()));
	}

	/// <summary>dbus-daemon 按连接顺序分配 :1.N，序号差即期间新建的连接数。</summary>
	private static int ConnectionSerial(DBusConnection connection) =>
		int.Parse(connection.UniqueName!.Split('.')[^1], CultureInfo.InvariantCulture);

	[PrivateDBusFact]
	public async Task 缺少监视器时单次查询返回不可用而不是报错()
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
		await using PrivateDBus bus = await PrivateDBus.StartAsync();
		Assert.False(await LinuxTrayAvailability.CheckAsync(bus.Address, timeout.Token));
		Assert.False(await LinuxTrayAvailability.CheckAsync(address: null, timeout.Token));
	}

	[PrivateDBusFact]
	public async Task 监听在单条连接上跟随监视器易主与宿主注册信号()
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
		await using PrivateDBus bus = await PrivateDBus.StartAsync();
		await using IAsyncEnumerator<bool> values = LinuxTrayAvailability.WatchAsync(bus.Address, timeout.Token).GetAsyncEnumerator(timeout.Token);
		Assert.True(await values.MoveNextAsync());
		Assert.False(values.Current);

		using DBusConnection service = new(bus.Address);
		await service.ConnectAsync();
		FakeWatcher watcher = new() { HostRegistered = true };
		service.AddMethodHandler(watcher);
		using DBusConnection before = new(bus.Address);
		await before.ConnectAsync();

		await service.RequestNameAsync(Watcher);
		Assert.True(await values.MoveNextAsync());
		Assert.True(values.Current);

		watcher.HostRegistered = false;
		Emit(service, "StatusNotifierHostUnregistered");
		Assert.True(await values.MoveNextAsync());
		Assert.False(values.Current);

		watcher.HostRegistered = true;
		Emit(service, "StatusNotifierHostRegistered");
		Assert.True(await values.MoveNextAsync());
		Assert.True(values.Current);

		await service.ReleaseNameAsync(Watcher);
		Assert.True(await values.MoveNextAsync());
		Assert.False(values.Current);

		// 四次重查都复用监听自己的连接：这期间总线上只多出了下面这一条。
		using DBusConnection after = new(bus.Address);
		await after.ConnectAsync();
		Assert.Equal(ConnectionSerial(before) + 1, ConnectionSerial(after));
		Assert.Equal(3, watcher.Queries);
	}

	[PrivateDBusFact]
	public async Task 总线断开时监听抛出而不是静默结束()
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
		PrivateDBus bus = await PrivateDBus.StartAsync();
		IAsyncEnumerator<bool> values = LinuxTrayAvailability.WatchAsync(bus.Address, timeout.Token).GetAsyncEnumerator(timeout.Token);
		try
		{
			Assert.True(await values.MoveNextAsync());
			Assert.False(values.Current);
			await bus.DisposeAsync();
			Exception failure = await Assert.ThrowsAnyAsync<Exception>(async () => await values.MoveNextAsync());
			Assert.False(failure is OperationCanceledException, failure.ToString());
		}
		finally
		{
			await values.DisposeAsync();
			await bus.DisposeAsync();
		}
	}
}
