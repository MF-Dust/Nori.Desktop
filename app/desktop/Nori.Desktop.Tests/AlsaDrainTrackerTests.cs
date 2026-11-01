using System.Diagnostics;
using Nori.Desktop.Audio.Linux;

namespace Nori.Desktop.Tests;

public sealed class AlsaDrainTrackerTests
{
	private const int Rate = 48_000;
	private const int Running = 3;
	// 期限边界换算成时间戳时有浮点舍入，到期断言留出 1 微秒余量。
	private const double Epsilon = 1e-6;

	private static long At(double seconds) => (long) (seconds * Stopwatch.Frequency) + Stopwatch.Frequency * 100;

	[Fact]
	public void 欠载或无剩余帧即完成且读取失败不伪装成完成()
	{
		AlsaDrainTracker tracker = new();

		Assert.Equal(AlsaDrainStep.Done, tracker.Next(AlsaPcm.Underrun, 0, -1, Rate, At(0)));
		Assert.Equal(AlsaDrainStep.Done, tracker.Next(0, 0, Running, Rate, At(0)));
		Assert.Equal(AlsaDrainStep.Fail, tracker.Next(-19, 0, -1, Rate, At(0)));
	}

	[Fact]
	public void 短音频停在准备状态时显式启动()
	{
		AlsaDrainTracker tracker = new();

		Assert.Equal(AlsaDrainStep.Start, tracker.Next(0, 480, AlsaPcm.StatePrepared, Rate, At(0)));
		Assert.Equal(AlsaDrainStep.Pending, tracker.Next(0, 480, Running, Rate, At(0.001)));
	}

	[Fact]
	public void 欠载后按首次报告的帧数等完尾音()
	{
		AlsaDrainTracker tracker = new();

		Assert.Equal(AlsaDrainStep.Pending, tracker.Next(0, 4_800, AlsaPcm.StateUnderrun, Rate, At(0)));
		// 后续读数变大也不能延长尾音期限。
		Assert.Equal(AlsaDrainStep.Pending, tracker.Next(0, 9_600, AlsaPcm.StateUnderrun, Rate, At(0.099)));
		Assert.Equal(AlsaDrainStep.Done, tracker.Next(0, 9_600, AlsaPcm.StateUnderrun, Rate, At(0.1 + Epsilon)));
	}

	[Fact]
	public void 剩余帧停滞时到达总等待上限后完成()
	{
		AlsaDrainTracker tracker = new();
		double limit = 0.1 + AlsaDrainTracker.Slack.TotalSeconds;

		Assert.Equal(AlsaDrainStep.Pending, tracker.Next(0, 4_800, Running, Rate, At(0)));
		Assert.Equal(AlsaDrainStep.Pending, tracker.Next(0, 4_800, Running, Rate, At(limit - 0.01)));
		Assert.Equal(AlsaDrainStep.Done, tracker.Next(0, 4_800, Running, Rate, At(limit + Epsilon)));
	}

	[Fact]
	public void 异常巨大的剩余读数也受上限约束()
	{
		AlsaDrainTracker tracker = new();
		double limit = AlsaDrainTracker.MaxReportedTail.TotalSeconds + AlsaDrainTracker.Slack.TotalSeconds;

		Assert.Equal(AlsaDrainStep.Pending, tracker.Next(0, long.MaxValue, Running, Rate, At(0)));
		Assert.Equal(AlsaDrainStep.Done, tracker.Next(0, long.MaxValue, Running, Rate, At(limit + Epsilon)));
	}

	[Fact]
	public void 挂起时请求恢复且同样受总等待上限约束()
	{
		AlsaDrainTracker tracker = new();

		Assert.Equal(AlsaDrainStep.Recover, tracker.Next(AlsaPcm.Suspended, 0, -1, Rate, At(0)));
		Assert.Equal(AlsaDrainStep.Recover, tracker.Next(AlsaPcm.Suspended, 0, -1, Rate, At(0.5)));
		Assert.Equal(AlsaDrainStep.Done, tracker.Next(AlsaPcm.Suspended, 0, -1, Rate, At(AlsaDrainTracker.Slack.TotalSeconds + Epsilon)));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void 信号中断可重试但不能延长总等待期限(bool alreadyPlaying)
	{
		AlsaDrainTracker tracker = new();
		double limit = AlsaDrainTracker.Slack.TotalSeconds;
		if (alreadyPlaying)
		{
			Assert.Equal(AlsaDrainStep.Pending, tracker.Next(0, 4_800, Running, Rate, At(0)));
			limit += 0.1;
		}

		// 失败的查询没有有效剩余帧，不能据此判断播完或延长等待。
		Assert.Equal(AlsaDrainStep.Pending, tracker.Next(-4, long.MaxValue, -1, Rate, At(0)));
		Assert.Equal(AlsaDrainStep.Pending, tracker.Next(-4, 0, -1, Rate, At(limit - 0.01)));
		Assert.Equal(AlsaDrainStep.Done, tracker.Next(-4, 0, -1, Rate, At(limit + Epsilon)));
	}

	[Fact]
	public void 信号中断后继续查询并启动短音频()
	{
		AlsaDrainTracker tracker = new();

		Assert.Equal(AlsaDrainStep.Pending, tracker.Next(-4, 0, -1, Rate, At(0)));
		Assert.Equal(AlsaDrainStep.Start, tracker.Next(0, 480, AlsaPcm.StatePrepared, Rate, At(0.01)));
		Assert.Equal(AlsaDrainStep.Pending, tracker.Next(0, 480, Running, Rate, At(0.02)));
		Assert.Equal(AlsaDrainStep.Done, tracker.Next(0, 0, Running, Rate, At(0.03)));
	}

	[Fact]
	public void 写入新样本后重新计算期限()
	{
		AlsaDrainTracker tracker = new();
		Assert.Equal(AlsaDrainStep.Pending, tracker.Next(0, 4_800, AlsaPcm.StateUnderrun, Rate, At(0)));
		Assert.Equal(AlsaDrainStep.Done, tracker.Next(0, 4_800, AlsaPcm.StateUnderrun, Rate, At(5)));

		tracker.Reset();

		Assert.Equal(AlsaDrainStep.Pending, tracker.Next(0, 4_800, AlsaPcm.StateUnderrun, Rate, At(5)));
		Assert.Equal(AlsaDrainStep.Pending, tracker.Next(0, 4_800, Running, Rate, At(5.5)));
	}
}
