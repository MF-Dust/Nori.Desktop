using System.Runtime.InteropServices;
using Nori.Core.Voice.Audio;

namespace Nori.Desktop.Audio.Linux;

/// <summary>
/// 打开名为 <c>default</c> 的 ALSA 设备。
///
/// <c>default</c> 走 dmix 或 PipeWire-ALSA，不打开 <c>hw:</c>，避免把整块声卡占住。
/// 采样率和声道用 near 协商，实际值交回上层重采样。
/// </summary>
internal sealed class AlsaNativeFactory : IAlsaFactory
{
	internal const string MissingLibraryMessage = "未找到 libasound.so.2。请安装 libasound2（Ubuntu 24.04 起为 libasound2t64）后再试。";

	public IAlsaPcm OpenOutput(int sampleRate, int channels) => Open(playback: true, sampleRate, channels);

	public IAlsaPcm OpenInput(int sampleRate, int channels) => Open(playback: false, sampleRate, channels);

	internal static AudioDeviceException MissingLibrary(Exception? inner = null) =>
		new(MissingLibraryMessage, inner);

	private static IAlsaPcm Open(bool playback, int sampleRate, int channels)
	{
		try
		{
			return new AlsaPcm(playback, sampleRate, channels);
		}
		catch (DllNotFoundException exception)
		{
			throw MissingLibrary(exception);
		}
		catch (EntryPointNotFoundException exception)
		{
			throw MissingLibrary(exception);
		}
	}
}

/// <summary>一次 <c>snd_pcm_open</c> 的生命周期。</summary>
internal sealed class AlsaPcm : IAlsaPcm
{
	private const string Library = "libasound.so.2";
	private const int StreamPlayback = 0;
	private const int StreamCapture = 1;
	private const int AccessInterleaved = 3;
	private const int FormatS16Le = 2;

	private IntPtr _handle;
	private readonly int _channels;

	internal AlsaPcm(bool playback, int sampleRate, int channels)
	{
		if (sampleRate < 1) throw new AudioDeviceException("采样率无效");
		if (channels < 1) throw new AudioDeviceException("声道数无效");
		int opened = snd_pcm_open(out _handle, "default", playback ? StreamPlayback : StreamCapture, 0);
		if (opened < 0 || _handle == IntPtr.Zero)
			throw new AudioDeviceException($"打不开 ALSA 设备 default：{ErrorText(opened)}");

		IntPtr parameters = IntPtr.Zero;
		try
		{
			Check(snd_pcm_hw_params_malloc(out parameters), "分配 ALSA 参数");
			Check(snd_pcm_hw_params_any(_handle, parameters), "读取 ALSA 参数");
			Check(snd_pcm_hw_params_set_access(_handle, parameters, AccessInterleaved), "设置 ALSA 访问方式");
			Check(snd_pcm_hw_params_set_format(_handle, parameters, FormatS16Le), "设置 ALSA 采样格式");
			uint actualChannels = (uint) channels;
			Check(snd_pcm_hw_params_set_channels_near(_handle, parameters, ref actualChannels), "设置 ALSA 声道");
			uint actualRate = (uint) sampleRate;
			int direction = 0;
			Check(snd_pcm_hw_params_set_rate_near(_handle, parameters, ref actualRate, ref direction), "设置 ALSA 采样率");
			uint bufferTime = 200_000;
			direction = 0;
			snd_pcm_hw_params_set_buffer_time_near(_handle, parameters, ref bufferTime, ref direction);
			Check(snd_pcm_hw_params(_handle, parameters), "应用 ALSA 参数");
			Check(snd_pcm_hw_params_get_channels(parameters, out actualChannels), "读取 ALSA 声道");
			direction = 0;
			Check(snd_pcm_hw_params_get_rate(parameters, out actualRate, ref direction), "读取 ALSA 采样率");
			Check(snd_pcm_prepare(_handle), "准备 ALSA 设备");
			if (actualChannels < 1 || actualRate < 1)
				throw new AudioDeviceException("ALSA 返回了无效的采样格式");
			_channels = (int) actualChannels;
			Format = new AudioFormat((int) actualRate, _channels);
		}
		catch
		{
			if (parameters != IntPtr.Zero) snd_pcm_hw_params_free(parameters);
			Dispose();
			throw;
		}
		snd_pcm_hw_params_free(parameters);
	}

	public AudioFormat Format { get; }

	public int Wait(int milliseconds)
	{
		if (_handle == IntPtr.Zero) return -1;
		int code = snd_pcm_wait(_handle, milliseconds);
		if (code > 0) return 1;
		if (code == 0) return 0;
		int recovered = snd_pcm_recover(_handle, code, 1);
		if (recovered < 0) throw new AudioDeviceException($"等待 ALSA 设备失败：{ErrorText(recovered)}");
		return 0;
	}

	public int Write(ReadOnlySpan<short> interleaved)
	{
		if (_handle == IntPtr.Zero || interleaved.IsEmpty) return 0;
		int frames = interleaved.Length / _channels;
		if (frames <= 0) return 0;
		unsafe
		{
			fixed (short* samples = interleaved)
			{
				nint written = snd_pcm_writei(_handle, (IntPtr) samples, (nuint) frames);
				if (written < 0)
				{
					int recovered = snd_pcm_recover(_handle, (int) written, 1);
					if (recovered < 0)
						throw new AudioDeviceException($"输出设备写入失败：{ErrorText((int) written)}");
					return 0;
				}
				return (int) written;
			}
		}
	}

	public int Read(Span<short> interleaved)
	{
		if (_handle == IntPtr.Zero || interleaved.IsEmpty) return 0;
		int frames = interleaved.Length / _channels;
		if (frames <= 0) return 0;
		unsafe
		{
			fixed (short* samples = interleaved)
			{
				nint read = snd_pcm_readi(_handle, (IntPtr) samples, (nuint) frames);
				if (read < 0)
				{
					int recovered = snd_pcm_recover(_handle, (int) read, 1);
					if (recovered < 0)
						throw new AudioDeviceException($"输入设备读取失败：{ErrorText((int) read)}");
					return 0;
				}
				return (int) read;
			}
		}
	}

	public void Drop()
	{
		if (_handle != IntPtr.Zero) snd_pcm_drop(_handle);
	}

	public void Drain()
	{
		if (_handle != IntPtr.Zero) snd_pcm_drain(_handle);
	}

	public void Dispose()
	{
		if (_handle == IntPtr.Zero) return;
		snd_pcm_drop(_handle);
		snd_pcm_close(_handle);
		_handle = IntPtr.Zero;
	}

	private static void Check(int code, string action)
	{
		if (code >= 0) return;
		throw new AudioDeviceException($"{action}失败：{ErrorText(code)}");
	}

	private static string ErrorText(int code)
	{
		try
		{
			IntPtr text = snd_strerror(code);
			string? message = text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);
			return string.IsNullOrWhiteSpace(message) ? code.ToString() : message;
		}
		catch (DllNotFoundException)
		{
			return code.ToString();
		}
	}

	[DllImport(Library)]
	private static extern int snd_pcm_open(out IntPtr pcm, string name, int stream, int mode);

	[DllImport(Library)]
	private static extern int snd_pcm_close(IntPtr pcm);

	[DllImport(Library)]
	private static extern int snd_pcm_prepare(IntPtr pcm);

	[DllImport(Library)]
	private static extern int snd_pcm_drop(IntPtr pcm);

	[DllImport(Library)]
	private static extern int snd_pcm_drain(IntPtr pcm);

	[DllImport(Library)]
	private static extern int snd_pcm_wait(IntPtr pcm, int timeout);

	[DllImport(Library)]
	private static extern int snd_pcm_recover(IntPtr pcm, int error, int silent);

	[DllImport(Library)]
	private static extern nint snd_pcm_writei(IntPtr pcm, IntPtr buffer, nuint frames);

	[DllImport(Library)]
	private static extern nint snd_pcm_readi(IntPtr pcm, IntPtr buffer, nuint frames);

	[DllImport(Library)]
	private static extern int snd_pcm_hw_params_malloc(out IntPtr parameters);

	[DllImport(Library)]
	private static extern void snd_pcm_hw_params_free(IntPtr parameters);

	[DllImport(Library)]
	private static extern int snd_pcm_hw_params_any(IntPtr pcm, IntPtr parameters);

	[DllImport(Library)]
	private static extern int snd_pcm_hw_params_set_access(IntPtr pcm, IntPtr parameters, int access);

	[DllImport(Library)]
	private static extern int snd_pcm_hw_params_set_format(IntPtr pcm, IntPtr parameters, int format);

	[DllImport(Library)]
	private static extern int snd_pcm_hw_params_set_channels_near(IntPtr pcm, IntPtr parameters, ref uint channels);

	[DllImport(Library)]
	private static extern int snd_pcm_hw_params_set_rate_near(IntPtr pcm, IntPtr parameters, ref uint rate, ref int direction);

	[DllImport(Library)]
	private static extern int snd_pcm_hw_params_set_buffer_time_near(IntPtr pcm, IntPtr parameters, ref uint microseconds, ref int direction);

	[DllImport(Library)]
	private static extern int snd_pcm_hw_params(IntPtr pcm, IntPtr parameters);

	[DllImport(Library)]
	private static extern int snd_pcm_hw_params_get_channels(IntPtr parameters, out uint channels);

	[DllImport(Library)]
	private static extern int snd_pcm_hw_params_get_rate(IntPtr parameters, out uint rate, ref int direction);

	[DllImport(Library)]
	private static extern IntPtr snd_strerror(int error);
}
