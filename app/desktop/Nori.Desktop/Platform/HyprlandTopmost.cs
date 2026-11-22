using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Nori.Desktop.Platform;

internal static class HyprlandTopmost
{
	private sealed record Client(int Pid, string Title, string Address, bool? Floating, bool? Pinned);

	internal static string ResolveSocketPath(string? runtimeDirectory, string signature)
	{
		if (!string.IsNullOrWhiteSpace(runtimeDirectory))
		{
			string path = Path.Combine(runtimeDirectory, "hypr", signature, ".socket.sock");
			if (File.Exists(path)) return path;
		}
		return Path.Combine("/tmp/hypr", signature, ".socket.sock");
	}

	internal static async Task<bool> TryPinAsync(string socketPath, int pid, string title, CancellationToken cancellationToken)
	{
		UnixDomainSocketEndPoint endpoint = new(socketPath);
		string response = await RequestAsync(endpoint, "j/clients", cancellationToken);
		Client? client = JsonSerializer.Deserialize<Client[]>(response, JsonSerializerOptions.Web)?
			.FirstOrDefault(client => client.Pid == pid && client.Title == title);
		if (client?.Pinned == true) return true;
		if (client is not { Pinned: false, Floating: bool floating, Address: { } address }
			|| !address.StartsWith("0x", StringComparison.Ordinal)
			|| !ulong.TryParse(address.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
			return false;

		if (!floating) await DispatchAsync(endpoint, $"setfloating address:{address}", cancellationToken);
		await DispatchAsync(endpoint, $"pin address:{address}", cancellationToken);
		return true;
	}

	private static async Task DispatchAsync(UnixDomainSocketEndPoint endpoint, string command, CancellationToken cancellationToken)
	{
		string response = await RequestAsync(endpoint, $"dispatch {command}", cancellationToken);
		if (!string.Equals(response.Trim(), "ok", StringComparison.Ordinal))
			throw new IOException("Hyprland 拒绝桌宠置顶命令");
	}

	private static async Task<string> RequestAsync(UnixDomainSocketEndPoint endpoint, string request, CancellationToken cancellationToken)
	{
		using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		await socket.ConnectAsync(endpoint, cancellationToken);
		using NetworkStream stream = new(socket);
		await stream.WriteAsync(Encoding.UTF8.GetBytes(request), cancellationToken);
		using StreamReader reader = new(stream, Encoding.UTF8);
		return await reader.ReadToEndAsync(cancellationToken);
	}
}
