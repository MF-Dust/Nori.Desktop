using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nori.Core.Voice.Audio;

namespace Nori.Desktop.Audio.Windows;

/// <summary>
/// Windows 的输入设备，走 WASAPI 共享模式。
///
/// 与输出侧对称，只有两处不同：
/// - 采集是**按包**取的。<c>GetNextPacketSize</c> 说这一包多少帧，必须整包取整包还，
///   不能只取一半 —— 剩下的不会留到下次，会直接丢。
/// - 驱动可能标记某一包为 <c>AUDCLNT_BUFFERFLAGS_SILENT</c>，此时缓冲里的内容
///   **没有意义**，要按静音处理而不是照搬。照搬多半也是零，但这条不保证。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiCaptureDevice(bool loopback = false) : IAudioCaptureDevice
{
	/// <summary>AUDCLNT_STREAMFLAGS_LOOPBACK：从**输出**端点采到正在播的东西。</summary>
	private const uint FlagLoopback = 0x00020000;

	/// <summary>采集缓冲时长。够长以免掉包，又不至于让停止不跟手。</summary>
	private const int BufferMilliseconds = 200;

	/// <summary>没有新包时睡多久再看。</summary>
	private const int PollMilliseconds = 10;

	/// <summary>AUDCLNT_BUFFERFLAGS_SILENT。</summary>
	private const uint FlagSilent = 0x2;

	/// <summary>当前用的是哪个输入设备。查「没声音」时第一条要看的。</summary>
	internal string DeviceName { get; private set; } = "";

	private readonly Lock _gate = new();
	private readonly ManualResetEventSlim _wake = new(false);

	private WasapiNativeApi.IAudioClient? _client;
	private WasapiNativeApi.IAudioCaptureClient? _capture;
	private int _channels;
	private bool _floatFormat;
	private volatile bool _stopped;
	private bool _started;

	/// <summary>
	/// 驱动把多少包标成了静音。
	///
	/// 用来分「麦克风被静音/没授权」和「采集代码写错了」—— 两者的现象都是一段全零的
	/// 录音，但前者驱动会明说。查「录进来没声音」时这是第一条要看的。
	/// </summary>
	internal int SilentPackets { get; private set; }

	/// <summary>一共取了多少包。</summary>
	internal int TotalPackets { get; private set; }

	/// <summary>上一包没取完的余料。包是整取整还的，取不下的要自己留着。</summary>
	private float[] _pending = [];
	private int _pendingAt;

	/// <inheritdoc />
	public AudioFormat Open()
	{
		try
		{
			WasapiNativeApi.IMmDeviceEnumerator enumerator =
				(WasapiNativeApi.IMmDeviceEnumerator) new WasapiNativeApi.MmDeviceEnumerator();
			// 环回模式下要开的是**输出**端点：采的是正在播的东西。
			// 它的用处是自证采集链路 —— 播一段已知的音再采回来比对，
			// 不必依赖「麦克风此刻有没有信号」这种环境条件。
			enumerator.GetDefaultAudioEndpoint(
				loopback ? WasapiNativeApi.DataFlowRender : WasapiNativeApi.DataFlowCapture,
				WasapiNativeApi.RoleConsole,
				out WasapiNativeApi.IMmDevice device);
			DeviceName = WasapiNativeApi.FriendlyName(device);

			Guid clientId = WasapiNativeApi.IidAudioClient;
			device.Activate(ref clientId, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out object clientObject);
			WasapiNativeApi.IAudioClient client = (WasapiNativeApi.IAudioClient) clientObject;

			client.GetMixFormat(out IntPtr mixFormat);
			try
			{
				AudioFormat actual = ReadFormat(mixFormat);
				client.Initialize(
					WasapiNativeApi.ShareModeShared, loopback ? FlagLoopback : 0,
					BufferMilliseconds * WasapiNativeApi.HundredNanosecondsPerMillisecond, 0,
					mixFormat, IntPtr.Zero);

				Guid captureId = WasapiNativeApi.IidAudioCaptureClient;
				client.GetService(ref captureId, out object captureObject);

				lock (_gate)
				{
					_client = client;
					_capture = (WasapiNativeApi.IAudioCaptureClient) captureObject;
					_channels = actual.Channels;
				}
				client.Start();
				_started = true;
				return actual;
			}
			finally
			{
				WasapiNativeApi.CoTaskMemFree(mixFormat);
			}
		}
		catch (Exception failure)
		{
			throw new AudioDeviceException($"打不开输入设备：{failure.Message}", failure);
		}
	}

	private AudioFormat ReadFormat(IntPtr mixFormat)
	{
		WasapiNativeApi.WaveFormatEx format = Marshal.PtrToStructure<WasapiNativeApi.WaveFormatEx>(mixFormat);
		ushort tag = format.FormatTag;
		if (tag == WasapiNativeApi.FormatExtensible && format.ExtraSize >= 22)
		{
			Guid subFormat = Marshal.PtrToStructure<Guid>(mixFormat + WasapiNativeApi.SubFormatOffset);
			_floatFormat = subFormat == WasapiNativeApi.SubtypeIeeeFloat;
		}
		else
		{
			_floatFormat = tag == WasapiNativeApi.FormatFloat;
		}

		if (!_floatFormat && format.BitsPerSample != 16)
			throw new AudioDeviceException($"不支持的采集格式：tag={tag} bits={format.BitsPerSample}");

		return new AudioFormat((int) format.SamplesPerSecond, format.Channels);
	}

	/// <inheritdoc />
	public int Read(Span<float> buffer, CancellationToken cancellationToken)
	{
		if (buffer.IsEmpty) return 0;

		// 先把上一包的余料倒出来。
		if (_pendingAt < _pending.Length)
		{
			int fromPending = Math.Min(buffer.Length, _pending.Length - _pendingAt);
			_pending.AsSpan(_pendingAt, fromPending).CopyTo(buffer);
			_pendingAt += fromPending;
			return fromPending;
		}

		WasapiNativeApi.IAudioCaptureClient capture;
		lock (_gate)
		{
			if (_capture is null) throw new AudioDeviceException("设备尚未打开");
			capture = _capture;
		}

		while (!_stopped && !cancellationToken.IsCancellationRequested)
		{
			capture.GetNextPacketSize(out uint packetFrames);
			if (packetFrames == 0)
			{
				_wake.Wait(PollMilliseconds, cancellationToken);
				_wake.Reset();
				continue;
			}

			capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
			try
			{
				int samples = (int) frames * _channels;
				float[] packet = new float[samples];
				TotalPackets++;
				// 驱动标了 SILENT 时缓冲内容没有意义，按静音处理。
				if ((flags & FlagSilent) != 0) SilentPackets++;
				else CopyOut(data, packet);

				int take = Math.Min(buffer.Length, samples);
				packet.AsSpan(0, take).CopyTo(buffer);
				// 整包必须整还，取不下的自己留着 —— 留不住就等于丢了。
				_pending = packet;
				_pendingAt = take;
				return take;
			}
			finally
			{
				capture.ReleaseBuffer(frames);
			}
		}
		return 0;
	}

	private void CopyOut(IntPtr data, Span<float> target)
	{
		if (_floatFormat)
		{
			unsafe
			{
				float* source = (float*) data;
				for (int index = 0; index < target.Length; index++) target[index] = source[index];
			}
			return;
		}

		unsafe
		{
			short* source = (short*) data;
			for (int index = 0; index < target.Length; index++) target[index] = source[index] / 32768f;
		}
	}

	/// <inheritdoc />
	public void Stop()
	{
		_stopped = true;
		_wake.Set();

		WasapiNativeApi.IAudioClient? client;
		lock (_gate) client = _client;
		try
		{
			if (client is not null && _started) client.Stop();
		}
		catch (COMException)
		{
			// 设备已经被拔掉/失效。
		}
	}

	public void Dispose()
	{
		Stop();
		lock (_gate)
		{
			if (_capture is not null) Marshal.FinalReleaseComObject(_capture);
			if (_client is not null) Marshal.FinalReleaseComObject(_client);
			_capture = null;
			_client = null;
		}
		_wake.Dispose();
	}
}
