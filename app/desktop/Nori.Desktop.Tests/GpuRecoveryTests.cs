using Nori.Desktop.Live2D;

namespace Nori.Desktop.Tests;

/// <summary>NORI-31 / NORI-32：GPU 故障后的退避。</summary>
public sealed class GpuRecoveryBackoffTests
{
	[Fact]
	public void 连续故障逐级拉长等待并封顶()
	{
		GpuRecoveryBackoff backoff = new();
		TimeSpan now = TimeSpan.FromMinutes(10);
		List<TimeSpan> delays = [];
		for (int attempt = 0; attempt < GpuRecoveryBackoff.Delays.Length + 2; attempt++)
		{
			delays.Add(backoff.NextDelay(now));
			now += delays[^1];
		}

		Assert.Equal(GpuRecoveryBackoff.Delays, delays.Take(GpuRecoveryBackoff.Delays.Length));
		Assert.All(delays.Skip(GpuRecoveryBackoff.Delays.Length), delay => Assert.Equal(GpuRecoveryBackoff.Delays[^1], delay));
	}

	[Fact]
	public void 稳定超过窗口后重新从最短等待开始()
	{
		GpuRecoveryBackoff backoff = new();
		TimeSpan now = TimeSpan.FromMinutes(10);
		Assert.False(backoff.IsRecovering(now));
		backoff.NextDelay(now);
		backoff.NextDelay(now + TimeSpan.FromSeconds(1));
		Assert.True(backoff.IsRecovering(now + TimeSpan.FromSeconds(30)));

		TimeSpan stable = now + TimeSpan.FromSeconds(1) + GpuRecoveryBackoff.StableWindow;

		Assert.False(backoff.IsRecovering(stable));
		Assert.Equal(GpuRecoveryBackoff.Delays[0], backoff.NextDelay(stable));
	}
}
