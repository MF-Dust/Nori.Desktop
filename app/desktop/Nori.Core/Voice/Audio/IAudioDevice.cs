namespace Nori.Core.Voice.Audio;

/// <summary>
/// 输出设备。**整个原生音频后端里唯一必须用原生代码实现的东西。**
///
/// 刻意做成推模型（我们往里写，写不进去就阻塞）而不是回调模型：
/// - 推模型的播放编排（解码、切块、发电平、停止、收尾）全是普通托管代码，
///   拿一个假设备就能测完，不必等真声卡；
/// - WASAPI 的「取缓冲 → 填 → 交还」和 miniaudio 的环形缓冲都能实现成推模型，
///   反过来把回调模型包成推模型也要一个环形缓冲，代价一样。
///
/// 实现方只需管四件事：按给定格式打开、把样本写进去、丢掉没播完的、关掉。
/// 重采样不在这一层 —— 设备接受什么采样率由 <see cref="Open"/> 的返回值说了算。
/// </summary>
public interface IAudioDevice : IDisposable
{
	/// <summary>
	/// 按目标格式打开设备。
	///
	/// 返回设备**实际**接受的格式：多数声卡固定跑在 48000，送 22050 的语音进去
	/// 要么由驱动重采样要么变调。返回实际值，让上层决定自己重采样还是接受。
	/// </summary>
	AudioFormat Open(int sampleRate, int channels);

	/// <summary>
	/// 写一批交错样本。设备缓冲满时阻塞，直到写得进去或被 <see cref="Stop"/> 打断。
	///
	/// 返回真正写进去的样本数：被打断时可能少于传入的长度。
	/// </summary>
	int Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken);

	/// <summary>等设备把已经写进去的样本播完。</summary>
	void Drain(CancellationToken cancellationToken);

	/// <summary>立刻停：丢掉没播完的，并让阻塞中的 <see cref="Write"/> 返回。</summary>
	void Stop();

	/// <summary>输出音量 0..1。设备不支持时由实现自己在样本上乘。</summary>
	double Volume { get; set; }
}

/// <summary>设备实际接受的格式。</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels);

/// <summary>打不开设备时抛这个，调用方据此降级成「这台机器上没有声音」而不是崩掉。</summary>
public sealed class AudioDeviceException(string message, Exception? inner = null)
	: Exception(message, inner);
