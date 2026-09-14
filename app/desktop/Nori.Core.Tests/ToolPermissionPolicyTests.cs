using Nori.Core.Tools;

namespace Nori.Core.Tests;

/// <summary>
/// 授权档位的判据。
///
/// 这一族守的是**放宽授权的每一条路都得是用户明确选的**。最容易写错的三处：
///   ① 认不出的配置值要退到最严的一档，不是最松的；
///   ② 只有真的按了「允许」才算同意，超时和取消不算；
///   ③ 完全放行要会到期，而且到期只在读取时判定，不偷偷改掉用户存的值。
/// </summary>
public sealed class ToolPermissionPolicyTests
{
	private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

	[Theory]
	[InlineData("ask", PermissionGear.Ask)]
	[InlineData("session", PermissionGear.Session)]
	[InlineData("trusted", PermissionGear.Trusted)]
	[InlineData("bypass", PermissionGear.Bypass)]
	[InlineData("BYPASS", PermissionGear.Bypass)]
	[InlineData("  trusted  ", PermissionGear.Trusted)]
	public void 认得的档位原样解析(string raw, PermissionGear expected) =>
		Assert.Equal(expected, ToolPermissionPolicy.Parse(raw));

	/// <summary>
	/// 认不出来时退到逐次确认。
	///
	/// 反过来写（退到最松）的代价是：配置损坏、键名改过、甚至一个拼错的值，
	/// 都会静默地把她升到无需确认 —— 而这类故障恰恰是没人会去看的。
	/// </summary>
	[Theory]
	[InlineData("")]
	[InlineData(null)]
	[InlineData("full")]
	[InlineData("bypasss")]
	[InlineData("完全放行")]
	public void 认不出的值退到最严的一档(string? raw) =>
		Assert.Equal(PermissionGear.Ask, ToolPermissionPolicy.Parse(raw));

	[Fact]
	public void 档位与配置字符串来回转换一致()
	{
		foreach (PermissionGear gear in Enum.GetValues<PermissionGear>())
		{
			Assert.Equal(gear, ToolPermissionPolicy.Parse(ToolPermissionPolicy.Format(gear)));
		}
	}

	// ---- 判定 ----

	[Fact]
	public void 逐次确认每次都问()
	{
		ToolPermissionPolicy policy = new();
		Assert.Equal(PermissionDecision.Ask, policy.Decide(PermissionGear.Ask, "s1", "writeFile", "confirm"));
		policy.Remember("s1", "writeFile");
		Assert.Equal(PermissionDecision.Ask, policy.Decide(PermissionGear.Ask, "s1", "writeFile", "confirm"));
	}

	[Fact]
	public void 本轮记住只在批准过之后放行()
	{
		ToolPermissionPolicy policy = new();
		Assert.Equal(PermissionDecision.Ask, policy.Decide(PermissionGear.Session, "s1", "writeFile", "confirm"));

		policy.Remember("s1", "writeFile");
		Assert.Equal(PermissionDecision.Allow, policy.Decide(PermissionGear.Session, "s1", "writeFile", "confirm"));

		// 换一个工具要重新问：批准的是「写文件」，不是「随便做什么」。
		Assert.Equal(PermissionDecision.Ask, policy.Decide(PermissionGear.Session, "s1", "runTask", "confirm"));
		// 换一轮也要重新问。
		Assert.Equal(PermissionDecision.Ask, policy.Decide(PermissionGear.Session, "s2", "writeFile", "confirm"));
	}

	[Fact]
	public void 本轮记住不覆盖高风险那一档()
	{
		ToolPermissionPolicy policy = new();
		policy.Remember("s1", "wipeDisk");
		Assert.Equal(PermissionDecision.Ask, policy.Decide(PermissionGear.Session, "s1", "wipeDisk", "dangerous"));
	}

	[Fact]
	public void 完全授权放行日常但仍拦高风险()
	{
		ToolPermissionPolicy policy = new();
		Assert.Equal(PermissionDecision.Allow, policy.Decide(PermissionGear.Trusted, "s1", "writeFile", "confirm"));
		Assert.Equal(PermissionDecision.Ask, policy.Decide(PermissionGear.Trusted, "s1", "wipeDisk", "dangerous"));
	}

	[Fact]
	public void 完全放行连高风险也不问()
	{
		ToolPermissionPolicy policy = new();
		Assert.Equal(PermissionDecision.Allow, policy.Decide(PermissionGear.Bypass, "s1", "writeFile", "confirm"));
		Assert.Equal(PermissionDecision.Allow, policy.Decide(PermissionGear.Bypass, "s1", "wipeDisk", "dangerous"));
	}

	/// <summary>safe 档根本不会走到这里（注册表自己就放行了），但这个函数单独看也要说得通。</summary>
	[Fact]
	public void safe档在任何一档下都不问()
	{
		ToolPermissionPolicy policy = new();
		foreach (PermissionGear gear in Enum.GetValues<PermissionGear>())
		{
			Assert.Equal(PermissionDecision.Allow, policy.Decide(gear, "s1", "readFile", "safe"));
		}
	}

	// ---- 记忆的边界 ----

	/// <summary>
	/// 只有真的批准了才能记。
	///
	/// 宿主那边只在 approved 为真时才调 Remember；这里钉的是「记了就等于放行」这层
	/// 因果 —— 哪天有人把超时也当成一种结果顺手记进来，这条会红。
	/// </summary>
	[Fact]
	public void 没记过就不放行()
	{
		ToolPermissionPolicy policy = new();
		Assert.False(policy.Remembered("s1", "writeFile"));
		Assert.Equal(PermissionDecision.Ask, policy.Decide(PermissionGear.Session, "s1", "writeFile", "confirm"));
	}

	[Fact]
	public void 一轮结束就忘掉()
	{
		ToolPermissionPolicy policy = new();
		policy.Remember("s1", "writeFile");
		Assert.Equal(1, policy.RememberedTurnCount);

		policy.ForgetTurn("s1");

		Assert.Equal(0, policy.RememberedTurnCount);
		Assert.Equal(PermissionDecision.Ask, policy.Decide(PermissionGear.Session, "s1", "writeFile", "confirm"));
	}

	/// <summary>宿主漏调一次清理，这张表也不能一直长。</summary>
	[Fact]
	public void 记忆有上限()
	{
		ToolPermissionPolicy policy = new();
		for (int index = 0; index < ToolPermissionPolicy.MaxRememberedTurns * 3; index++)
		{
			policy.Remember($"s{index}", "writeFile");
		}

		Assert.Equal(ToolPermissionPolicy.MaxRememberedTurns, policy.RememberedTurnCount);
		// 淘汰的是最早的那些。
		Assert.False(policy.Remembered("s0", "writeFile"));
		Assert.True(policy.Remembered($"s{ToolPermissionPolicy.MaxRememberedTurns * 3 - 1}", "writeFile"));
	}

	// ---- 完全放行的到期 ----

	[Fact]
	public void 完全放行在有效期内照常生效()
	{
		DateTimeOffset until = ToolPermissionPolicy.BypassDeadline(Now);
		Assert.Equal(PermissionGear.Bypass, ToolPermissionPolicy.Effective(PermissionGear.Bypass, until, Now));
		Assert.Equal(
			PermissionGear.Bypass,
			ToolPermissionPolicy.Effective(PermissionGear.Bypass, until, until.AddSeconds(-1)));
	}

	/// <summary>到期之后降到完全授权，不是降到逐次确认 —— 理由写在 BypassWindow 的注释里。</summary>
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(86400)]
	public void 完全放行到期后降到完全授权(int secondsPastDeadline)
	{
		DateTimeOffset until = ToolPermissionPolicy.BypassDeadline(Now);
		DateTimeOffset later = until.AddSeconds(secondsPastDeadline);
		Assert.Equal(PermissionGear.Trusted, ToolPermissionPolicy.Effective(PermissionGear.Bypass, until, later));
	}

	/// <summary>存了 bypass 却没有到期时刻，只可能是被外部改过库 —— 当成已过期。</summary>
	[Fact]
	public void 没有到期时刻的完全放行不算数() =>
		Assert.Equal(PermissionGear.Trusted, ToolPermissionPolicy.Effective(PermissionGear.Bypass, null, Now));

	/// <summary>到期时刻只对完全放行有意义，不该把别的档也改掉。</summary>
	[Theory]
	[InlineData(PermissionGear.Ask)]
	[InlineData(PermissionGear.Session)]
	[InlineData(PermissionGear.Trusted)]
	public void 其余档位不受到期时刻影响(PermissionGear gear)
	{
		Assert.Equal(gear, ToolPermissionPolicy.Effective(gear, null, Now));
		Assert.Equal(gear, ToolPermissionPolicy.Effective(gear, Now.AddDays(-1), Now));
		Assert.Equal(gear, ToolPermissionPolicy.Effective(gear, Now.AddDays(1), Now));
	}

	[Fact]
	public void 剩余时间只在完全放行且未到期时有值()
	{
		DateTimeOffset until = Now.AddMinutes(30);
		Assert.Equal(TimeSpan.FromMinutes(30), ToolPermissionPolicy.BypassRemaining(PermissionGear.Bypass, until, Now));
		Assert.Null(ToolPermissionPolicy.BypassRemaining(PermissionGear.Bypass, Now.AddMinutes(-1), Now));
		Assert.Null(ToolPermissionPolicy.BypassRemaining(PermissionGear.Bypass, null, Now));
		Assert.Null(ToolPermissionPolicy.BypassRemaining(PermissionGear.Trusted, until, Now));
	}

	/// <summary>到期时刻写出去再读回来要一致，跨时区也是同一刻。</summary>
	[Fact]
	public void 到期时刻能原样来回()
	{
		DateTimeOffset deadline = ToolPermissionPolicy.BypassDeadline(Now);
		string stored = ToolPermissionPolicy.FormatDeadline(deadline);
		DateTimeOffset? parsed = ToolPermissionPolicy.ParseDeadline(stored);

		Assert.NotNull(parsed);
		Assert.Equal(deadline, parsed!.Value);
		Assert.Equal(deadline.ToUnixTimeMilliseconds(), parsed.Value.ToUnixTimeMilliseconds());
	}

	[Theory]
	[InlineData("")]
	[InlineData(null)]
	[InlineData("四小时后")]
	[InlineData("2026-13-45T99:99:99Z")]
	public void 读不出来的到期时刻当作没有(string? raw) =>
		Assert.Null(ToolPermissionPolicy.ParseDeadline(raw));
}
