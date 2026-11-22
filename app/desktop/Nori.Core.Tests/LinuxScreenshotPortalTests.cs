using Tmds.DBus.Protocol;

namespace Nori.Core.Tests;

public sealed class LinuxScreenshotPortalTests
{
	private const string Portal = "org.freedesktop.portal.Desktop";
	private const string Screenshot = "org.freedesktop.portal.Screenshot";

	[PrivateDBusFact]
	public async Task 截图请求在方法回包之前接住匹配请求路径的响应()
	{
		await using PrivateDBus bus = await PrivateDBus.StartAsync();
		string file = Path.Combine(bus.Directory, "截 屏.png");
		Assert.Equal(file, await RequestAsync(bus, 0, new Uri(file).AbsoluteUri));
	}

	[PrivateDBusFact]
	public async Task Portal分别报告取消和失败且不会接受其中的文件路径()
	{
		await using PrivateDBus bus = await PrivateDBus.StartAsync();
		foreach ((uint code, string message) in new[] { (1u, "用户已取消系统截屏"), (2u, "系统截屏失败或被拒绝") })
		{
			InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
				() => RequestAsync(bus, code, "file:///tmp/not-this-request.png"));
			Assert.Equal(message, exception.Message);
		}
	}

	[PrivateDBusFact]
	public async Task Portal拒绝远程文件地址()
	{
		await using PrivateDBus bus = await PrivateDBus.StartAsync();
		InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
			() => RequestAsync(bus, 0, "file://example.org/tmp/remote.png"));
		Assert.Equal("截屏 Portal 未返回本地截图文件", exception.Message);
	}

	private static async Task<string> RequestAsync(PrivateDBus bus, uint code, string uri)
	{
		using DBusConnection service = new(bus.Address);
		await service.ConnectAsync();
		Server server = new(service, code, uri);
		service.AddMethodHandler(server);
		await service.RequestNameAsync(Portal);
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
		try { return await Platform.LinuxScreenshotPortal.RequestFileAsync(bus.Address, timeout.Token); }
		finally { Assert.True(server.Interactive); }
	}

	private sealed class Server(DBusConnection connection, uint code, string uri) : IPathMethodHandler
	{
		public string Path => "/org/freedesktop/portal/desktop";
		public bool HandlesChildPaths => false;
		internal bool Interactive { get; private set; }

		public ValueTask HandleMethodAsync(MethodContext context)
		{
			var reader = context.Request.GetBodyReader();
			if (context.Request.InterfaceAsString == "org.freedesktop.DBus.Properties" && context.Request.MemberAsString == "Get"
				&& reader.ReadString() == Screenshot && reader.ReadString() == "version")
			{
				using var version = context.CreateReplyWriter("v");
				version.WriteVariant(VariantValue.UInt32(2));
				context.Reply(version.CreateMessage());
			}
			else if (context.Request.InterfaceAsString == Screenshot && context.Request.MemberAsString == "Screenshot")
			{
				string parent = reader.ReadString();
				var options = reader.ReadDictionaryOfStringToVariantValue();
				Interactive = parent == "" && options["interactive"].GetBool();
				string sender = context.Request.SenderAsString!;
				string token = options["handle_token"].GetString();
				string request = $"{Path}/request/{sender[1..].Replace('.', '_')}/{token}";
				// 同一服务发出的另一个请求不能被消费；真正的响应有意在方法回包前发送。
				Emit(sender, request + "_other", 0, "file:///tmp/wrong.png");
				Emit(sender, request, code, uri);
				using var reply = context.CreateReplyWriter("o");
				reply.WriteObjectPath(request);
				context.Reply(reply.CreateMessage());
			}
			else context.ReplyUnknownMethodError();
			return ValueTask.CompletedTask;
		}

		private void Emit(string destination, string request, uint response, string file)
		{
			using var writer = connection.GetMessageWriter();
			writer.WriteSignalHeader(destination: destination, path: request, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
			writer.WriteUInt32(response);
			writer.WriteDictionary(new Dictionary<string, VariantValue> { ["uri"] = VariantValue.String(file) });
			connection.TrySendMessage(writer.CreateMessage());
		}
	}
}
