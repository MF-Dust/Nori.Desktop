using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nori.Desktop.Audio.Windows;
using Nori.Core.Voice.Audio;

namespace Nori.Desktop.Tests;

/// <summary>用托管 COM 替身校验同步边界和样本写入，不依赖真实声卡。</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioDeviceTests
{
	private sealed class FakeClient : WasapiNativeApi.IAudioClient, WasapiNativeApi.IAudioRenderClient, IDisposable
	{
		public IntPtr Buffer { get; } = Marshal.AllocHGlobal(256);
		public Action CheckAccess { get; set; } = () => { };
		public Action? BufferEntered { get; set; }
		public Action? PaddingRead { get; set; }
		public uint Padding { get; set; }
		public COMException? PaddingFailure { get; set; }
		public int Starts { get; private set; }
		public int Stops { get; private set; }
		public int Resets { get; private set; }
		public int Releases { get; private set; }

		public void GetCurrentPadding(out uint paddingFrames)
		{
			CheckAccess();
			if (PaddingFailure is { } failure) throw failure;
			PaddingRead?.Invoke();
			paddingFrames = Padding;
		}

		public void GetBuffer(uint requestedFrames, out IntPtr buffer)
		{
			CheckAccess();
			BufferEntered?.Invoke();
			buffer = Buffer;
		}

		public void ReleaseBuffer(uint writtenFrames, uint flags) { CheckAccess(); Releases++; }
		public void Start() { CheckAccess(); Starts++; }
		public void Stop() { CheckAccess(); Stops++; }
		public void Reset() { CheckAccess(); Resets++; }
		public void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid) => throw new NotSupportedException();
		public void GetBufferSize(out uint bufferFrameCount) => throw new NotSupportedException();
		public void GetStreamLatency(out long latency) => throw new NotSupportedException();
		public void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch) => throw new NotSupportedException();
		public void GetMixFormat(out IntPtr deviceFormat) => throw new NotSupportedException();
		public void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod) => throw new NotSupportedException();
		public void SetEventHandle(IntPtr eventHandle) => throw new NotSupportedException();
		public void GetService(ref Guid iid, out object instance) => throw new NotSupportedException();
		public void Dispose() => Marshal.FreeHGlobal(Buffer);
	}

	private static FieldInfo Field(string name) =>
		typeof(WasapiAudioDevice).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

	private static WasapiAudioDevice Device(FakeClient client, bool floatFormat = true)
	{
		WasapiAudioDevice device = new();
		Field("_client").SetValue(device, client);
		Field("_render").SetValue(device, client);
		Field("_channels").SetValue(device, 1);
		Field("_bufferFrames").SetValue(device, 64u);
		Field("_floatFormat").SetValue(device, floatFormat);
		Lock gate = (Lock) Field("_gate").GetValue(device)!;
		// Dispose 在同一把锁内释放 RCW；所有使用 RCW 的调用必须持有它。
		client.CheckAccess = () => Assert.True(gate.IsHeldByCurrentThread, "COM 调用未与释放互斥");
		return device;
	}

	private static void DisposeDevice(WasapiAudioDevice device)
	{
		// 托管替身不是 RCW，不能交给 FinalReleaseComObject。
		Field("_client").SetValue(device, null);
		Field("_render").SetValue(device, null);
		device.Dispose();
		device.Dispose();
		device.Stop();
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void 写入排空停止共用释放锁且保留软件音量(bool floatFormat)
	{
		using FakeClient client = new();
		WasapiAudioDevice device = Device(client, floatFormat);
		try
		{
			device.Volume = 0.5;
			Assert.Equal(3, device.Write([0.5f, -0.5f, 4f], CancellationToken.None));
			if (floatFormat)
			{
				float[] samples = new float[3];
				Marshal.Copy(client.Buffer, samples, 0, samples.Length);
				Assert.Equal([0.25f, -0.25f, 2f], samples);
			}
			else
			{
				short[] samples = new short[3];
				Marshal.Copy(client.Buffer, samples, 0, samples.Length);
				Assert.Equal([(short) 8192, (short) -8192, short.MaxValue], samples);
			}
			device.Drain(CancellationToken.None);
			device.Stop();
			device.Stop();
			Assert.Equal(1, client.Starts);
			Assert.Equal(1, client.Stops);
			Assert.Equal(1, client.Resets);
			Assert.Equal(1, client.Releases);
		}
		finally
		{
			DisposeDevice(device);
		}
	}

	[Fact]
	public async Task 停止先唤醒再等待写入归还缓冲()
	{
		using FakeClient client = new();
		using ManualResetEventSlim release = new(false);
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		WasapiAudioDevice device = Device(client);
		device.Write([0.5f], CancellationToken.None);
		client.BufferEntered = () =>
		{
			entered.SetResult();
			Assert.True(release.Wait(TimeSpan.FromSeconds(5)), "未放行 COM 缓冲");
		};
		Task writing = Task.Run(() => device.Write(new float[] {0.5f}, CancellationToken.None));
		Task stopping = Task.CompletedTask;
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
			stopping = Task.Run(device.Stop);
			ManualResetEventSlim wake = (ManualResetEventSlim) Field("_wake").GetValue(device)!;
			Assert.True(wake.Wait(TimeSpan.FromSeconds(3)), "Stop 尚未唤醒写入");
			Assert.Equal(0, client.Stops);
			Assert.Equal(1, client.Releases);
		}
		finally
		{
			release.Set();
			await Task.WhenAll(writing, stopping).WaitAsync(TimeSpan.FromSeconds(3));
			DisposeDevice(device);
		}
		Assert.Equal(2, client.Releases);
		Assert.Equal(1, client.Stops);
		Assert.Equal(1, client.Resets);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task 停止打断缓冲已满的写入或排空(bool drain)
	{
		using FakeClient client = new();
		WasapiAudioDevice device = Device(client);
		device.Write([0.5f], CancellationToken.None);
		client.Padding = 64;
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		client.PaddingRead = () => entered.TrySetResult();
		Task pumping = Task.Run(() =>
		{
			if (drain) device.Drain(CancellationToken.None);
			else Assert.Equal(0, device.Write(new float[] {0.5f}, CancellationToken.None));
		});
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
			await Task.Run(device.Stop).WaitAsync(TimeSpan.FromSeconds(3));
			await pumping.WaitAsync(TimeSpan.FromSeconds(3));
			Assert.Equal(1, client.Stops);
		}
		finally
		{
			device.Stop();
			await pumping.WaitAsync(TimeSpan.FromSeconds(3));
			DisposeDevice(device);
		}
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void 设备拔出转为音频设备异常(bool drain)
	{
		using FakeClient client = new();
		WasapiAudioDevice device = Device(client);
		device.Write([0.5f], CancellationToken.None);
		client.PaddingFailure = new COMException("设备已失效", unchecked((int) 0x88890004));
		try
		{
			AudioDeviceException failure = Assert.Throws<AudioDeviceException>(() =>
			{
				if (drain) device.Drain(CancellationToken.None);
				else device.Write(new float[] {0.5f}, CancellationToken.None);
			});
			Assert.Same(client.PaddingFailure, failure.InnerException);
		}
		finally
		{
			DisposeDevice(device);
		}
	}

	[Fact]
	public void 释放后不能重新打开设备()
	{
		using WasapiAudioDevice device = new();
		device.Dispose();
		Assert.Throws<ObjectDisposedException>(() => device.Open(48000, 2));
	}
}
