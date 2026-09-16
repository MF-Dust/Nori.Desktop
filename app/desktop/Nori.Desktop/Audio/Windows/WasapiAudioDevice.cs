using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nori.Core.Voice.Audio;

namespace Nori.Desktop.Audio.Windows;

/// <summary>
/// Windows 的输出设备，走 WASAPI 共享模式。
///
/// 共享模式而不是独占：独占能拿到更低延迟，代价是把整块声卡占住，别的程序放不了声。
/// 一个桌面伴侣说句话就把用户的音乐掐掉，是不能接受的。
///
/// 取设备的混音格式而不是让 WASAPI 帮我们转：转换归上层的
/// <see cref="NativeAudioPlayback"/> 管（它那份重采样是可测的），这里只负责把
/// 已经是设备格式的样本推进去。<see cref="Open"/> 返回设备实际接受的格式，
/// 上层据此决定要不要转。
///
/// 轮询而不是事件驱动：事件驱动省一点 CPU，但要多一个事件句柄和一个专用线程，
/// 而这里本来就跑在自己的泵线程上。轮询的代价是每 ~10ms 醒一次，对一段语音而言
/// 可以忽略。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiAudioDevice : IAudioDevice
{
	/// <summary>缓冲时长。太短会在负载高时断音，太长会让 Stop 之后还拖一截声音。</summary>
	private const int BufferMilliseconds = 200;

	/// <summary>缓冲满时睡多久再看。取设备周期的量级即可。</summary>
	private const int PollMilliseconds = 10;

	/// <summary>当前用的是哪个输出设备。查「没声音」时第一条要看的。</summary>
	internal string DeviceName { get; private set; } = "";

	private readonly Lock _gate = new();
	private readonly ManualResetEventSlim _wake = new(false);

	private WasapiNativeApi.IAudioClient? _client;
	private WasapiNativeApi.IAudioRenderClient? _render;
	private uint _bufferFrames;
	private int _channels;
	private bool _floatFormat;
	private ushort _bitsPerSample;
	private volatile bool _stopped;
	private bool _started;

	/// <inheritdoc />
	public double Volume { get; set; } = 1.0;

	/// <inheritdoc />
	public AudioFormat Open(int sampleRate, int channels)
	{
		try
		{
			WasapiNativeApi.IMmDeviceEnumerator enumerator =
				(WasapiNativeApi.IMmDeviceEnumerator) new WasapiNativeApi.MmDeviceEnumerator();
			enumerator.GetDefaultAudioEndpoint(
				WasapiNativeApi.DataFlowRender, WasapiNativeApi.RoleConsole,
				out WasapiNativeApi.IMmDevice device);
			DeviceName = WasapiNativeApi.FriendlyName(device);

			Guid clientId = WasapiNativeApi.IidAudioClient;
			device.Activate(ref clientId, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out object clientObject);
			WasapiNativeApi.IAudioClient client = (WasapiNativeApi.IAudioClient) clientObject;

			client.GetMixFormat(out IntPtr mixFormat);
			try
			{
				AudioFormat actual = ReadFormat(mixFormat);
				// 共享模式下必须按混音格式初始化；给别的格式会返回
				// AUDCLNT_E_UNSUPPORTED_FORMAT，而不是帮我们转。
				client.Initialize(
					WasapiNativeApi.ShareModeShared, 0,
					BufferMilliseconds * WasapiNativeApi.HundredNanosecondsPerMillisecond, 0,
					mixFormat, IntPtr.Zero);

				client.GetBufferSize(out _bufferFrames);
				Guid renderId = WasapiNativeApi.IidAudioRenderClient;
				client.GetService(ref renderId, out object renderObject);

				lock (_gate)
				{
					_client = client;
					_render = (WasapiNativeApi.IAudioRenderClient) renderObject;
					_channels = actual.Channels;
				}
				return actual;
			}
			finally
			{
				WasapiNativeApi.CoTaskMemFree(mixFormat);
			}
		}
		catch (Exception failure)
		{
			throw new AudioDeviceException($"打不开输出设备：{failure.Message}", failure);
		}
	}

	/// <summary>
	/// 读混音格式。
	///
	/// 共享模式下这几乎总是 32 位浮点，但不能假设 —— 有些声卡驱动给的是
	/// WAVE_FORMAT_EXTENSIBLE 包着 PCM16。判错了不会报错，只会把浮点的位模式
	/// 当整数播出去，声音是一片噪声。
	/// </summary>
	private AudioFormat ReadFormat(IntPtr mixFormat)
	{
		WasapiNativeApi.WaveFormatEx format = Marshal.PtrToStructure<WasapiNativeApi.WaveFormatEx>(mixFormat);
		_bitsPerSample = format.BitsPerSample;

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
			throw new AudioDeviceException($"不支持的混音格式：tag={tag} bits={format.BitsPerSample}");

		return new AudioFormat((int) format.SamplesPerSecond, format.Channels);
	}

	/// <inheritdoc />
	public int Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
	{
		if (samples.IsEmpty) return 0;
		WasapiNativeApi.IAudioClient client;
		WasapiNativeApi.IAudioRenderClient render;
		lock (_gate)
		{
			if (_client is null || _render is null) throw new AudioDeviceException("设备尚未打开");
			client = _client;
			render = _render;
		}

		// 第一批数据写进去之后才 Start：先 Start 会先播出一段没写过的缓冲，
		// 也就是一小段杂音。
		int frames = samples.Length / Math.Max(1, _channels);
		int writtenFrames = 0;

		while (writtenFrames < frames)
		{
			if (_stopped || cancellationToken.IsCancellationRequested) break;

			client.GetCurrentPadding(out uint padding);
			uint free = _bufferFrames - padding;
			if (free == 0)
			{
				EnsureStarted(client);
				// 缓冲满了就等。Stop 会把这个等待放出来，不必等到超时。
				_wake.Wait(PollMilliseconds, cancellationToken);
				_wake.Reset();
				continue;
			}

			int take = Math.Min((int) free, frames - writtenFrames);
			render.GetBuffer((uint) take, out IntPtr buffer);
			CopyInto(buffer, samples.Slice(writtenFrames * _channels, take * _channels));
			render.ReleaseBuffer((uint) take, 0);
			writtenFrames += take;
			EnsureStarted(client);
		}

		return writtenFrames * _channels;
	}

	private void EnsureStarted(WasapiNativeApi.IAudioClient client)
	{
		if (_started) return;
		client.Start();
		_started = true;
	}

	/// <summary>把样本拷进 WASAPI 给的缓冲，顺带乘上音量。</summary>
	private void CopyInto(IntPtr buffer, ReadOnlySpan<float> samples)
	{
		double volume = Math.Clamp(Volume, 0, 1);
		if (_floatFormat)
		{
			unsafe
			{
				float* target = (float*) buffer;
				for (int index = 0; index < samples.Length; index++)
					target[index] = (float) (samples[index] * volume);
			}
			return;
		}

		unsafe
		{
			short* target = (short*) buffer;
			for (int index = 0; index < samples.Length; index++)
			{
				// 先夹再转：越界不夹会绕回成爆音。
				double sample = Math.Clamp(samples[index] * volume, -1, 1);
				target[index] = (short) Math.Round(sample * 32767);
			}
		}
	}

	/// <inheritdoc />
	public void Drain(CancellationToken cancellationToken)
	{
		WasapiNativeApi.IAudioClient? client;
		lock (_gate) client = _client;
		if (client is null || !_started) return;

		// 等缓冲里剩的那点播完，否则 Dispose 会把尾音切掉。
		while (!_stopped && !cancellationToken.IsCancellationRequested)
		{
			client.GetCurrentPadding(out uint padding);
			if (padding == 0) break;
			_wake.Wait(PollMilliseconds, cancellationToken);
			_wake.Reset();
		}
	}

	/// <inheritdoc />
	public void Stop()
	{
		_stopped = true;
		// 先放行等待中的 Write，再动设备 —— 反过来的话那个 Write 会继续往一个
		// 正在被停掉的客户端上写。
		_wake.Set();

		WasapiNativeApi.IAudioClient? client;
		lock (_gate) client = _client;
		try
		{
			if (client is not null && _started)
			{
				client.Stop();
				client.Reset();
			}
		}
		catch (COMException)
		{
			// 设备已经被拔掉/失效。停不下来的东西也不必再停。
		}
	}

	public void Dispose()
	{
		Stop();
		lock (_gate)
		{
			if (_render is not null) Marshal.FinalReleaseComObject(_render);
			if (_client is not null) Marshal.FinalReleaseComObject(_client);
			_render = null;
			_client = null;
		}
		_wake.Dispose();
	}
}
