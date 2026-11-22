using Nori.Core.Voice;
using Nori.Core.Voice.Audio;
using Nori.Desktop.Audio.Linux;
using Nori.Desktop.Audio.Mac;
using Nori.Desktop.Audio.Windows;

namespace Nori.Desktop.Audio;

/// <summary>
/// 按平台挑设备实现。
///
/// Windows 走 WASAPI，macOS 走 AudioQueue，Linux 走 ALSA 的 <c>default</c> 设备。
/// 设备是每次播放或录音新建的，不是长期持有：
/// 用户中途换了默认输出时，下一句话自然用新设备；
/// 一台机器上没有可用设备时，失败只影响这一次，不影响应用启动。
/// </summary>
internal static class NativeAudioFactory
{
	internal static IAudioPlayback CreatePlayback(Func<string?>? outputDeviceId = null) =>
		new NativeAudioPlayback(() => OpenOutput(outputDeviceId?.Invoke()), Decode);

	internal static IMicrophoneRecorder CreateRecorder(CaptureOwnership? ownership = null) =>
		new NativeMicrophoneRecorder(OpenCapture, ownership);

	internal static NativeAudioDeviceManager CreateDeviceManager() => new();

	internal static IAudioCaptureDevice OpenCapture() => OpenCapture(null);

	internal static IAudioCaptureDevice OpenCapture(string? deviceId = null)
	{
		deviceId = NativeAudioDeviceManager.NormalizeDeviceId(deviceId);
		if (OperatingSystem.IsWindows()) return new WasapiCaptureDevice(deviceId: deviceId);
		if (OperatingSystem.IsMacOS()) return new CoreAudioCaptureDevice(deviceId);
		if (OperatingSystem.IsLinux()) return new AlsaCaptureDevice(deviceId);
		throw new AudioDeviceException("这个平台还没有原生输入设备实现");
	}

	internal static IAudioDevice OpenOutput() => OpenOutput(null);

	internal static IAudioDevice OpenOutput(string? deviceId = null)
	{
		deviceId = NativeAudioDeviceManager.NormalizeDeviceId(deviceId);
		if (OperatingSystem.IsWindows()) return new WasapiAudioDevice(deviceId);
		if (OperatingSystem.IsMacOS()) return new CoreAudioOutputDevice(deviceId);
		if (OperatingSystem.IsLinux()) return new AlsaAudioDevice(deviceId);
		throw new AudioDeviceException("这个平台还没有原生输出设备实现");
	}

	/// <summary>
	/// 按实际内容解码 WAV，不以 MIME 声明代替 RIFF/WAVE 检查。
	/// 非 WAV 只拒绝当前段，后续 WAV 仍可正常播放。
	/// </summary>
	internal static PcmAudio Decode(ReadOnlyMemory<byte> bytes, string mime)
	{
		if (WaveDecoder.IsWave(bytes.Span)) return WaveDecoder.Decode(bytes.Span);
		throw new AudioDecodeException(
			$"原生音频后端仅支持 WAV，收到的是 {mime}。请将 TTS 服务（自定义 HTTP 在服务端）配置为输出 PCM WAV。");
	}
}
