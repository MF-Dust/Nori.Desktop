using Nori.Desktop.Live2D;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

public class AutonomousMotionPlannerTests
{
	private static readonly AutonomousMotionSlot[] IdleMotions =
	[
		new("Idle", 0, false),
		new("Idle", 1, false),
		new("Idle", 2, true),
	];

	private static readonly AutonomousMotionSlot[] VoiceMotions =
	[
		new("Talk", 0, false),
		new("Talk", 1, false),
	];

	[Theory]
	[InlineData("Idle", "idle")]
	[InlineData("IDLE_02", "idle")]
	[InlineData("Talk", "voice")]
	[InlineData("Speaking", "voice")]
	[InlineData("VoiceA", "voice")]
	public void 待机和语音组按模型组名归类(string group, string expected)
	{
		Assert.True(AutonomousMotionPlanner.TryClassify(group, out AutonomousMotionRole role));
		Assert.Equal(expected, role == AutonomousMotionRole.Voice ? "voice" : "idle");
	}

	[Theory]
	[InlineData("TapBody")]
	[InlineData("Touch")]
	[InlineData("Click")]
	[InlineData("Dance")]
	[InlineData("")]
	[InlineData(null)]
	public void 点击和无关组不进入自主动作(string? group)
	{
		Assert.False(AutonomousMotionPlanner.TryClassify(group, out _));
	}

	[Fact]
	public void 显式高优先级动作不会被待机或语音打断()
	{
		AutonomousMotionPlanner planner = Ready();
		AutonomousDecision interacting = planner.Tick(Frame(now: 10, priority: MotionPriority.Force, finished: false, speaking: true));
		Assert.Equal(AutonomousActivity.Interacting, interacting.Activity);
		Assert.False(interacting.HasMotion);

		AutonomousDecision busy = planner.Tick(Frame(now: 10, priority: MotionPriority.Normal, finished: false, group: "Wave"));
		Assert.Equal(AutonomousActivity.Busy, busy.Activity);
		Assert.False(busy.HasMotion);
	}

	[Fact]
	public void 说话时优先语音动作并可以替换正在播放的待机()
	{
		AutonomousMotionPlanner planner = Ready(next: _ => 0);
		AutonomousDecision decision = planner.Tick(Frame(now: 10, priority: MotionPriority.Idle, finished: false, speaking: true, group: "Idle"));
		Assert.Equal(AutonomousActivity.Speaking, decision.Activity);
		Assert.Equal(MotionPriority.Normal, decision.Priority);
		Assert.Equal("Talk", decision.Motion?.Group);
		Assert.Equal(0, decision.Motion?.Index);
	}

	[Fact]
	public void 语音动作有最短间隔且不打断正在播放的语音()
	{
		AutonomousMotionPlanner planner = Ready(next: _ => 0);
		AutonomousDecision first = planner.Tick(Frame(now: 10, speaking: true));
		Assert.Equal(0, first.Motion?.Index);
		planner.Commit(first.Motion!.Value, first.Priority, 10, AutonomousPacing.Normal);
		Assert.False(planner.Tick(Frame(now: 10.2, speaking: true, priority: MotionPriority.Normal, finished: false, group: "Talk")).HasMotion);
		Assert.False(planner.Tick(Frame(now: 10.2, speaking: true)).HasMotion);
		Assert.True(planner.Tick(Frame(now: 10.5, speaking: true)).HasMotion);
	}

	[Fact]
	public void 没有语音组时说话不开始待机()
	{
		AutonomousMotionPlanner planner = Ready(includeVoice: false);
		AutonomousDecision decision = planner.Tick(Frame(now: 10, speaking: true));
		Assert.Equal(AutonomousActivity.Speaking, decision.Activity);
		Assert.False(decision.HasMotion);
	}

	[Fact]
	public void 冷却未结束时不开始下一条待机()
	{
		AutonomousMotionPlanner planner = Ready(next: _ => 0);
		AutonomousDecision first = planner.Tick(Frame(now: 10));
		Assert.True(first.HasMotion);
		planner.Commit(first.Motion!.Value, first.Priority, 10, AutonomousPacing.Normal);

		AutonomousDecision cooled = planner.Tick(Frame(now: 17.9));
		Assert.Equal(AutonomousActivity.Idle, cooled.Activity);
		Assert.False(cooled.HasMotion);

		AutonomousDecision ready = planner.Tick(Frame(now: 18));
		Assert.True(ready.HasMotion);
	}

	[Fact]
	public void 频率在下一次成功播放时生效()
	{
		AutonomousMotionPlanner planner = Ready(next: _ => 0);
		AutonomousDecision first = planner.Tick(Frame(now: 10, frequency: AutonomousPacing.Lively));
		planner.Commit(first.Motion!.Value, first.Priority, 10, AutonomousPacing.Lively);
		Assert.False(planner.Tick(Frame(now: 13.9, frequency: AutonomousPacing.Lively)).HasMotion);
		Assert.True(planner.Tick(Frame(now: 14, frequency: AutonomousPacing.Lively)).HasMotion);

		planner.Commit(first.Motion!.Value, first.Priority, 14, AutonomousPacing.Sparse);
		Assert.False(planner.Tick(Frame(now: 29.9, frequency: AutonomousPacing.Sparse)).HasMotion);
		Assert.True(planner.Tick(Frame(now: 30, frequency: AutonomousPacing.Sparse)).HasMotion);
	}

	[Fact]
	public void 随机播放避开刚播过的动作并在没有别的选择时放行()
	{
		int[] draws = [0, 0, 0];
		int cursor = 0;
		AutonomousMotionPlanner planner = Ready(next: _ => draws[cursor++]);
		AutonomousDecision first = planner.Tick(Frame(now: 10));
		Assert.Equal(0, first.Motion?.Index);
		planner.Commit(first.Motion!.Value, first.Priority, 10, AutonomousPacing.Normal);

		AutonomousDecision second = planner.Tick(Frame(now: 20));
		Assert.Equal(1, second.Motion?.Index);
		planner.Commit(second.Motion!.Value, second.Priority, 20, AutonomousPacing.Normal);

		AutonomousDecision third = planner.Tick(Frame(now: 30));
		Assert.Equal(0, third.Motion?.Index);
	}

	[Fact]
	public void 关闭随机时固定播放第一个睁眼待机()
	{
		AutonomousMotionPlanner planner = Ready();
		AutonomousDecision decision = planner.Tick(Frame(now: 10, random: false));
		Assert.Equal(0, decision.Motion?.Index);

		planner = new AutonomousMotionPlanner();
		planner.Bind(1, [new AutonomousMotionSlot("Idle", 0, true), new AutonomousMotionSlot("Idle", 1, false)], []);
		Assert.Equal(1, planner.Tick(Frame(now: 10, random: false)).Motion?.Index);
	}

	[Fact]
	public void 整组都闭眼时仍然可以播放()
	{
		AutonomousMotionPlanner planner = new();
		planner.Bind(1, [new AutonomousMotionSlot("Idle", 0, true)], []);
		Assert.Equal(0, planner.Tick(Frame(now: 10, random: false)).Motion?.Index);
	}

	[Fact]
	public void 没有待机动作时保持空闲且不抛出()
	{
		AutonomousMotionPlanner planner = new();
		planner.Bind(1, [], []);
		AutonomousDecision decision = planner.Tick(Frame(now: 10));
		Assert.Equal(AutonomousActivity.Idle, decision.Activity);
		Assert.False(decision.HasMotion);
	}

	[Fact]
	public void 待机开关关闭或当前动作未结束时不开始新待机()
	{
		AutonomousMotionPlanner planner = Ready();
		Assert.False(planner.Tick(Frame(now: 10, idleEnabled: false)).HasMotion);
		Assert.False(planner.Tick(Frame(now: 10, priority: MotionPriority.Idle, finished: false, group: "Idle")).HasMotion);
	}

	[Fact]
	public void 关闭或暂停时不开始动作也不消耗恢复延迟()
	{
		AutonomousMotionPlanner planner = Ready();
		planner.Hold();
		Assert.False(planner.Tick(Frame(now: 10, enabled: false)).HasMotion);
		Assert.True(planner.ResumeHeld);
		Assert.Equal(AutonomousActivity.Suspended, planner.Tick(Frame(now: 10, suspended: true)).Activity);
		Assert.True(planner.ResumeHeld);
	}

	[Fact]
	public void 隐藏恢复后的第一段间隔内不立刻开始待机()
	{
		AutonomousMotionPlanner planner = Ready();
		planner.Hold();
		Assert.False(planner.Tick(Frame(now: 10)).HasMotion);
		Assert.False(planner.ResumeHeld);
		Assert.False(planner.Tick(Frame(now: 17)).HasMotion);
		Assert.True(planner.Tick(Frame(now: 18)).HasMotion);
	}

	[Fact]
	public void 换模型后不会再选中旧动作()
	{
		AutonomousMotionPlanner planner = Ready(next: _ => 1);
		AutonomousDecision oldMotion = planner.Tick(Frame(now: 10));
		Assert.Equal("Idle", oldMotion.Motion?.Group);
		planner.Commit(oldMotion.Motion!.Value, oldMotion.Priority, 10, AutonomousPacing.Normal);

		planner.Bind(2, [new AutonomousMotionSlot("IdleLoop", 0, false)], [new AutonomousMotionSlot("Speak", 0, false)]);
		AutonomousDecision fresh = planner.Tick(Frame(now: 10));
		Assert.Equal("IdleLoop", fresh.Motion?.Group);
		Assert.Equal(0, fresh.Motion?.Index);
		Assert.NotEqual(oldMotion.Motion?.Group, fresh.Motion?.Group);
	}

	[Fact]
	public void 释放后没有可播放的旧候选()
	{
		AutonomousMotionPlanner planner = Ready();
		planner.Reset();
		Assert.Equal(-1, planner.Generation);
		Assert.False(planner.Tick(Frame(now: 100)).HasMotion);
	}

	[Theory]
	[InlineData("", AutonomousPacing.Normal)]
	[InlineData("sparse", AutonomousPacing.Sparse)]
	[InlineData("HIGH", AutonomousPacing.Lively)]
	[InlineData("weekly", AutonomousPacing.Normal)]
	public void 未识别的频率回退到自然(string raw, string expected)
	{
		Assert.Equal(expected, AutonomousPacing.Normalize(raw));
	}

	[Fact]
	public void 写入频率只接受三个词()
	{
		Assert.True(AutonomousPacing.TryNormalize(" lively ", true, out string lively));
		Assert.Equal(AutonomousPacing.Lively, lively);
		Assert.False(AutonomousPacing.TryNormalize("high", true, out _));
	}

	private static AutonomousMotionPlanner Ready(bool includeVoice = true, Func<int, int>? next = null)
	{
		AutonomousMotionPlanner planner = new();
		if (next is not null) planner.UseRandom(next);
		planner.Bind(1, IdleMotions, includeVoice ? VoiceMotions : []);
		return planner;
	}

	private static AutonomousFrame Frame(
		double now,
		bool enabled = true,
		bool random = true,
		string? frequency = null,
		bool suspended = false,
		bool speaking = false,
		bool finished = true,
		MotionPriority priority = MotionPriority.None,
		string? group = null,
		bool idleEnabled = true) => new(
		now,
		enabled,
		random,
		frequency ?? AutonomousPacing.Normal,
		suspended,
		speaking,
		finished,
		priority,
		group,
		idleEnabled);
}
