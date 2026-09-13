using Avalonia.Media;
using Nori.Core.Expression;

namespace Nori.Desktop.Expression;

/// <summary>
/// 灯效设备随情绪变色。
///
/// 这是覆盖面最窄的一条通道 —— 要用户装了 OpenRGB 并且开着它的服务端。但它是她**离开屏幕**
/// 的少数途径之一：你不看显示器，也能从机箱或键盘的颜色知道她在。
///
/// 探测策略与 `nvidia-smi` 相反：**首次失败后仍然重试**。没有 N 卡的机器不会中途长出一张卡，
/// 但用户很可能在 Nori 起来之后才打开 OpenRGB；首次失败就放弃的话，他开了软件却发现功能
/// 还是灰的，只能重启 Nori。重试的成本可以忽略 —— 回环上连接被拒是立刻返回的。
/// </summary>
public sealed class RgbLightingChannel : IExpressionChannel, IDisposable
{
	/// <summary>设置项键名。</summary>
	public const string ChannelKey = "expression_rgb";

	/// <summary>重新探测的间隔。</summary>
	public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);

	private readonly Func<OpenRgbClient?> _connect;
	private readonly Lock _gate = new();
	private OpenRgbClient? _client;
	private IReadOnlyList<RgbDevice> _devices = [];
	private DateTimeOffset _probedAt = DateTimeOffset.MinValue;

	/// <summary>创建通道。</summary>
	/// <param name="connect">建立连接；连不上返回 null。</param>
	public RgbLightingChannel(Func<OpenRgbClient?>? connect = null) =>
		_connect = connect ?? (() => OpenRgbClient.TryConnect());

	/// <inheritdoc />
	public string Key => ChannelKey;

	/// <summary>
	/// 侵入等级 Peripheral 而非 Global。
	///
	/// 它不改你屏幕上的任何东西，只改外设的灯 —— 装了 OpenRGB 的人本来就是主动要灯效的，
	/// 默认开是合理的。
	/// </summary>
	public Intrusiveness Level => Intrusiveness.Peripheral;

	/// <inheritdoc />
	public bool IsAvailable => Probe().Count > 0;

	/// <summary>当前探测到的设备，供设置页显示。</summary>
	public IReadOnlyList<RgbDevice> Devices => Probe();

	/// <summary>
	/// 丢掉当前连接，下次访问时重新探测。
	///
	/// 用户刚打开 OpenRGB、刚插了新键盘时由 `refreshDevices` 调到这里 —— 不必等下一个探测周期。
	/// </summary>
	public void Invalidate()
	{
		lock (_gate)
		{
			_client?.Dispose();
			_client = null;
			_devices = [];
			_probedAt = DateTimeOffset.MinValue;
		}
	}

	/// <inheritdoc />
	public Task ApplyAsync(ExpressionPalette palette, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(palette);
		IReadOnlyList<RgbDevice> devices = Probe();
		if (devices.Count == 0) return Task.CompletedTask;

		Color color = ExpressionColors.Parse(palette.Primary);
		lock (_gate)
		{
			if (_client is not {} client) return Task.CompletedTask;
			foreach (RgbDevice device in devices)
			{
				cancellationToken.ThrowIfCancellationRequested();
				client.SetColor(device, color);
			}
		}

		return Task.CompletedTask;
	}

	private IReadOnlyList<RgbDevice> Probe()
	{
		lock (_gate)
		{
			if (DateTimeOffset.UtcNow - _probedAt < ProbeInterval) return _devices;
			_probedAt = DateTimeOffset.UtcNow;

			try
			{
				_client ??= _connect();
				_devices = _client?.ListDevices() ?? [];
			}
			catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException
				or ObjectDisposedException)
			{
				// 服务端退出了：丢掉连接，下个周期重连。
				_client?.Dispose();
				_client = null;
				_devices = [];
			}

			return _devices;
		}
	}

	/// <inheritdoc />
	public void Dispose() => Invalidate();
}
