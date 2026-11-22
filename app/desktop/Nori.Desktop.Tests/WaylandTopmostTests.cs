using System.Net.Sockets;
using System.Text;
using Nori.Desktop.Platform;

namespace Nori.Desktop.Tests;

public class WaylandTopmostTests
{
	private const string SwayTree = """
		{"id":1,"nodes":[{"id":2,"pid":20,"name":"Nori"},{"id":3,"pid":10,"name":"Nori Chat"},
		{"id":4,"floating_nodes":[{"id":64,"pid":10,"name":"Nori"}]}]}
		""";

	[Fact]
	public void Hyprland旧版Socket路径()
	{
		Assert.Equal("/tmp/hypr/pet-instance/.socket.sock", HyprlandTopmost.ResolveSocketPath(null, "pet-instance"));
		Assert.Equal("/tmp/hypr/pet-instance/.socket.sock", HyprlandTopmost.ResolveSocketPath("/does-not-exist", "pet-instance"));
	}

	[Theory]
	[InlineData(null, false)]
	[InlineData("true", true)]
	[InlineData("false", true, "dispatch setfloating address:0xab", "dispatch pin address:0xab")]
	[InlineData("false", true, "dispatch pin address:0xab")]
	public async Task Hyprland仅置顶匹配PID和标题的未固定窗口(string? pinned, bool found, params string[] dispatches)
	{
		if (!System.OperatingSystem.IsLinux()) return;
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
		CancellationToken token = timeout.Token;
		string runtime = Path.Combine(Path.GetTempPath(), $"nt-{Guid.NewGuid():N}");
		const string signature = "pet";
		string path = Path.Combine(runtime, "hypr", signature, ".socket.sock");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		try
		{
			using Socket server = Listen(path);
			Assert.Equal(path, HyprlandTopmost.ResolveSocketPath(runtime, signature));
			string floating = dispatches.Length == 1 ? "true" : "false";
			string match = pinned is null ? "" : $$""",{"pid":10,"title":"Nori","address":"0xab","pinned":{{pinned}},"floating":{{floating}}}""";
			string clients = """
				[{"pid":20,"title":"Nori","address":"0xcd","pinned":false,"floating":true},
				{"pid":10,"title":"Nori Chat","address":"0xef","pinned":false,"floating":true}
				""" + match + "]";

			Task serve = ServeAsync();
			Task<bool> pin = HyprlandTopmost.TryPinAsync(path, 10, "Nori", token);
			await Task.WhenAll(serve, pin);
			Assert.Equal(found, await pin);

			async Task ServeAsync()
			{
				await ExchangeAsync("j/clients", clients);
				foreach (string dispatch in dispatches) await ExchangeAsync(dispatch, "ok");
			}

			async Task ExchangeAsync(string expected, string response)
			{
				using Socket client = await server.AcceptAsync(token);
				using NetworkStream stream = new(client);
				byte[] request = new byte[Encoding.UTF8.GetByteCount(expected)];
				await stream.ReadExactlyAsync(request, token);
				Assert.Equal(expected, Encoding.UTF8.GetString(request));
				await stream.WriteAsync(Encoding.UTF8.GetBytes(response), token);
			}
		}
		finally { Directory.Delete(runtime, recursive: true); }
	}

	[Fact]
	public async Task Swayi3帧使用Magic主机字节序和UTF8字节长度()
	{
		byte[] frame = SwayTopmost.EncodeFrame(SwayTopmost.GetTree, "桌宠");
		Assert.Equal("i3-ipc", Encoding.ASCII.GetString(frame, 0, 6));
		Assert.Equal(6u, BitConverter.ToUInt32(frame, 6));
		Assert.Equal(4u, BitConverter.ToUInt32(frame, 10));
		Assert.Equal(20, frame.Length);
		using MemoryStream stream = new(frame);
		CancellationToken token = CancellationToken.None;
		Assert.Equal("桌宠"u8.ToArray(), await SwayTopmost.ReadFrameAsync(stream, 4, token));
		stream.Position = 0;
		await Assert.ThrowsAsync<IOException>(() => SwayTopmost.ReadFrameAsync(stream, 0, token));
		frame[0] = 0;
		stream.Position = 0;
		await Assert.ThrowsAsync<IOException>(() => SwayTopmost.ReadFrameAsync(stream, 4, token));
	}

	[Fact]
	public void Sway从浮动树按PID和完整标题匹配节点()
	{
		byte[] tree = Encoding.UTF8.GetBytes(SwayTree);
		Assert.Equal(64, SwayTopmost.FindContainer(tree, 10, "Nori"));
		Assert.Equal(0, SwayTopmost.FindContainer(tree, 10, "Nor"));
		Assert.Equal(0, SwayTopmost.FindContainer(tree, 11, "Nori"));
	}

	[Fact]
	public async Task Sway先通过Socket查树再按节点ID浮动固定()
	{
		if (!System.OperatingSystem.IsLinux()) return;
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
		CancellationToken token = timeout.Token;
		string path = Path.Combine(Path.GetTempPath(), $"nt-{Guid.NewGuid():N}.sock");
		using Socket server = Listen(path);
		Task serve = ServeAsync();
		Task<bool> stick = SwayTopmost.TryStickAsync(path, 10, "Nori", token);
		await Task.WhenAll(serve, stick);
		Assert.True(await stick);

		async Task ServeAsync()
		{
			using Socket client = await server.AcceptAsync(token);
			using NetworkStream stream = new(client);
			Assert.Empty(await SwayTopmost.ReadFrameAsync(stream, 4, token));
			byte[] tree = SwayTopmost.EncodeFrame(4, SwayTree);
			await stream.WriteAsync(tree.AsMemory(0, 8), token);
			await stream.WriteAsync(tree.AsMemory(8), token);
			byte[] command = await SwayTopmost.ReadFrameAsync(stream, 0, token);
			Assert.Equal("[con_id=64] floating enable, sticky enable", Encoding.UTF8.GetString(command));
			await stream.WriteAsync(SwayTopmost.EncodeFrame(0, """[{"success":true},{"success":true}]"""), token);
		}
	}

	private static Socket Listen(string path)
	{
		Socket server = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		server.Bind(new UnixDomainSocketEndPoint(path));
		server.Listen(1);
		return server;
	}
}
