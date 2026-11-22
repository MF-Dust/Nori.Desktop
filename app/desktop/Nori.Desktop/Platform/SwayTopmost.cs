using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nori.Desktop.Platform;

internal static class SwayTopmost
{
	internal const uint RunCommand = 0;
	internal const uint GetTree = 4;
	private const int HeaderSize = 14;
	private const uint MaxReplySize = 16 * 1024 * 1024;
	private static ReadOnlySpan<byte> Magic => "i3-ipc"u8;

	private sealed record Node(long Id, int? Pid, string? Name, Node[]? Nodes,
		[property: JsonPropertyName("floating_nodes")] Node[]? FloatingNodes);
	private sealed record CommandResult(bool Success);

	internal static async Task<bool> TryStickAsync(string socketPath, int pid, string title, CancellationToken cancellationToken)
	{
		using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
		using NetworkStream stream = new(socket);
		await stream.WriteAsync(EncodeFrame(GetTree, ""), cancellationToken);
		byte[] tree = await ReadFrameAsync(stream, GetTree, cancellationToken);
		long container = FindContainer(tree, pid, title);
		if (container == 0) return false;

		await stream.WriteAsync(EncodeFrame(RunCommand, FormattableString.Invariant($"[con_id={container}] floating enable, sticky enable")), cancellationToken);
		byte[] reply = await ReadFrameAsync(stream, RunCommand, cancellationToken);
		if (JsonSerializer.Deserialize<CommandResult[]>(reply, JsonSerializerOptions.Web) is not [{ Success: true }, { Success: true }])
			throw new IOException("Sway 拒绝桌宠浮动或固定命令");
		return true;
	}

	internal static long FindContainer(ReadOnlySpan<byte> tree, int pid, string title) =>
		FindNode(JsonSerializer.Deserialize<Node>(tree, JsonSerializerOptions.Web), pid, title);

	private static long FindNode(Node? node, int pid, string title)
	{
		if (node is null) return 0;
		if (node.Pid == pid && node.Name == title) return node.Id;
		long id = FindChild(node.Nodes, pid, title);
		return id != 0 ? id : FindChild(node.FloatingNodes, pid, title);
	}

	private static long FindChild(Node[]? nodes, int pid, string title)
	{
		if (nodes is null) return 0;
		foreach (Node node in nodes)
		{
			long id = FindNode(node, pid, title);
			if (id != 0) return id;
		}
		return 0;
	}

	internal static byte[] EncodeFrame(uint type, string payload)
	{
		int length = Encoding.UTF8.GetByteCount(payload);
		byte[] frame = new byte[HeaderSize + length];
		Magic.CopyTo(frame);
		// i3-ipc 使用主机字节序，长度是 UTF-8 字节数。
		BitConverter.TryWriteBytes(frame.AsSpan(6), (uint)length);
		BitConverter.TryWriteBytes(frame.AsSpan(10), type);
		Encoding.UTF8.GetBytes(payload.AsSpan(), frame.AsSpan(HeaderSize));
		return frame;
	}

	internal static async Task<byte[]> ReadFrameAsync(Stream stream, uint expectedType, CancellationToken cancellationToken)
	{
		byte[] header = new byte[HeaderSize];
		await stream.ReadExactlyAsync(header, cancellationToken);
		uint length = BitConverter.ToUInt32(header, 6);
		if (!header.AsSpan(0, 6).SequenceEqual(Magic) || BitConverter.ToUInt32(header, 10) != expectedType || length > MaxReplySize)
			throw new IOException("Sway IPC 响应帧头无效");
		byte[] payload = new byte[length];
		await stream.ReadExactlyAsync(payload, cancellationToken);
		return payload;
	}
}
