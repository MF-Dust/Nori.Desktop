using System.Text.Json;
using Nori.Core.Agent;

namespace Nori.Core.Tests;

/// <summary>Agent Trace 的边界、并发与正文隔离测试。</summary>
public sealed class AgentTraceTests
{
	[Fact]
	public void 会话和工具标识限制长度()
	{
		AgentTraceRecord record = new(
			new string('s', AgentTraceRecord.MaxSessionIdLength + 50),
			"llm",
			0,
			null,
			new string('t', AgentTraceRecord.MaxToolNameLength + 50),
			"completed");

		Assert.Equal(AgentTraceRecord.MaxSessionIdLength, record.SessionId.Length);
		Assert.Equal(AgentTraceRecord.MaxToolNameLength, record.ToolName!.Length);
	}

	[Fact]
	public void 收集器并发写入仍保持有界()
	{
		const int capacity = 64;
		AgentTraceCollector collector = new(capacity);

		Parallel.For(0, 2_000, index => collector.Record(new AgentTraceRecord(
			$"session-{index}", "llm", index, index, null, "completed")));

		Assert.Equal(capacity, collector.Count);
		Assert.Equal(capacity, collector.Snapshot().Count);
	}

	[Fact]
	public void 记录不包含敏感正文或工具载荷()
	{
		AgentTraceRecord record = new("session", "tool", 12, 0, "weather", "completed");
		string serialized = JsonSerializer.Serialize(record);

		Assert.DoesNotContain("arguments", serialized, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("result", serialized, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("reply", serialized, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void 结构化字段也经过统一脱敏()
	{
		AgentTraceRecord record = new(
			"session api_key=secret-value C:\\Users\\Nori\\chat.log",
			"tool",
			12,
			0,
			"tool token=another-secret",
			"completed");
		string serialized = JsonSerializer.Serialize(record);

		Assert.DoesNotContain("secret-value", serialized, StringComparison.Ordinal);
		Assert.DoesNotContain("another-secret", serialized, StringComparison.Ordinal);
		Assert.DoesNotContain("C:\\Users\\Nori", serialized, StringComparison.Ordinal);
	}
}
