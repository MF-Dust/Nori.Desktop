using System.Threading.Channels;
using Nori.Core.Notifications;
using Nori.Core.Platform;

namespace Nori.Core.Tests;

public sealed class LinuxNativeNotifierTests
{
	private sealed class FakeNotification : ILinuxApprovalNotification
	{
		public bool SupportsActions { get; set; } = true;
		internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource<uint> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal bool Disposed { get; private set; }
		internal int CloseCount { get; private set; }
		private Action<LinuxNotificationSignal>? _signal;
		public async Task<uint> SendAsync(ApprovalNotice notice, bool english, Action<LinuxNotificationSignal> signal, CancellationToken cancellationToken)
		{
			_signal = signal;
			Started.TrySetResult();
			return await Reply.Task.WaitAsync(cancellationToken);
		}
		public Task CloseAsync(uint id, CancellationToken cancellationToken)
		{
			Assert.Equal(7u, id);
			CloseCount++;
			Closed.TrySetResult();
			return Task.CompletedTask;
		}
		internal void Emit(string? action, uint id = 7) => _signal!(new(id, action));
		public void Dispose() => Disposed = true;
	}

	private static ApprovalNotice Notice(string id = "approval-test") => new()
	{
		RequestId = id, ToolName = "<b>writeFile</b>", Description = "敏感描述", ArgumentSummary = "敏感参数",
	};
	private static async Task Wait(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(6));

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void 内容双语且不泄漏描述与参数(bool english)
	{
		var content = LinuxApprovalNotification.Format(Notice(), english, ["actions", "body-markup"]);
		Assert.Contains("&lt;b&gt;writeFile&lt;/b&gt;", content.Body);
		Assert.DoesNotContain("敏感", content.Body);
		Assert.Contains(english ? "Open Nori" : "主界面确认", content.Body);
		Assert.Equal(new[] { "default", english ? "Open Nori" : "打开 Nori",
			"action=allow&id=approval-test", english ? "Allow" : "允许执行",
			"action=deny&id=approval-test", english ? "Deny" : "拒绝" }, content.Actions);
	}

	[Fact]
	public void 无动作能力仅显示纯文本且截断工具名()
	{
		var content = LinuxApprovalNotification.Format(Notice(), false, []);
		Assert.Empty(content.Actions);
		Assert.Contains("<b>writeFile</b>", content.Body);
		var longContent = LinuxApprovalNotification.Format(Notice() with { ToolName = new string('x', 500) }, true, []);
		Assert.Contains(new string('x', ApprovalToastXml.MaxSummaryLength) + "…", longContent.Body);
		Assert.DoesNotContain(new string('x', 100), longContent.Body);
	}

	[Fact]
	public async Task 回包前撤回仍会关闭通知且不接受迟到动作()
	{
		FakeNotification client = new();
		List<string> activated = [];
		await using LinuxNativeNotifier notifier = new(() => false, activated.Add, _ => Assert.Fail(), () => client);
		notifier.Show(Notice());
		await Wait(client.Started.Task);
		notifier.Hide("approval-test");
		client.Emit("action=allow&id=approval-test");
		client.Reply.SetResult(7);
		await Wait(client.Closed.Task);
		await notifier.DisposeAsync();
		Assert.Empty(activated);
		Assert.True(client.Disposed);
		Assert.Equal(1, client.CloseCount);
	}

	[Theory]
	[InlineData("allow")]
	[InlineData("deny")]
	public async Task 正文只打开且只接受本条通知的精确按钮并忽略重放(string action)
	{
		FakeNotification client = new();
		Channel<string> activated = Channel.CreateUnbounded<string>();
		await using LinuxNativeNotifier notifier = new(() => true, value => activated.Writer.TryWrite(value), _ => Assert.Fail(), () => client);
		notifier.Show(Notice());
		notifier.Show(Notice());
		await Wait(client.Started.Task);
		client.Emit("action=allow&id=other");
		client.Emit("allow");
		client.Emit("action=allow&id=approval-test", id: 8);
		client.Emit("action=open&id=approval-test");
		client.Emit("default");
		client.Reply.SetResult(7);
		Assert.Equal(ToastActivation.Open("approval-test"), await activated.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6)));
		string expected = $"action={action}&id=approval-test";
		client.Emit(expected);
		client.Emit(expected);
		Assert.Equal(expected, await activated.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6)));
		await Wait(client.Closed.Task);
		await notifier.DisposeAsync();
		Assert.False(activated.Reader.TryRead(out _));
		Assert.Equal(1, client.CloseCount);
	}

	[Fact]
	public async Task 系统关闭通知不产生审批决定且其他通知仍可操作()
	{
		FakeNotification first = new(), second = new();
		Queue<FakeNotification> clients = new([first, second]);
		Channel<string> activated = Channel.CreateUnbounded<string>();
		await using LinuxNativeNotifier notifier = new(() => false, value => activated.Writer.TryWrite(value), _ => Assert.Fail(), () => clients.Dequeue());
		notifier.Show(Notice("first"));
		notifier.Show(Notice("second"));
		await Wait(first.Started.Task);
		first.Reply.SetResult(7);
		await Wait(second.Started.Task);
		second.Reply.SetResult(7);
		first.Emit(null);
		first.Emit("action=allow&id=first");
		second.Emit("default");
		Assert.Equal(ToastActivation.Open("second"), await activated.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6)));
		Assert.True(first.Disposed);
		Assert.Equal(0, first.CloseCount);
		await notifier.DisposeAsync();
		Assert.False(activated.Reader.TryRead(out _));
		Assert.Equal(1, second.CloseCount);
	}

	[Fact]
	public async Task 不支持按钮时忽略动作并在退出时撤回()
	{
		FakeNotification client = new() { SupportsActions = false }, barrier = new();
		Queue<FakeNotification> clients = new([client, barrier]);
		List<string> activated = [];
		await using LinuxNativeNotifier notifier = new(() => false, activated.Add, _ => Assert.Fail(), () => clients.Dequeue());
		notifier.Show(Notice());
		await Wait(client.Started.Task);
		client.Emit("default");
		client.Emit("action=allow&id=approval-test");
		client.Reply.SetResult(7);
		notifier.Show(Notice("barrier"));
		await Wait(barrier.Started.Task);
		Assert.Empty(activated);
		barrier.Reply.SetResult(7);
		// Dispose 必须等待在途回包并关闭，不能仅丢掉连接。
		await notifier.DisposeAsync();
		Assert.False(notifier.Available);
		Assert.Empty(activated);
		Assert.Equal(1, client.CloseCount);
		Assert.True(client.Disposed);
	}

	[Fact]
	public async Task 服务和日志故障不传播且后续请求仍能发送()
	{
		FakeNotification client = new();
		int attempts = 0, failures = 0;
		await using LinuxNativeNotifier notifier = new(() => false, _ => { }, _ => { failures++; throw new InvalidOperationException(); },
			() => ++attempts == 1 ? throw new InvalidOperationException("服务不可用") : client);
		notifier.Show(Notice("first"));
		notifier.Show(Notice("second"));
		await Wait(client.Started.Task);
		client.Reply.SetResult(7);
		await notifier.DisposeAsync();
		Assert.Equal(1, failures);
		Assert.Equal(1, client.CloseCount);
	}

	[Fact]
	public async Task 在途请求不回包时有界取消并释放连接()
	{
		FakeNotification client = new();
		int failures = 0;
		await using LinuxNativeNotifier notifier = new(() => false, _ => Assert.Fail(), _ => failures++, () => client);
		notifier.Show(Notice());
		await Wait(client.Started.Task);
		await Wait(notifier.DisposeAsync().AsTask());
		Assert.Equal(1, failures);
		Assert.True(client.Disposed);
	}
}
