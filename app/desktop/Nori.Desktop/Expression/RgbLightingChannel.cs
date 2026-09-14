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
	private bool _probing;

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

	/// <summary>
	/// 有没有可控设备。
	///
	/// **绝不在这里做阻塞 I/O。** 这个属性会被 <c>BuildSnapshot</c> 调到，而快照在应用里到处
	/// 都在建；同步去连一次 TCP（超时 2 秒）会让每次建快照都可能卡住两秒。第一版就是这么
	/// 写的，CI 上两个时序敏感的界面用例因此稳定失败 —— 单元测试发现不了，因为它们不建快照。
	///
	/// 读缓存，顺便在后台触发一次刷新；缓存为空时如实返回 false，下一次就有结果了。
	/// </summary>
	public bool IsAvailable
	{
		get
		{
			EnsureFresh();
			lock (_gate) return _devices.Count > 0;
		}
	}

	/// <summary>当前探测到的设备，供设置页显示。与 <see cref="IsAvailable"/> 一样只读缓存。</summary>
	public IReadOnlyList<RgbDevice> Devices
	{
		get
		{
			EnsureFresh();
			lock (_gate) return _devices;
		}
	}

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
			_probing = false;
		}
	}

	/// <inheritdoc />
	public Task ApplyAsync(ExpressionPalette palette, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(palette);
		Color color = ExpressionColors.Parse(palette.Primary);

		// 下发本来就跑在协调器的异步路径上，这里同步做 I/O 不阻塞界面。
		lock (_gate)
		{
			if (_client is not {} client || _devices.Count == 0) return Task.CompletedTask;
			foreach (RgbDevice device in _devices)
			{
				cancellationToken.ThrowIfCancellationRequested();
				client.SetColor(device, color);
			}
		}

		return Task.CompletedTask;
	}

	/// <summary>
	/// 缓存过期时在**后台**探一次，立刻返回不等结果。
	///
	/// 同一时刻只允许一次探测在飞：连不上时每次要走满超时，并发放行会堆起一串等待中的连接。
	/// </summary>
	private void EnsureFresh()
	{
		lock (_gate)
		{
			if (_probing || DateTimeOffset.UtcNow - _probedAt < ProbeInterval) return;
			_probing = true;
			_probedAt = DateTimeOffset.UtcNow;
		}

		_ = Task.Run(Probe);
	}

	/// <summary>
	/// 探一次并发布结果。
	///
	/// **I/O 在锁外做，锁只用来发布。** 持锁去连 TCP 的话，读缓存的 IsAvailable 会堵在锁上，
	/// 改成异步探测就等于白改 —— 阻塞只是从「同步调用」挪到了「等锁」。
	/// </summary>
	private void Probe()
	{
		OpenRgbClient? client;
		lock (_gate) client = _client;

		IReadOnlyList<RgbDevice> devices = [];
		bool broken = false;
		try
		{
			client ??= _connect();
			devices = client?.ListDevices() ?? [];
		}
		catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException
			or ObjectDisposedException)
		{
			// 服务端退出了：丢掉连接，下个周期重连。
			broken = true;
		}

		lock (_gate)
		{
			if (broken)
			{
				client?.Dispose();
				_client = null;
				_devices = [];
			}
			else
			{
				_client = client;
				_devices = devices;
			}

			_probing = false;
		}
	}

	/// <inheritdoc />
	public void Dispose() => Invalidate();
}
