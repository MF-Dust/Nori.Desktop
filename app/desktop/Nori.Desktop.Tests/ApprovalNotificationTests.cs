using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
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
		public int? HideThreadId { get; private set; }
		public bool FailOnShow { get; init; }
		public bool FailOnHide { get; init; }

		public void Show(ApprovalNotice notice)
		{
			if (FailOnShow) throw new InvalidOperationException("模拟通知显示失败");
			Shown.Add(notice);
		}
		public void Hide(string requestId)
		{
			HideThreadId = Environment.CurrentManagedThreadId;
			if (FailOnHide) throw new InvalidOperationException("模拟通知撤销失败");
			Hidden.Add(requestId);
		}
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

	private Task ActivateToastFromBackgroundAsync(string arguments) => Task.Run(() =>
	{
		// 必须从真实后台线程进入，不能让 UI 线程内联掩盖漏调度的问题。
		Assert.False(Dispatcher.UIThread.CheckAccess());
		return _runtime.OnToastActivatedAsync(arguments);
	}).WaitAsync(TimeSpan.FromSeconds(5));

	[Theory]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public Task 通知呈现异常不丢失授权也不阻断完成(bool failOnShow, bool failOnHide) => WithSettingsUiAsync(async () =>
	{
		FakeNotifier notifier = new() {FailOnShow = failOnShow, FailOnHide = failOnHide};
		_runtime.UseNotifierForTests(notifier);
		using NativeChatTestSource source = new();
		int results = 0;
		source.Received = payload =>
		{
			if (payload.GetProperty("type").GetString() == "approval-result") results++;
		};
		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", Request(), CancellationToken.None);
		Assert.False(decision.IsCompleted);

		await ActivateToastFromBackgroundAsync(ToastActivation.Encode(ToastAction.Deny, "approval-one"));

		Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
		Assert.Equal(1, results);
		Assert.False(_runtime.RespondApprovalFromNotification("approval-one", true));
		Assert.False(_runtime.Permissions.Remembered("session", "writeFile"));
		if (failOnShow)
			Assert.Contains(_services.Logger.RecentLogs(), entry => entry.Message == "显示系统通知失败：InvalidOperationException");
		if (failOnHide)
			Assert.Contains(_services.Logger.RecentLogs(), entry => entry.Message == "撤销系统通知失败：InvalidOperationException");
	});

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public Task 从通知上的决定在UI线程收尾且不抢焦点(bool approved) => WithSettingsUiAsync(async () =>
	{
		int uiThreadId = Environment.CurrentManagedThreadId;
		int resultThreadId = 0;
		FakeNotifier notifier = new();
		_runtime.UseNotifierForTests(notifier);
		using NativeChatTestSource source = new() {IsVisible = false};
		source.Received = payload =>
		{
			if (payload.GetProperty("type").GetString() == "approval-result")
				resultThreadId = Environment.CurrentManagedThreadId;
		};

		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", Request(), CancellationToken.None);
		await ActivateToastFromBackgroundAsync(ToastActivation.Encode(
			approved ? ToastAction.Allow : ToastAction.Deny, "approval-one"));

		Assert.Equal(approved, await decision.WaitAsync(TimeSpan.FromSeconds(2)));
		Assert.Equal(approved, _runtime.Permissions.Remembered("session", "writeFile"));
		Assert.Equal("approval-one", Assert.Single(notifier.Hidden));
		Assert.Equal(uiThreadId, notifier.HideThreadId);
		Assert.Equal(uiThreadId, resultThreadId);
		Assert.False(_windows.IsWindowVisible(WindowLabels.Main));
		Assert.Equal(0, _windows.ChatShowCount);
	});

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public Task 通知正文在UI线程打开且窗口异常不泄漏(bool failOnOpen) => WithSettingsUiAsync(async () =>
	{
		int uiThreadId = Environment.CurrentManagedThreadId;
		int showThreadId = 0;
		FakeNotifier notifier = new();
		_runtime.UseNotifierForTests(notifier);
		using NativeChatTestSource source = new();
		_windows.VisibilityChanged += (label, visible) =>
		{
			if (label != WindowLabels.Main || !visible) return;
			showThreadId = Environment.CurrentManagedThreadId;
			if (failOnOpen) throw new InvalidOperationException("模拟窗口打开失败");
		};
		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", Request(), CancellationToken.None);

		await ActivateToastFromBackgroundAsync(ToastActivation.Open("approval-one"));

		Assert.Equal(uiThreadId, showThreadId);
		Assert.False(decision.IsCompleted);
		Assert.Empty(notifier.Hidden);
		if (failOnOpen)
			Assert.Contains(_services.Logger.RecentLogs(), entry => entry.Message == "处理系统通知失败：InvalidOperationException");
		await ActivateToastFromBackgroundAsync(ToastActivation.Encode(ToastAction.Deny, "approval-one"));
		Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
	});

	[Theory]
	[InlineData("")]
	[InlineData("action=allow")]
	[InlineData("action=unknown&id=approval-one")]
	[InlineData("action=allow&id=approval-missing")]
	[InlineData("action=deny&id=approval-missing")]
	[InlineData("action=open&id=approval-missing")]
	public Task 后台无效通知不决定授权也不打开窗口(string arguments) => WithSettingsUiAsync(async () =>
	{
		FakeNotifier notifier = new();
		_runtime.UseNotifierForTests(notifier);
		using NativeChatTestSource source = new();
		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", Request(), CancellationToken.None);

		await ActivateToastFromBackgroundAsync(arguments);

		Assert.False(decision.IsCompleted);
		Assert.Empty(notifier.Hidden);
		Assert.False(_windows.IsWindowVisible(WindowLabels.Main));
		Assert.Equal(0, _windows.ChatShowCount);
		await ActivateToastFromBackgroundAsync(ToastActivation.Encode(ToastAction.Deny, "approval-one"));
		Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(2)));
	});

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public Task 已处理或取消的通知正文不再打开窗口(bool cancelled) => WithSettingsUiAsync(async () =>
	{
		_runtime.UseNotifierForTests(new FakeNotifier());
		using NativeChatTestSource source = new();
		using CancellationTokenSource cancellation = new();
		Task<bool> decision = _runtime.RequestApprovalAsync(source, "session", Request(), cancellation.Token);
		if (cancelled) cancellation.Cancel();
		else await ActivateToastFromBackgroundAsync(ToastActivation.Encode(ToastAction.Deny, "approval-one"));
		Assert.False(await decision.WaitAsync(TimeSpan.FromSeconds(2)));

		await ActivateToastFromBackgroundAsync(ToastActivation.Open("approval-one"));

		Assert.False(_windows.IsWindowVisible(WindowLabels.Main));
		Assert.Equal(0, _windows.ChatShowCount);
	});

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
	public void 快照带出通知开关与本平台支不支持()
	{
		JsonElement workspace = JsonSerializer
			.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options)
			.GetProperty("workspace");

		Assert.True(workspace.GetProperty("toastApprovals").GetBoolean());
		// 自动化测试的 OperatingSystem 别名恒为 Windows；通知契约必须看真实宿主平台。
		Assert.Equal(System.OperatingSystem.IsWindows(), workspace.GetProperty("toastSupported").GetBoolean());

		// 这里只验证快照投影，不能调用会删除真实快捷方式与注册表项的注销命令。
		_config.Set(ConfigStore.KeyToastApprovals, new ConfigValue.Boolean(false));
		_runtime.InvalidateSnapshot("workspace");

		JsonElement after = JsonSerializer
			.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options)
			.GetProperty("workspace");
		Assert.False(after.GetProperty("toastApprovals").GetBoolean());
		Assert.Equal(System.OperatingSystem.IsWindows(), after.GetProperty("toastSupported").GetBoolean());
	}
}
