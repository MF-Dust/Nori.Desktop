using Nori.Core.Voice.Audio;

namespace Nori.Desktop.Audio.Linux;

/// <summary>ALSA PCM 的可替换边界。测试注入替身，真机走 <see cref="AlsaNativeFactory"/>。</summary>
internal interface IAlsaPcm : IDisposable
{
	AudioFormat Format { get; }

	/// <summary>等待设备可读写。1 表示就绪，0 表示超时，负数表示已停止或失败。</summary>
	int Wait(int milliseconds);

	/// <summary>写入交错 16 位样本，返回写入的帧数。</summary>
	int Write(ReadOnlySpan<short> interleaved);

	/// <summary>读出交错 16 位样本，返回读到的帧数。</summary>
	int Read(Span<short> interleaved);

	void Drop();

	/// <summary>非阻塞地推进播放收尾；缓冲尚未播完时返回 false。</summary>
	bool TryDrain();
}

/// <summary>按一次播放或录音打开 ALSA 设备。</summary>
internal interface IAlsaFactory
{
	IAlsaPcm OpenOutput(int sampleRate, int channels);

	IAlsaPcm OpenInput(int sampleRate, int channels);
}
