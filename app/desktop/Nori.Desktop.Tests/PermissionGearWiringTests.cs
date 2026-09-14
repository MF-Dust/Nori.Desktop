using System.Text.Json;
using Nori.Core.Agent;
using Nori.Core.Configuration;
using Nori.Core.Tools;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>
/// 档位在宿主里真的接上了没有。
///
/// <c>ToolPermissionPolicyTests</c> 钉的是判据本身，这一族钉的是**调用点** ——
/// 判据写得再对，没人调它就等于不存在。这类「写了但没接」的漏法在这个仓库里出过
/// 一次（<c>RevokeAccess</c> 写了文档没接调用点），所以每一档都从
/// <c>RequestApprovalAsync</c> 这个真实入口验一遍，而不是直接调策略对象。
/// </summary>
public partial class BridgeCommandsTests
{
	private static ToolApprovalRequest GearRequest(string toolName, string level = "confirm") => new()
	{
		RequestId = $"gear-{Guid.NewGuid():N}",
		ToolName = toolName,
		PermissionLevel = level,
	};

	/// <summary>卡片发出去了没有。自动放行这条路上一张都不该有。</summary>
	private static bool SawApprovalCard(NativeChatTestSource source) =>
		source.Events.Any(payload =>
			payload.TryGetProperty("type", out JsonElement type)
			&& type.GetString() == "approval-request");

	private Task<object?> SetGearAsync(string gear) =>
		CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "settings_update_permission", Args(new {gear}));

	/// <summary>默认必须还是逐次确认：升级不能顺手把老用户放宽一档。</summary>
	[Fact]
	public void 默认档位是逐次确认()
	{
		Assert.Equal(PermissionGear.Ask, _runtime.StoredGear);
		Assert.Equal(PermissionGear.Ask, _runtime.EffectiveGear);
	}

	[Fact]
	public async Task 逐次确认下仍然弹卡片()
	{
		using NativeChatTestSource source = new();
		using CancellationTokenSource cts = new();

		Task<bool> decision = _runtime.RequestApprovalAsync(source, "s1", GearRequest("writeFile"), cts.Token);

		Assert.True(SawApprovalCard(source));
		await cts.CancelAsync();
		Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
	}

	[Fact]
	public async Task 完全授权不再弹确认()
	{
		await SetGearAsync("trusted");
		using NativeChatTestSource source = new();

		bool approved = await _runtime
			.RequestApprovalAsync(source, "s1", GearRequest("writeFile"), CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(2));

		Assert.True(approved);
		// 自动放行必须在发事件之前判掉，否则界面会闪一张卡片又自己消失。
		Assert.False(SawApprovalCard(source));
	}

	[Fact]
	public async Task 完全授权仍然拦高风险()
	{
		await SetGearAsync("trusted");
		using NativeChatTestSource source = new();
		using CancellationTokenSource cts = new();

		Task<bool> decision = _runtime.RequestApprovalAsync(source, "s1", GearRequest("wipeDisk", "dangerous"), cts.Token);

		Assert.True(SawApprovalCard(source));
		await cts.CancelAsync();
		Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
	}

	[Fact]
	public async Task 完全放行连高风险也不弹()
	{
		await SetGearAsync("bypass");
		using NativeChatTestSource source = new();

		bool approved = await _runtime
			.RequestApprovalAsync(source, "s1", GearRequest("wipeDisk", "dangerous"), CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(2));

		Assert.True(approved);
		Assert.False(SawApprovalCard(source));
	}

	/// <summary>
	/// 本轮记住：第一次弹，批准之后同一轮里的同一个工具不再弹，换一轮重新弹。
	///
	/// 这是四档里唯一需要跨调用保留状态的一档，也是唯一会因为"记错了范围"而
	/// 悄悄放宽授权的一档，所以三个边界一次测全。
	/// </summary>
	[Fact]
	public async Task 本轮记住只免掉同轮同工具()
	{
		await SetGearAsync("session");
		using NativeChatTestSource source = new();
		BridgeCommands commands = CreateCommands();

		ToolApprovalRequest first = GearRequest("writeFile");
		Task<bool> decision = _runtime.RequestApprovalAsync(source, "turn-1", first, CancellationToken.None);
		Assert.True(SawApprovalCard(source));
		await commands.InvokeAsync(source, "approval_respond", Args(new {requestId = first.RequestId, approved = true}));
		Assert.True(await decision.WaitAsync(TimeSpan.FromSeconds(2)));

		// 同一轮、同一个工具：不再弹。
		using NativeChatTestSource again = new();
		Assert.True(await _runtime
			.RequestApprovalAsync(again, "turn-1", GearRequest("writeFile"), CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(2)));
		Assert.False(SawApprovalCard(again));

		// 同一轮、换个工具：批准的是「写文件」，不是「随便做什么」。
		using NativeChatTestSource otherTool = new();
		using CancellationTokenSource cts = new();
		Task<bool> pending = _runtime.RequestApprovalAsync(otherTool, "turn-1", GearRequest("runTask"), cts.Token);
		Assert.True(SawApprovalCard(otherTool));
		await cts.CancelAsync();
		await pending.WaitAsync(TimeSpan.FromSeconds(2));

		// 下一轮：重新开始问。
		using NativeChatTestSource nextTurn = new();
		using CancellationTokenSource nextCts = new();
		Task<bool> next = _runtime.RequestApprovalAsync(nextTurn, "turn-2", GearRequest("writeFile"), nextCts.Token);
		Assert.True(SawApprovalCard(nextTurn));
		await nextCts.CancelAsync();
		await next.WaitAsync(TimeSpan.FromSeconds(2));
	}

	/// <summary>
	/// 拒绝和超时都不算同意。
	///
	/// 反过来写的代价很隐蔽：一次没人看见的超时会换来这一轮里后续的静默放行。
	/// </summary>
	[Fact]
	public async Task 本轮记住只认真的批准()
	{
		await SetGearAsync("session");
		using NativeChatTestSource source = new();
		BridgeCommands commands = CreateCommands();

		ToolApprovalRequest denied = GearRequest("writeFile");
		Task<bool> decision = _runtime.RequestApprovalAsync(source, "turn-1", denied, CancellationToken.None);
		await commands.InvokeAsync(source, "approval_respond", Args(new {requestId = denied.RequestId, approved = false}));
		Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(2)));

		using NativeChatTestSource after = new();
		using CancellationTokenSource cts = new();
		Task<bool> again = _runtime.RequestApprovalAsync(after, "turn-1", GearRequest("writeFile"), cts.Token);
		Assert.True(SawApprovalCard(after));
		await cts.CancelAsync();
		await again.WaitAsync(TimeSpan.FromSeconds(2));
	}

	/// <summary>
	/// 完全放行要写到期时刻，而且到期之后自己降档。
	///
	/// 这一条同时钉住「到期只在读取时判定」：配置里存的仍然是 bypass，变的只是生效值 ——
	/// 界面要能同时说出「你选的是完全放行」和「它已经到期」。
	/// </summary>
	[Fact]
	public async Task 完全放行会到期并降回完全授权()
	{
		await SetGearAsync("bypass");
		Assert.Equal(PermissionGear.Bypass, _runtime.EffectiveGear);
		Assert.NotNull(_runtime.BypassUntil);

		// 把到期时刻拨到过去，等价于那段时间过完了。
		_config.Set(
			ToolPermissionPolicy.KeyBypassUntil,
			new ConfigValue.Text(ToolPermissionPolicy.FormatDeadline(DateTimeOffset.UtcNow.AddMinutes(-1))));

		Assert.Equal(PermissionGear.Bypass, _runtime.StoredGear);
		Assert.Equal(PermissionGear.Trusted, _runtime.EffectiveGear);
	}

	/// <summary>换回别的档要把到期时刻清掉，否则下次再选完全放行会捡到一个旧期限。</summary>
	[Fact]
	public async Task 换回别的档会清掉到期时刻()
	{
		await SetGearAsync("bypass");
		Assert.NotNull(_runtime.BypassUntil);

		await SetGearAsync("ask");

		Assert.Null(_runtime.BypassUntil);
		Assert.Equal(PermissionGear.Ask, _runtime.EffectiveGear);
	}

	/// <summary>
	/// 写入侧要严格。
	///
	/// 读取侧把认不出的值退到最严的一档是对的（配置损坏时宁可多问几次），但在写入侧
	/// 沿用同一条规则，会把一次拼错的调用变成一次静默的「改成逐次确认」——
	/// 调用方以为自己设成了 trusted。
	/// </summary>
	[Fact]
	public async Task 拼错的档位要报错而不是静默改档()
	{
		await SetGearAsync("trusted");

		await Assert.ThrowsAsync<InvalidOperationException>(() => SetGearAsync("full"));

		Assert.Equal(PermissionGear.Trusted, _runtime.StoredGear);
	}

	/// <summary>快照要同时报出「你选的」和「现在生效的」，界面靠这两项说清到期。</summary>
	[Fact]
	public async Task 快照报出档位与生效值()
	{
		await SetGearAsync("session");
		JsonElement snapshot = JsonSerializer.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options);
		JsonElement permissions = snapshot.GetProperty("workspace").GetProperty("permissions");

		Assert.Equal("session", permissions.GetProperty("gear").GetString());
		Assert.Equal("session", permissions.GetProperty("effective").GetString());
		Assert.False(permissions.GetProperty("safeMode").GetBoolean());
	}

	/// <summary>
	/// 安全模式压过任何一档。
	///
	/// 安全模式是用来收拾残局的，把完全放行打开不能让它变成最宽的那一档。
	/// </summary>
	[Fact]
	public async Task 安全模式下完全放行也不放行()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		await fixture.SetGearAsync("bypass");
		using NativeChatTestSource source = new();

		Assert.False(await fixture._runtime
			.RequestApprovalAsync(source, "s1", GearRequest("writeFile"), CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(2)));
		Assert.False(SawApprovalCard(source));
	}

	/* ── 自动化：接管鼠标键盘 ──────────────────────────────────────────────
	 * 这条路有自己的一套审批，不走工具那条。它是「写了档位但漏接一处」最可能
	 * 发生的地方，所以两档都从真实入口验。 */

	private static Nori.Core.Automation.AutomationApprovalRequest AutomationRequest() => new(
		Guid.NewGuid(),
		Guid.NewGuid(),
		[Nori.Core.Automation.AutomationActionKind.Click],
		DateTimeOffset.UtcNow);

	/// <summary>接管鼠标键盘按 dangerous 算：「完全授权」这一档仍然要问。</summary>
	[Fact]
	public async Task 完全授权下自动化仍然要问()
	{
		await SetGearAsync("trusted");
		using CancellationTokenSource cts = new();

		Task<Nori.Core.Automation.AutomationApprovalDecision> decision =
			_runtime.RequestAutomationApprovalAsync(AutomationRequest(), cts.Token);

		// 没有立刻拿到结论 —— 说明它在等人，而不是自己放行了。
		Assert.False(decision.IsCompleted);
		await cts.CancelAsync();
		try { await decision.WaitAsync(TimeSpan.FromSeconds(2)); }
		catch (OperationCanceledException) { /* 取消即未放行，正是要的 */ }
	}

	/// <summary>「完全放行」才免掉。这也是它和完全授权在今天唯一真实的差别。</summary>
	[Fact]
	public async Task 完全放行下自动化不再问()
	{
		await SetGearAsync("bypass");

		Nori.Core.Automation.AutomationApprovalDecision decision = await _runtime
			.RequestAutomationApprovalAsync(AutomationRequest(), CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(2));

		Assert.Equal(Nori.Core.Automation.AutomationApprovalOutcome.Approved, decision.Outcome);
	}
}
