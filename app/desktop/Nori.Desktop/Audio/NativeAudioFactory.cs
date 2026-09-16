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
	/// 解码。
	///
	/// 只认 WAV：本地 GPT-SoVITS 直接返回 wav，而 OpenAI 的 /audio/speech 支持
	/// response_format —— 那个请求体是我们自己拼的。也就是说我们控制得了的链路
	/// 都能只出 wav。别的格式抛出去，由调用方报「这段放不了」而不是静默无声。
	/// </summary>
	private static PcmAudio Decode(ReadOnlyMemory<byte> bytes, string mime)
	{
		if (WaveDecoder.IsWave(bytes.Span)) return WaveDecoder.Decode(bytes.Span);
		throw new AudioDecodeException($"原生音频后端目前只解 WAV，收到的是 {mime}");
	}
}
