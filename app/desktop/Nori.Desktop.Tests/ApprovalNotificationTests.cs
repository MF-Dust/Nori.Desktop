using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Agent;
using Nori.Core.Configuration;
using Nori.Core.Notifications;
using Nori.Core.Tools;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>
/// 待决授权与系统通知的接线。
///
/// 这一族不碰真的 COM：真通知要建开始菜单快捷方式、写 HKCU，跑在测试机上会留下东西，
/// 而 CI 的会话里本来也弹不出来。这里换成一个假的呈现层，验的是**接线**：
/// 什么时候弹、什么时候收、从通知回来的决定算不算数。
///
/// 通知这条路存在的前提是「应用内的卡片不撤」，所以每一条都同时确认原来那条还在。
/// </summary>
public partial class BridgeCommandsTests
{
	/// <summary>记下被要求弹和收的那些，不做任何真实呈现。</summary>
	private sealed class FakeNotifier : INativeNotifier
	{
		public List<ApprovalNotice> Shown { get; } = [];
		public List<string> Hidden { get; } = [];
		public bool Available { get; set; } = true;

		public void Show(ApprovalNotice notice) => Shown.Add(notice);
		public void Hide(string requestId) => Hidden.Add(requestId);
	}

	private ToolApprovalRequest Request(string id = "approval-one", string level = "confirm") => new()
	{
		RequestId = id,
		ToolName = "writeFile",
		PermissionLevel = level,
		Description = "把文本写进工作目录",
		Arguments = new JsonObject {["path"] = "笔记/待办.md"},
		DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(2),
	};

	[Fact]
	public async Task 待决授权会同时发一条系统通知()
	{
		FakeNotifier notifier = new();
		_runtime.UseNotifierForTests(notifier);
		FakeBridgeSource source = new(WindowLabels.Main);

		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", Request(), CancellationToken.None);

		ApprovalNotice notice = Assert.Single(notifier.Shown);
		Assert.Equal("approval-one", notice.RequestId);
		Assert.Equal("writeFile", notice.ToolName);
		Assert.Equal("confirm", notice.PermissionLevel);
		// 参数要带上，否则通知上只有工具名，判断不了该不该允许。
		Assert.Contains("笔记/待办.md", notice.ArgumentSummary);

		Assert.True(_runtime.RespondApproval(source, "approval-one", true));
		Assert.True(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
	}

	/// <summary>通知只是第二个面，应用内那条事件一条都不能少。</summary>
	[Fact]
	public async Task 发了通知之后应用内的卡片照旧()
	{
		FakeNotifier notifier = new();
		_runtime.UseNotifierForTests(notifier);
		FakeBridgeSource source = new(WindowLabels.Main);

		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", Request(), CancellationToken.None);

		// 应用内那条仍然在等、仍然可以由窗口解掉 —— 这就是「卡片没被撤掉」。
		Assert.True(_runtime.RespondApproval(source, "approval-one", false));
		Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
	}

	/// <summary>在应用内点掉之后，屏幕上那条要收走 —— 留一张点了没反应的卡片比不弹更糟。</summary>
	[Fact]
	public async Task 在应用内批准之后通知被收掉()
	{
		FakeNotifier notifier = new();
		_runtime.UseNotifierForTests(notifier);
		FakeBridgeSource source = new(WindowLabels.Main);

		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", Request(), CancellationToken.None);
		Assert.Empty(notifier.Hidden);

		Assert.True(_runtime.RespondApproval(source, "approval-one", true));
		Assert.True(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
		Assert.Equal("approval-one", Assert.Single(notifier.Hidden));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task 从通知上的决定算数(bool approved)
	{
		FakeNotifier notifier = new();
		_runtime.UseNotifierForTests(notifier);
		FakeBridgeSource source = new(WindowLabels.Main);

		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", Request(), CancellationToken.None);

		Assert.True(_runtime.RespondApprovalFromNotification("approval-one", approved));
		Assert.Equal(approved, await decision.WaitAsync(TimeSpan.FromSeconds(2)));
		// 通知这条路结束的，同样要把屏幕上那条收掉。
		Assert.Equal("approval-one", Assert.Single(notifier.Hidden));
	}

	/// <summary>
	/// 通知不属于任何一个窗口，所以它走的是不校验来源的那条路。
	/// 但**其余判定一条不少** —— 这里验的是找不到对应授权时不能返回真。
	/// </summary>
	[Fact]
	public void 对不上的授权id一律不算数()
	{
		_runtime.UseNotifierForTests(new FakeNotifier());

		Assert.False(_runtime.RespondApprovalFromNotification("approval-不存在", true));
		Assert.False(_runtime.RespondApprovalFromNotification("", true));
	}

	/// <summary>只有真的按了允许才记住；从通知拒绝不该让后面整轮静默放行。</summary>
	[Fact]
	public async Task 从通知拒绝不会被记成本轮已允许()
	{
		_runtime.UseNotifierForTests(new FakeNotifier());
		FakeBridgeSource source = new(WindowLabels.Main);

		Task<bool> denied = _runtime.RequestApprovalAsync(source, "记忆会话", Request("approval-deny"), CancellationToken.None);
		Assert.True(_runtime.RespondApprovalFromNotification("approval-deny", false));
		Assert.False(await denied.WaitAsync(TimeSpan.FromSeconds(2)));

		Assert.False(_runtime.Permissions.Remembered("记忆会话", "writeFile"));
	}

	/// <summary>两条路解同一个授权，谁先到算谁的；后到的那条不能再翻一次。</summary>
	[Fact]
	public async Task 通知先回之后应用内再回一次不算数()
	{
		_runtime.UseNotifierForTests(new FakeNotifier());
		FakeBridgeSource source = new(WindowLabels.Main);

		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", Request(), CancellationToken.None);

		Assert.True(_runtime.RespondApprovalFromNotification("approval-one", false));
		Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
		Assert.False(_runtime.RespondApproval(source, "approval-one", true));
	}

	/// <summary>关掉开关就一条都不发。</summary>
	[Fact]
	public async Task 关掉之后不再发通知()
	{
		FakeNotifier notifier = new();
		_runtime.UseNotifierForTests(notifier);
		_config.Set(ConfigStore.KeyToastApprovals, new ConfigValue.Text("0"));
		FakeBridgeSource source = new(WindowLabels.Main);

		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", Request(), CancellationToken.None);

		Assert.Empty(notifier.Shown);
		// 但授权本身照走 —— 关掉的是通知，不是确认。
		Assert.True(_runtime.RespondApproval(source, "approval-one", false));
		Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
	}

	/// <summary>呈现层说自己弹不出来时不硬发，但授权照常走完。</summary>
	[Fact]
	public async Task 呈现层不可用时不发但授权照常()
	{
		FakeNotifier notifier = new() {Available = false};
		_runtime.UseNotifierForTests(notifier);
		FakeBridgeSource source = new(WindowLabels.Main);

		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", Request(), CancellationToken.None);

		Assert.Empty(notifier.Shown);
		Assert.True(_runtime.RespondApproval(source, "approval-one", true));
		Assert.True(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
	}

	/// <summary>按档位自动放行的那些根本不该弹 —— 用户选的就是「别问我」。</summary>
	[Fact]
	public async Task 按档位自动放行的不弹通知()
	{
		FakeNotifier notifier = new();
		_runtime.UseNotifierForTests(notifier);
		_config.Set(ToolPermissionPolicy.KeyGear, new ConfigValue.Text("trusted"));
		FakeBridgeSource source = new(WindowLabels.Main);

		Assert.True(await _runtime.RequestApprovalAsync(
			source, "session", Request(level: "confirm"), CancellationToken.None));
		Assert.Empty(notifier.Shown);
	}

	[Fact]
	public async Task 快照带出通知开关与本平台支不支持()
	{
		JsonElement workspace = JsonSerializer
			.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options)
			.GetProperty("workspace");

		Assert.True(workspace.GetProperty("toastApprovals").GetBoolean());
		Assert.Equal(OperatingSystem.IsWindows(), workspace.GetProperty("toastSupported").GetBoolean());

		await CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "settings_update_notifications", Args(new {enabled = false}));

		JsonElement after = JsonSerializer
			.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options)
			.GetProperty("workspace");
		Assert.False(after.GetProperty("toastApprovals").GetBoolean());
	}
}
