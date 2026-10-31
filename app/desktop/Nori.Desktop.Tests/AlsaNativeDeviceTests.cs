using Nori.Core.Voice.Audio;
using Nori.Desktop.Audio.Linux;
using Xunit.Abstractions;

namespace Nori.Desktop.Tests;

/// <summary>仅显式开启时访问 ALSA default；测试不保存录音，播放只写入静音。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AlsaNativeFactAttribute : FactAttribute
{
	public AlsaNativeFactAttribute()
	{
		if (!System.OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("NORI_TEST_ALSA") != "1")
			Skip = "在 Linux 设置 NORI_TEST_ALSA=1 执行原生 ALSA 测试；输入测试会短暂访问默认麦克风。";
	}
}

public sealed class AlsaNativeDeviceTests(ITestOutputHelper output)
{
	[AlsaNativeFact]
	public async Task DefaultOutputWritesDrainsAndStops()
	{
		using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
		await Task.Run(() =>
		{
			foreach (int milliseconds in new[] { 10, 250 })
			{
				using AlsaAudioDevice device = new();
				output.WriteLine($"打开 ALSA default 输出设备，播放 {milliseconds} 毫秒静音");
				AudioFormat format = device.Open(48_000, 1);
				output.WriteLine("输出设备已打开");
				Assert.True(format.SampleRate > 0);
				Assert.True(format.Channels > 0);
				float[] silence = new float[Math.Max(1, format.SampleRate * milliseconds / 1000) * format.Channels];
				Assert.Equal(silence.Length, device.Write(silence, cancellation.Token));
				output.WriteLine("静音缓冲已写入，等待播放收尾");
				device.Drain(cancellation.Token);
				output.WriteLine("播放收尾已返回");
				cancellation.Token.ThrowIfCancellationRequested();
				device.Stop();
				Assert.Equal(0, device.Write(silence, cancellation.Token));
			}
		}).WaitAsync(TimeSpan.FromSeconds(10));
	}

	[AlsaNativeFact]
	public async Task DefaultInputReadsAndStops()
	{
		using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
		await Task.Run(() =>
		{
			using AlsaCaptureDevice device = new();
			output.WriteLine("打开 ALSA default 录音设备");
			AudioFormat format = device.Open();
			output.WriteLine("录音设备已打开");
			Assert.True(format.SampleRate > 0);
			Assert.True(format.Channels > 0);
			float[] samples = new float[480 * format.Channels];
			int read = device.Read(samples, cancellation.Token);
			output.WriteLine("已读取一段采样缓冲");
			Assert.InRange(read, 1, samples.Length);
			Assert.Equal(0, read % format.Channels);
			Assert.All(samples.Take(read), sample => Assert.True(float.IsFinite(sample)));
			device.Stop();
			Assert.Equal(0, device.Read(samples, cancellation.Token));
		}).WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Theory]
	[InlineData(true, "默认输出设备", "输出设备")]
	[InlineData(false, "默认录音设备", "麦克风")]
	public void 初始化失败提供对应设备的修复提示(bool playback, string deviceName, string guidance)
	{
		AlsaNativeFactory factory = new();
		AudioDeviceException exception = Assert.Throws<AudioDeviceException>(() => playback
			? factory.OpenOutput(0, 1) : factory.OpenInput(0, 1));

		Assert.Contains(deviceName, exception.Message, StringComparison.Ordinal);
		Assert.Contains(guidance, exception.Message, StringComparison.Ordinal);
		Assert.Contains("采样率无效", exception.InnerException?.Message, StringComparison.Ordinal);
	}
}
