using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Avalonia.Media;

namespace Nori.Desktop.Expression;

/// <summary>一台可控灯效设备。</summary>
public sealed record RgbDevice
{
	/// <summary>OpenRGB 里的设备序号，控制时按它寻址。</summary>
	public required int Index { get; init; }

	/// <summary>设备名，例如「ROG Strix Keyboard」。</summary>
	public required string Name { get; init; }

	/// <summary>灯珠数量。控制时要按这个数量发同样多的颜色。</summary>
	public required int LedCount { get; init; }
}

/// <summary>
/// OpenRGB SDK 的最小客户端。
///
/// 选 OpenRGB 而不是各家厂商 SDK：一套协议覆盖多数厂商，而且驱动的负担落在 OpenRGB 身上
/// 不在我们这儿 —— 主板灯效那类需要内核驱动的，由用户自己决定装不装它。
///
/// **协议实现未经真实服务端验证。** 因此解析全程带边界检查，任何字段不一致就把该设备判为
/// 不可用，绝不拿半解析的结果去写硬件。最坏情况是「没检测到设备」，不是「发了乱七八糟的
/// 数据给键盘」。
/// </summary>
public sealed class OpenRgbClient : IDisposable
{
	/// <summary>OpenRGB SDK 服务端的默认端口。</summary>
	public const int DefaultPort = 6742;

	/// <summary>包头魔数。</summary>
	private static readonly byte[] Magic = "ORGB"u8.ToArray();

	private const int HeaderSize = 16;
	private const uint RequestControllerCount = 0;
	private const uint RequestControllerData = 1;
	private const uint SetClientName = 50;
	private const uint UpdateLeds = 1050;
	private const uint SetCustomMode = 1100;

	/// <summary>本客户端声明的协议版本。</summary>
	private const uint ProtocolVersion = 1;

	/// <summary>单个响应的最大字节数，防御性上限。</summary>
	private const int MaxPayload = 4 * 1024 * 1024;

	/// <summary>连接与读写超时。连不上是常态（用户没开 OpenRGB），不能让它拖住调用方。</summary>
	public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

	private readonly TcpClient _tcp;
	private readonly NetworkStream _stream;

	private OpenRgbClient(TcpClient tcp)
	{
		_tcp = tcp;
		_stream = tcp.GetStream();
	}

	/// <summary>
	/// 连服务端。连不上返回 null 而不是抛 —— 用户没装或没开 OpenRGB 是常态而不是故障。
	/// </summary>
	public static OpenRgbClient? TryConnect(int port = DefaultPort)
	{
		TcpClient tcp = new();
		try
		{
			// 回环上连接被拒是立刻返回的，不会走满超时。
			if (!tcp.ConnectAsync("127.0.0.1", port).Wait(Timeout) || !tcp.Connected)
			{
				tcp.Dispose();
				return null;
			}

			tcp.ReceiveTimeout = (int)Timeout.TotalMilliseconds;
			tcp.SendTimeout = (int)Timeout.TotalMilliseconds;
			OpenRgbClient client = new(tcp);
			client.Send(0, SetClientName, Encoding.ASCII.GetBytes("Nori\0"));
			return client;
		}
		catch (Exception exception) when (exception is SocketException or IOException or AggregateException)
		{
			tcp.Dispose();
			return null;
		}
	}

	/// <summary>枚举可控设备。任何一台解析不出来就跳过它，不影响其余设备。</summary>
	public IReadOnlyList<RgbDevice> ListDevices()
	{
		Send(0, RequestControllerCount, []);
		byte[] countPayload = Receive();
		if (countPayload.Length < 4) return [];

		int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(countPayload);
		if (count is < 0 or > 256) return [];

		// 请求体只是一个协议版本号，循环外准备好，不要每次迭代都分配。
		byte[] version = new byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(version, ProtocolVersion);

		List<RgbDevice> devices = [];
		for (int index = 0; index < count; index++)
		{
			Send((uint)index, RequestControllerData, version);
			if (TryParseDevice(Receive(), index) is {} device) devices.Add(device);
		}

		return devices;
	}

	/// <summary>把一台设备的全部灯珠设成同一个颜色。</summary>
	public void SetColor(RgbDevice device, Color color)
	{
		ArgumentNullException.ThrowIfNull(device);
		if (device.LedCount <= 0) return;

		// 先切到自定义模式，否则设备正在跑内置特效时颜色会被立刻覆盖掉。
		Send((uint)device.Index, SetCustomMode, []);

		byte[] payload = new byte[4 + 2 + (device.LedCount * 4)];
		BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)payload.Length);
		BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)device.LedCount);
		for (int led = 0; led < device.LedCount; led++)
		{
			int offset = 6 + (led * 4);
			payload[offset] = color.R;
			payload[offset + 1] = color.G;
			payload[offset + 2] = color.B;
			payload[offset + 3] = 0;
		}

		Send((uint)device.Index, UpdateLeds, payload);
	}

	/// <summary>
	/// 从控制器数据里取出设备名与灯珠数。
	///
	/// 只取这两项，但它们分处结构的两端，中间的模式与分区必须逐个跳过。全程带边界检查：
	/// 协议未经真实服务端验证，读越界时返回 null 把这台设备判为不可用，而不是拿错值去写硬件。
	/// </summary>
	internal static RgbDevice? TryParseDevice(byte[] payload, int index)
	{
		ArgumentNullException.ThrowIfNull(payload);
		try
		{
			int offset = 4;                       // 跳过 data_size
			offset += 4;                          // 跳过 type
			string name = ReadString(payload, ref offset);
			ReadString(payload, ref offset);      // description
			ReadString(payload, ref offset);      // version
			ReadString(payload, ref offset);      // serial
			ReadString(payload, ref offset);      // location

			int modes = ReadUInt16(payload, ref offset);
			offset += 4;                          // active_mode
			for (int mode = 0; mode < modes; mode++) SkipMode(payload, ref offset);

			int zones = ReadUInt16(payload, ref offset);
			for (int zone = 0; zone < zones; zone++) SkipZone(payload, ref offset);

			int leds = ReadUInt16(payload, ref offset);
			return leds is <= 0 or > 4096 || name.Length == 0
				? null
				: new RgbDevice {Index = index, Name = name, LedCount = leds};
		}
		catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException
			or ArgumentException or OverflowException)
		{
			return null;
		}
	}

	private static void SkipMode(byte[] payload, ref int offset)
	{
		ReadString(payload, ref offset);          // name
		offset += 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4;  // value flags speed_min speed_max colors_min colors_max speed direction
		offset += 4;                              // color_mode
		int colors = ReadUInt16(payload, ref offset);
		offset += colors * 4;
	}

	private static void SkipZone(byte[] payload, ref int offset)
	{
		ReadString(payload, ref offset);          // name
		offset += 4 + 4 + 4 + 4;                  // type leds_min leds_max leds_count
		int matrix = ReadUInt16(payload, ref offset);
		offset += matrix;
	}

	/// <summary>读 `u16 长度 + 字节`，长度含结尾的 NUL。</summary>
	private static string ReadString(byte[] payload, ref int offset)
	{
		int length = ReadUInt16(payload, ref offset);
		if (length <= 0 || offset + length > payload.Length) throw new ArgumentOutOfRangeException(nameof(payload));

		string value = Encoding.UTF8.GetString(payload, offset, length - 1);
		offset += length;
		return value;
	}

	private static int ReadUInt16(byte[] payload, ref int offset)
	{
		if (offset + 2 > payload.Length) throw new ArgumentOutOfRangeException(nameof(payload));
		int value = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));
		offset += 2;
		return value;
	}

	private void Send(uint device, uint packetId, byte[] data)
	{
		byte[] header = new byte[HeaderSize];
		Magic.CopyTo(header, 0);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), device);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), packetId);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)data.Length);

		_stream.Write(header);
		if (data.Length > 0) _stream.Write(data);
	}

	private byte[] Receive()
	{
		byte[] header = new byte[HeaderSize];
		_stream.ReadExactly(header);
		if (!header.AsSpan(0, 4).SequenceEqual(Magic)) throw new IOException("OpenRGB 响应的包头魔数不对");

		int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
		if (size is < 0 or > MaxPayload) throw new IOException($"OpenRGB 响应长度异常: {size}");
		if (size == 0) return [];

		byte[] payload = new byte[size];
		_stream.ReadExactly(payload);
		return payload;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		_stream.Dispose();
		_tcp.Dispose();
	}
}
