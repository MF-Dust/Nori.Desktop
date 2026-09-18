using Nori.Core.Voice;
using Nori.Core.Voice.Audio;
using Nori.Desktop.Audio.Windows;

namespace Nori.Desktop.Audio;

/// <summary>
/// 按平台挑设备实现。
///
/// 眼下只有 Windows 一条（WASAPI）；mac 与 Linux 的设备还没做，那两个平台由
/// <see cref="AudioBackend.PrefersNative"/> 判成走 WebView，走不到这里。
/// 之后补 CoreAudio / ALSA 时，改的只有这个文件。
///
/// 设备是**每次播放/录音新建**的，不是长期持有：
/// - 用户中途换了默认输出（插上耳机）时下一句话自然用新设备，不必监听设备变更；
/// - 一台机器上没有任何输出设备时，失败只影响这一次，不影响应用启动。
/// </summary>
internal static class NativeAudioFactory
{
	internal static IAudioPlayback CreatePlayback() =>
		new NativeAudioPlayback(OpenOutput, Decode);

	internal static IMicrophoneRecorder CreateRecorder() =>
		new NativeMicrophoneRecorder(OpenInput);

	private static IAudioDevice OpenOutput() => OperatingSystem.IsWindows()
		? new WasapiAudioDevice()
		: throw new AudioDeviceException("这个平台还没有原生输出设备实现");

	private static IAudioCaptureDevice OpenInput() => OperatingSystem.IsWindows()
		? new WasapiCaptureDevice()
		: throw new AudioDeviceException("这个平台还没有原生输入设备实现");

	/// <summary>
	/// 按实际内容解码 WAV，不以 MIME 声明代替 RIFF/WAVE 检查。
	/// 非 WAV 只拒绝当前段，不更换后端或自动创建 WebView；后续 WAV 仍可正常播放。
	/// </summary>
	internal static PcmAudio Decode(ReadOnlyMemory<byte> bytes, string mime)
	{
		if (WaveDecoder.IsWave(bytes.Span)) return WaveDecoder.Decode(bytes.Span);
		throw new AudioDecodeException(
			$"原生音频后端仅支持 WAV，收到的是 {mime}。请将 TTS 服务（自定义 HTTP 在服务端）配置为输出 PCM WAV；"
			+ "或显式设置 audio_backend=webview 并重启应用，使用兼容音频后端。");
	}
}
