using System.Buffers;
using System.Runtime.InteropServices;
using Nori.Core.Voice.Audio;

namespace Nori.Desktop.Audio.Mac;

/// <summary>
/// macOS 共享模式输出/输入，走 AudioToolbox 的 AudioQueue。
///
/// 共享模式由 AudioQueue 保证：不会独占声卡。回调只填已分配的缓冲，
/// 阻塞和停止醒在 <see cref="CoreAudioOutputDevice"/> 的环形缓冲里。
/// </summary>
internal sealed class CoreAudioQueue(string? deviceId = null) : ICoreAudioQueue
{
	private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
	private const uint LinearPcm = 0x6C70636D;
	private const uint FloatPacked = 9;
	private const uint VolumeParameter = 1;
	private const int BufferCount = 3;

	private static readonly OutputCallback OutputThunk = OnOutput;
	private static readonly InputCallback InputThunk = OnInput;

	private readonly Lock _gate = new();
	private GCHandle _handle;
	private IntPtr _queue;
	private Func<Span<float>, int>? _render;
	private Action<ReadOnlySpan<float>>? _capture;
	private bool _started;
	private bool _disposed;

	public AudioFormat OpenOutput(int sampleRate, int channels, Func<Span<float>, int> render)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		Abort();
		_render = render;
		try
		{
			AudioFormat format = CreateQueue(output: true, sampleRate, channels, OutputThunk);
			PrimeOutputBuffers(format);
			return format;
		}
		catch
		{
			Abort();
			throw;
		}
	}

	public AudioFormat OpenInput(Action<ReadOnlySpan<float>> capture)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		Abort();
		_capture = capture;
		try
		{
			AudioFormat format = CreateQueue(output: false, 48000, 1, InputThunk);
			PrimeInputBuffers(format);
			return format;
		}
		catch
		{
			Abort();
			throw;
		}
	}

	public void SetVolume(double volume)
	{
		lock (_gate)
		{
			if (_queue == IntPtr.Zero) return;
			float value = (float) Math.Clamp(volume, 0, 1);
			int status = AudioQueueSetParameter(_queue, VolumeParameter, value);
			if (status != 0) throw new AudioDeviceException($"设置输出音量失败：{status}");
		}
	}

	public void Start()
	{
		lock (_gate)
		{
			if (_queue == IntPtr.Zero || _started) return;
			int status = AudioQueueStart(_queue, IntPtr.Zero);
			if (status != 0) throw QueueFailure(_capture is null ? AudioDeviceDirection.Output : AudioDeviceDirection.Input, status);
			_started = true;
		}
	}

	public void Finish()
	{
		lock (_gate)
		{
			if (_queue == IntPtr.Zero || !_started) return;
			AudioQueueStop(_queue, 0);
			_started = false;
		}
	}

	public void StopImmediate()
	{
		lock (_gate)
		{
			if (_queue == IntPtr.Zero || !_started) return;
			AudioQueueStop(_queue, 1);
			_started = false;
		}
	}

	public void Abort()
	{
		lock (_gate)
		{
			if (_disposed) return;
			ReleaseQueueLocked();
		}
		FreeHandle();
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			ReleaseQueueLocked();
		}
		FreeHandle();
	}

	private void ReleaseQueueLocked()
	{
		_render = null;
		_capture = null;
		if (_queue != IntPtr.Zero)
		{
			if (_started) AudioQueueStop(_queue, 1);
			AudioQueueDispose(_queue, 1);
			_queue = IntPtr.Zero;
		}
		_started = false;
	}

	private void FreeHandle()
	{
		if (_handle.IsAllocated) _handle.Free();
	}

	private AudioFormat CreateQueue(bool output, int sampleRate, int channels, Delegate callback)
	{
		if (!OperatingSystem.IsMacOS()) throw new AudioDeviceException("未找到 AudioToolbox。macOS 系统音频组件不可用。");
		AudioDeviceDirection direction = output ? AudioDeviceDirection.Output : AudioDeviceDirection.Input;
		AudioFormat hardware;
		if (deviceId is null)
		{
			hardware = CoreAudioDeviceCatalog.GetDefaultFormat(direction)
				?? new AudioFormat(Math.Max(1, sampleRate), Math.Max(1, channels));
		}
		else
		{
			AudioDeviceInfo? device = CoreAudioDeviceCatalog.Enumerate(direction).FirstOrDefault(device => device.Id == deviceId);
			if (device?.DefaultSampleRate is not int rate || device.DefaultChannels is not int count)
				throw new AudioDeviceException("所选 CoreAudio 设备不存在、已断开或不可用，请重新选择设备。");
			hardware = new AudioFormat(rate, count);
		}
		AudioStreamBasicDescription description = FloatFormat(hardware);
		_handle = GCHandle.Alloc(this);
		int status = output
			? AudioQueueNewOutput(ref description, (OutputCallback) callback, GCHandle.ToIntPtr(_handle), IntPtr.Zero, IntPtr.Zero, 0, out _queue)
			: AudioQueueNewInput(ref description, (InputCallback) callback, GCHandle.ToIntPtr(_handle), IntPtr.Zero, IntPtr.Zero, 0, out _queue);
		if (status != 0 || _queue == IntPtr.Zero)
		{
			if (_queue != IntPtr.Zero)
			{
				AudioQueueDispose(_queue, 1);
				_queue = IntPtr.Zero;
			}
			FreeHandle();
			throw QueueFailure(direction, status);
		}
		if (deviceId is not null)
		{
			nint uid = CoreAudioDeviceCatalog.CreateDeviceUid(deviceId);
			if (uid == 0) throw new AudioDeviceException("无法读取所选 CoreAudio 设备标识。");
			try
			{
				status = AudioQueueSetProperty(_queue, 0x61716364, ref uid, (uint) IntPtr.Size); // aqcd
				if (status != 0) throw QueueFailure(direction, status);
			}
			finally { CoreAudioDeviceCatalog.CFRelease(uid); }
		}
		return hardware;
	}

	private void PrimeOutputBuffers(AudioFormat format)
	{
		int bytes = BytesPerBuffer(format);
		for (int index = 0; index < BufferCount; index++)
		{
			int status = AudioQueueAllocateBuffer(_queue, (uint) bytes, out IntPtr buffer);
			if (status != 0) throw new AudioDeviceException($"分配输出缓冲失败：{status}");
			Render(_queue, buffer);
		}
	}

	private void PrimeInputBuffers(AudioFormat format)
	{
		int bytes = BytesPerBuffer(format);
		for (int index = 0; index < BufferCount; index++)
		{
			int status = AudioQueueAllocateBuffer(_queue, (uint) bytes, out IntPtr buffer);
			if (status != 0) throw new AudioDeviceException($"分配输入缓冲失败：{status}");
			status = AudioQueueEnqueueBuffer(_queue, buffer, 0, IntPtr.Zero);
			if (status != 0) throw new AudioDeviceException($"提交输入缓冲失败：{status}");
		}
	}

	private int BytesPerBuffer(AudioFormat format)
	{
		int frames = Math.Max(1, format.SampleRate / 20);
		return frames * format.Channels * sizeof(float);
	}

	private void Render(IntPtr queue, IntPtr buffer)
	{
		AudioQueueBufferHeader header = Marshal.PtrToStructure<AudioQueueBufferHeader>(buffer);
		int samples = (int) (header.AudioDataBytesCapacity / sizeof(float));
		if (samples <= 0 || header.AudioData == IntPtr.Zero) return;
		float[] rented = ArrayPool<float>.Shared.Rent(samples);
		try
		{
			Span<float> destination = rented.AsSpan(0, samples);
			try
			{
				int filled = _render?.Invoke(destination) ?? 0;
				if (filled < samples) destination[Math.Max(0, filled)..].Clear();
			}
			catch (Exception)
			{
				destination.Clear();
			}
			Marshal.Copy(rented, 0, header.AudioData, samples);
		}
		finally
		{
			ArrayPool<float>.Shared.Return(rented);
		}
		Marshal.WriteInt32(buffer, 16, (int) header.AudioDataBytesCapacity);
		AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
	}

	private void Capture(IntPtr queue, IntPtr buffer)
	{
		AudioQueueBufferHeader header = Marshal.PtrToStructure<AudioQueueBufferHeader>(buffer);
		int samples = (int) (header.AudioDataByteSize / sizeof(float));
		if (samples > 0 && header.AudioData != IntPtr.Zero && _capture is not null)
		{
			float[] rented = ArrayPool<float>.Shared.Rent(samples);
			try
			{
				Marshal.Copy(header.AudioData, rented, 0, samples);
				_capture(rented.AsSpan(0, samples));
			}
			catch (Exception)
			{
				// 采集回调不能把异常扔回声卡线程。
			}
			finally
			{
				ArrayPool<float>.Shared.Return(rented);
			}
		}
		AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
	}

	private static void OnOutput(IntPtr userData, IntPtr queue, IntPtr buffer)
	{
		if (GCHandle.FromIntPtr(userData).Target is CoreAudioQueue self) self.Render(queue, buffer);
	}

	private static void OnInput(IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr startTime, uint packetCount, IntPtr packets)
	{
		_ = startTime;
		_ = packetCount;
		_ = packets;
		if (GCHandle.FromIntPtr(userData).Target is CoreAudioQueue self) self.Capture(queue, buffer);
	}

	private static AudioStreamBasicDescription FloatFormat(AudioFormat format)
	{
		uint bytesPerFrame = (uint) (format.Channels * sizeof(float));
		return new AudioStreamBasicDescription
		{
			SampleRate = format.SampleRate,
			FormatId = LinearPcm,
			FormatFlags = FloatPacked,
			BytesPerPacket = bytesPerFrame,
			FramesPerPacket = 1,
			BytesPerFrame = bytesPerFrame,
			ChannelsPerFrame = (uint) format.Channels,
			BitsPerChannel = 32,
		};
	}

	private AudioDeviceException QueueFailure(AudioDeviceDirection direction, int status)
	{
		bool input = direction == AudioDeviceDirection.Input;
		string kind = input ? "输入" : "输出";
		string permission = input ? CoreAudioDeviceCatalog.MicrophonePermissionNote : "";
		return new AudioDeviceException($"无法打开或启动 CoreAudio {kind}设备，请确认设备仍可用。{permission}（AudioQueue {status}）")
		{
			IsBusy = OperatingSystem.IsMacOS() && CoreAudioDeviceCatalog.IsHeldByAnotherProcess(direction, deviceId),
		};
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct AudioStreamBasicDescription
	{
		public double SampleRate;
		public uint FormatId;
		public uint FormatFlags;
		public uint BytesPerPacket;
		public uint FramesPerPacket;
		public uint BytesPerFrame;
		public uint ChannelsPerFrame;
		public uint BitsPerChannel;
		public uint Reserved;
	}

	[StructLayout(LayoutKind.Explicit, Size = 24)]
	private struct AudioQueueBufferHeader
	{
		[FieldOffset(0)] public uint AudioDataBytesCapacity;
		[FieldOffset(8)] public IntPtr AudioData;
		[FieldOffset(16)] public uint AudioDataByteSize;
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void OutputCallback(IntPtr userData, IntPtr queue, IntPtr buffer);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void InputCallback(
		IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr startTime, uint packetCount, IntPtr packets);

	[DllImport(AudioToolbox)]
	private static extern int AudioQueueNewOutput(
		ref AudioStreamBasicDescription format, OutputCallback callback, IntPtr userData,
		IntPtr callbackRunLoop, IntPtr callbackRunLoopMode, uint flags, out IntPtr queue);

	[DllImport(AudioToolbox)]
	private static extern int AudioQueueNewInput(
		ref AudioStreamBasicDescription format, InputCallback callback, IntPtr userData,
		IntPtr callbackRunLoop, IntPtr callbackRunLoopMode, uint flags, out IntPtr queue);

	[DllImport(AudioToolbox)]
	private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint byteSize, out IntPtr buffer);

	[DllImport(AudioToolbox)]
	private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetDescriptions, IntPtr packetDescription);

	[DllImport(AudioToolbox)]
	private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);

	[DllImport(AudioToolbox)]
	private static extern int AudioQueueStop(IntPtr queue, byte immediate);

	[DllImport(AudioToolbox)]
	private static extern int AudioQueueDispose(IntPtr queue, byte immediate);

	[DllImport(AudioToolbox)]
	private static extern int AudioQueueSetParameter(IntPtr queue, uint parameter, float value);

	[DllImport(AudioToolbox)]
	private static extern int AudioQueueSetProperty(IntPtr queue, uint property, ref nint value, uint size);
}
