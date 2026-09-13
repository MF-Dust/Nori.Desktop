using Nori.Core.Agent;
using Nori.Core.Observation;

namespace Nori.Core.Tests;

/// <summary>
/// 机器状态的分档与注入。
///
/// 重点是**只注入分档不注入读数**：系统提示词在整个请求的最前面，其中任何一个字节变化都会
/// 使整段前缀的缓存失效。精确读数每一轮都不一样，直接写进去等于每轮全量重算。
/// </summary>
public sealed class MachineStateTests
{
	// ---- 分档 ----

	[Theory]
	[InlineData(1000, 4000, UsageLevel.Ample)]
	[InlineData(19480, 32129, UsageLevel.Ample)]   // 实测值：32 GB 用掉 19.5 GB 仍算充足
	[InlineData(3000, 4000, UsageLevel.Tight)]
	[InlineData(3800, 4000, UsageLevel.Critical)]
	public void 占用按比例分档(int used, int total, UsageLevel expected)
	{
		Assert.Equal(expected, MachineStateText.Usage(used, total));
	}

	/// <summary>取不到与「读数为零」必须分得开：前者不该注入，后者是真的空闲。</summary>
	[Theory]
	[InlineData(null, 4000)]
	[InlineData(1000, null)]
	[InlineData(1000, 0)]
	public void 读不到时分档为未知(int? used, int? total)
	{
		Assert.Equal(UsageLevel.Unknown, MachineStateText.Usage(used, total));
	}

	[Theory]
	[InlineData(0, LoadLevel.Idle)]
	[InlineData(19, LoadLevel.Idle)]
	[InlineData(20, LoadLevel.Moderate)]
	[InlineData(60, LoadLevel.Moderate)]
	[InlineData(61, LoadLevel.Busy)]
	[InlineData(null, LoadLevel.Unknown)]
	public void 负载按利用率分档(int? percent, LoadLevel expected)
	{
		Assert.Equal(expected, MachineStateText.Load(percent));
	}

	[Fact]
	public void 在场按空闲时长分档()
	{
		Assert.Equal(PresenceLevel.Active, MachineStateText.Presence(TimeSpan.FromSeconds(5)));
		Assert.Equal(PresenceLevel.Away, MachineStateText.Presence(TimeSpan.FromMinutes(3)));
		Assert.Equal(PresenceLevel.Absent, MachineStateText.Presence(TimeSpan.FromHours(2)));
		Assert.Equal(PresenceLevel.Unknown, MachineStateText.Presence(null));
	}

	// ---- 渲染 ----

	private static MachineState Sample(int usedMb = 8000, int? cpu = 5) => new()
	{
		MemoryUsedMb = usedMb,
		MemoryTotalMb = 32000,
		CpuPercent = cpu,
		Uptime = TimeSpan.FromHours(110.9),
		Idle = TimeSpan.FromSeconds(30),
	};

	/// <summary>
	/// 渲染结果里不能出现任何精确读数。
	///
	/// 这是这一族最重要的一条：数字一旦进了系统提示词，它每轮都变，整段前缀的缓存每轮失效。
	/// </summary>
	[Fact]
	public void 渲染里不含精确读数()
	{
		string text = MachineStateText.Render(Sample());

		foreach (string number in new[] {"8000", "32000", "5%", "19480", "32129"})
		{
			Assert.DoesNotContain(number, text, StringComparison.Ordinal);
		}
	}

	/// <summary>读数在分档内波动时渲染结果必须逐字节相同，否则缓存照样每轮失效。</summary>
	[Fact]
	public void 同一分档内的波动不改变渲染结果()
	{
		Assert.Equal(MachineStateText.Render(Sample(8000, 5)), MachineStateText.Render(Sample(9000, 12)));
	}

	[Fact]
	public void 跨分档时渲染结果改变()
	{
		Assert.NotEqual(MachineStateText.Render(Sample(8000)), MachineStateText.Render(Sample(30000)));
	}

	[Fact]
	public void 什么都取不到时不渲染()
	{
		Assert.Equal("", MachineStateText.Render(MachineState.Empty));
		Assert.False(MachineState.Empty.HasAnything);
	}

	/// <summary>只有部分读数时也要能渲染：平台能力不同，缺项是常态而不是异常。</summary>
	[Fact]
	public void 只有部分读数时照常渲染()
	{
		string text = MachineStateText.Render(new MachineState {Uptime = TimeSpan.FromHours(3)});

		Assert.Contains("已开机 3 小时", text, StringComparison.Ordinal);
		Assert.DoesNotContain("内存", text, StringComparison.Ordinal);
	}

	/// <summary>这一段是环境事实，要明说不是让她复述指标，否则她会把它当成话题念出来。</summary>
	[Fact]
	public void 渲染里声明这是环境信息()
	{
		Assert.Contains("不是让你复述", MachineStateText.Render(Sample()), StringComparison.Ordinal);
	}

	[Fact]
	public void 显卡热的时候才提温度()
	{
		GpuState hot = new() {Name = "x", TemperatureCelsius = 85, UtilizationPercent = 90, MemoryUsedMb = 1, MemoryTotalMb = 100};
		GpuState cool = hot with {TemperatureCelsius = 50};

		Assert.Contains("温度偏高", MachineStateText.Render(new MachineState {Gpu = hot}), StringComparison.Ordinal);
		Assert.DoesNotContain("温度偏高", MachineStateText.Render(new MachineState {Gpu = cool}), StringComparison.Ordinal);
	}

	// ---- 注入 ----

	[Fact]
	public void 提示词里带上机器状态()
	{
		string prompt = PromptBuilder.Build(new PromptBuildOptions {MachineState = Sample(), ToolsJson = "[]"});

		Assert.Contains("这台机器现在的状态", prompt, StringComparison.Ordinal);
		Assert.Contains("内存充足", prompt, StringComparison.Ordinal);
	}

	[Fact]
	public void 没有机器状态时整段不注入()
	{
		Assert.DoesNotContain(
			"这台机器现在的状态",
			PromptBuilder.Build(new PromptBuildOptions {ToolsJson = "[]"}),
			StringComparison.Ordinal);

		Assert.DoesNotContain(
			"这台机器现在的状态",
			PromptBuilder.Build(new PromptBuildOptions {MachineState = MachineState.Empty, ToolsJson = "[]"}),
			StringComparison.Ordinal);
	}
}
