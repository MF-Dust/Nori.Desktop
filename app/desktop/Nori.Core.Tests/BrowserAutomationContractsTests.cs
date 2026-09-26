using System.Text;
using System.Text.Json;
using Nori.Core.Automation;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

/// <summary>浏览器结构化动作契约与短期结果边界测试。</summary>
public sealed class BrowserAutomationContractsTests
{
	[Fact]
	public void 只解析六种白名单动作且计划有上限()
	{
		JsonElement actions = JsonSerializer.SerializeToElement(new object[]
		{
			new {type = "navigate", url = "https://example.test"},
			new {type = "click", selector = "#open"},
			new {type = "fill", selector = "#query", text = "只留在内存"},
			new {type = "scroll", pixels = 120},
			new {type = "wait", milliseconds = 50},
			new {type = "read_visible_text"},
		});

		BrowserAutomationTaskPlan plan = BrowserAutomationTaskPlan.Parse(actions);

		Assert.Equal(6, plan.Actions.Count);
		Assert.IsType<BrowserNavigateAction>(plan.Actions[0]);
		Assert.IsType<BrowserClickAction>(plan.Actions[1]);
		Assert.IsType<BrowserFillAction>(plan.Actions[2]);
		Assert.IsType<BrowserScrollAction>(plan.Actions[3]);
		Assert.IsType<BrowserWaitAction>(plan.Actions[4]);
		Assert.IsType<BrowserReadVisibleTextAction>(plan.Actions[5]);
		Assert.Equal(TimeSpan.FromSeconds(120), BrowserAutomationTaskLimits.MaximumDuration);
	}

	[Theory]
	[InlineData("eval")]
	[InlineData("download")]
	[InlineData("upload")]
	[InlineData("password")]
	[InlineData("payment")]
	[InlineData("captcha")]
	[InlineData("permission_bypass")]
	public void 拒绝非白名单或高风险动作(string type)
	{
		JsonElement actions = JsonSerializer.SerializeToElement(new[] {new {type}});
		BrowserAutomationActionValidationException exception = Assert.Throws<BrowserAutomationActionValidationException>(
			() => BrowserAutomationTaskPlan.Parse(actions));
		Assert.DoesNotContain(type, exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void 拒绝超过二十个动作()
	{
		object[] actions = Enumerable.Range(0, BrowserAutomationTaskLimits.MaxActions + 1)
			.Select(_ => (object)new {type = "read_visible_text"})
			.ToArray();

		Assert.Throws<BrowserAutomationActionValidationException>(() =>
			BrowserAutomationTaskPlan.Parse(JsonSerializer.SerializeToElement(actions)));
	}

	[Fact]
	public void 可见文本按三十二KiB字节边界截断()
	{
		string source = string.Concat(Enumerable.Repeat("海", BrowserAutomationTaskLimits.MaxVisibleTextBytes));
		string bounded = BrowserAutomationTaskLimits.TruncateVisibleText(source);

		Assert.True(Encoding.UTF8.GetByteCount(bounded) <= BrowserAutomationTaskLimits.MaxVisibleTextBytes);
		Assert.True(bounded.Length < source.Length);
		Assert.False(bounded.Length > 0 && char.IsHighSurrogate(bounded[^1]));
	}

	[Fact]
	public void 短期结果过期后不再可读()
	{
		MutableTimeProvider clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
		BrowserAutomationResultStore store = new(clock);
		Guid taskId = Guid.NewGuid();
		store.Set(new BrowserAutomationTaskResult(taskId, true, "页面可见文本", null, clock.GetUtcNow()));

		Assert.NotNull(store.Get(taskId));
		clock.Advance(BrowserAutomationResultStore.ResultTtl + TimeSpan.FromSeconds(1));
		Assert.Null(store.Get(taskId));
	}
}
