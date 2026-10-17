using Nori.Core.Memory;

namespace Nori.Core.Tests;

public sealed class MemoryRetrievalTests
{
	[Fact]
	public void Rrf融合不直接比较异构分数()
	{
		IReadOnlyList<RetrievalHit> result = RrfFusion.Fuse(
		[
			[ new RetrievalHit(1, 100, 1), new RetrievalHit(2, 1, 2) ],
			[ new RetrievalHit(2, 0.99, 1), new RetrievalHit(1, 0.2, 2) ],
		]);

		Assert.Equal(1, result[0].MemoryId);
		Assert.Equal(2, result[1].MemoryId);
		Assert.Equal(1, result[0].Rank);
	}

	[Fact]
	public void 重要度置信度强化和衰减参与最终评分()
	{
		DateTimeOffset now = new(2025, 1, 20, 0, 0, 0, TimeSpan.Zero);
		MemoryItem item = new()
		{
			Id = 1,
			Type = "fact",
			Content = "测试",
			Importance = 0.9,
			Source = "chat",
			CreatedAt = now.AddDays(-10).ToString("o"),
			UpdatedAt = now.AddDays(-10).ToString("o"),
			Kind = "episodic",
			Confidence = 0.9,
			TtlDays = 30,
			ReinforcementCount = 2,
		};

		double decay = Math.Exp(-Math.Log(2) * 10 / 30);
		double expected = 1.2 * 0.975 * 1.1 * decay;

		Assert.Equal(expected, DecayCalculator.FinalScore(1, item, now), 12);
		Assert.Equal(1.2 * 0.975 * 1.1, DecayCalculator.FinalScore(1, item, now, applyTemporalDecay: false), 12);
	}

	[Fact]
	public void QueryBuilder保留当前问题并带入最近上下文()
	{
		string query = MemoryQueryBuilder.Build("那她后来怎么样了？", [("user", "我们正在讨论研究员和八月十五日事故")]);
		Assert.Contains("那她后来怎么样了", query, StringComparison.Ordinal);
		Assert.Contains("研究员", query, StringComparison.Ordinal);
	}
}
