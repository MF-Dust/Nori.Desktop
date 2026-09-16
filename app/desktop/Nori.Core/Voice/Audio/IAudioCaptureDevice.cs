namespace Nori.Core.Voice.Audio;

/// <summary>
/// 输入设备。与 <see cref="IAudioDevice"/> 对称，只是方向反过来。
///
/// 同样做成拉模型而不是回调：录音的编排（攒缓冲、下混、重采样、封 WAV）因此全是
/// 普通托管代码，拿假设备就能测完。
/// </summary>
public interface IAudioCaptureDevice : IDisposable
{
	/// <summary>
	/// 打开默认输入设备，返回它**实际**给出的格式。
	///
	/// 不要求设备按我们想要的采样率工作：麦克风常固定在 44100 或 48000，
	/// 而我们要的是 16000 单声道。转换由上层做，理由同输出侧 —— 那段代码可测。
	/// </summary>
	AudioFormat Open();

	/// <summary>
	/// 读一批交错样本。没有新数据时阻塞，直到有、或被 <see cref="Stop"/> 打断。
	///
	/// 返回读到的样本数；0 表示被打断。
	/// </summary>
	int Read(Span<float> buffer, CancellationToken cancellationToken);

	/// <summary>立刻停，并让阻塞中的 <see cref="Read"/> 返回。</summary>
	void Stop();
}
