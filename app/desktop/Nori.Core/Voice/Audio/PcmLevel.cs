namespace Nori.Core.Voice.Audio;

/// <summary>
/// 从 PCM 取电平，用来驱动口型。
///
/// 这是原生播放相对 WebView 唯一一处**用户看得见**的改善：现在电平在前端算完、
/// 每约 60ms 经桥回传一次，跨进程有抖动；原生可以直接在送进设备的那个缓冲上算，
/// 和听到的声音严格对齐。
///
/// 取 RMS 不取峰值：峰值对单个爆音样本过敏，嘴会一抽一抽；RMS 是这一窗的能量，
/// 和人听到的响度接近。
/// </summary>
public static class PcmLevel
{
    /// <summary>口型更新的目标间隔。比 WebView 那条的 60ms 密一档，仍远低于渲染帧率。</summary>
    public const int WindowMilliseconds = 25;

    /// <summary>
    /// 一窗的 RMS，结果压到 0..1。
    ///
    /// <paramref name="samples"/> 是交错样本；多声道取各声道的平均能量，因为口型
    /// 只有一张嘴，不分左右。
    /// </summary>
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (float sample in samples) sum += (double) sample * sample;
        return Math.Clamp(Math.Sqrt(sum / samples.Length), 0, 1);
    }

    /// <summary>一窗里有多少个交错样本。</summary>
    public static int WindowSamples(int sampleRate, int channels) =>
        Math.Max(1, sampleRate * WindowMilliseconds / 1000) * Math.Max(1, channels);

    /// <summary>
    /// 把整段音频切成窗，逐窗给出电平。
    ///
    /// 主要供测试与离线核对；实时播放时由设备回调按缓冲算，不走这条。
    /// </summary>
    public static IReadOnlyList<double> Envelope(PcmAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        int window = WindowSamples(audio.SampleRate, audio.Channels);
        List<double> levels = [];
        for (int at = 0; at < audio.Samples.Length; at += window)
            levels.Add(Rms(audio.Samples.AsSpan(at, Math.Min(window, audio.Samples.Length - at))));
        return levels;
    }

    /// <summary>
    /// 让电平看起来像嘴在动，而不是像一条抖动的曲线。
    ///
    /// 张嘴要快、闭嘴要慢：语音的起音是瞬态，收尾有余韵。两个方向用同一个系数的话，
    /// 快系数会让闭嘴时抽搐，慢系数会让张嘴跟不上音头。
    /// </summary>
    public static double Smooth(double previous, double current) =>
        current > previous
            ? previous + (current - previous) * 0.6
            : previous + (current - previous) * 0.25;
}
