using System.Linq;
using Nori.Core.Proactive;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	// ---- 提醒持久化 ----

	[Fact]
	public async Task reminder_add落库并可被新store恢复()
	{
		BridgeCommands commands = CreateCommands();
		object? added = await commands.InvokeAsync(
			new FakeBridgeSource("main"), "reminder_add", Args(new {content = "喝水", delayMinutes = 30}));
		Assert.NotNull(added);

		// 新的 store 实例从同一数据库读到该提醒 (重启恢复语义)
		Nori.Core.Proactive.ReminderStore store = new(_database);
		Assert.Single(store.List(), item => item.Content == "喝水");
	}

	[Fact]
	public async Task 到期提醒由TakeDue领取并等待确认()
	{
		Nori.Core.Proactive.ReminderStore store = new(_database);
		store.Add("过期提醒", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000);

		var due = store.TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
		Assert.Single(due);
		Assert.Empty(store.TakeDue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
	}

	[Fact]
	public async Task reminder_update更新快照并可被列表读回()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		Nori.Core.Proactive.ReminderItem added = Assert.IsType<Nori.Core.Proactive.ReminderItem>(await commands.InvokeAsync(
			main, "reminder_add", Args(new {content = "原始提醒", delayMinutes = 30})));

		int before = _runtime.SnapshotVersion;
		long triggerTime = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds();
		Nori.Core.Proactive.ReminderItem updated = Assert.IsType<Nori.Core.Proactive.ReminderItem>(await commands.InvokeAsync(
			main, "reminder_update", Args(new
			{
				id = added.Id,
				content = "更新提醒",
				triggerTime,
				repeatDaily = true,
				timezone = "UTC",
				recurrenceJson = "{\"type\":\"daily\"}",
			})));
		Assert.True(_runtime.SnapshotVersion > before);
		Assert.Equal("更新提醒", updated.Content);
		Assert.True(updated.RepeatDaily);
		Assert.Equal("UTC", updated.Timezone);
		Assert.Equal("{\"type\":\"daily\"}", updated.RecurrenceJson);

		object? listed = await commands.InvokeAsync(main, "reminder_list", Args(new { }));
		Nori.Core.Proactive.ReminderItem shown = Assert.Single((IReadOnlyList<Nori.Core.Proactive.ReminderItem>)listed!);
		Assert.Equal("更新提醒", shown.Content);
	}

	[Fact]
	public async Task reminder_cancel保持旧返回值并写入取消终态()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource main = new(WindowLabels.Main);
		Nori.Core.Proactive.ReminderItem added = Assert.IsType<Nori.Core.Proactive.ReminderItem>(await commands.InvokeAsync(
			main, "reminder_add", Args(new {content = "待取消提醒", delayMinutes = 15})));
		Assert.Equal(true, await commands.InvokeAsync(main, "reminder_cancel", Args(new {id = added.Id})));
		Assert.Equal("cancelled", new Nori.Core.Proactive.ReminderStore(_database).Get(added.Id)!.Status);
		Assert.Equal(false, await commands.InvokeAsync(main, "reminder_cancel", Args(new {id = added.Id})));
	}

	[Fact]
	public async Task reminder命令拒绝非main和越界参数()
	{
		BridgeCommands commands = CreateCommands();
		FakeBridgeSource pet = new(WindowLabels.Pet);
		string[] commandsToCheck = ["reminder_update", "reminder_list"];
		foreach (string command in commandsToCheck)
		{
			await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(pet, command, Args(new {id = "missing"})));
		}

		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "reminder_add", Args(new {content = new string('x', 201), delayMinutes = 15})));
		Nori.Core.Proactive.ReminderItem added = Assert.IsType<Nori.Core.Proactive.ReminderItem>(await commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "reminder_add", Args(new {content = "边界提醒", delayMinutes = 15})));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "reminder_update", Args(new {id = added.Id, timezone = "Not/AZone"})));
		await Assert.ThrowsAsync<InvalidOperationException>(() => commands.InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "reminder_update", Args(new {id = added.Id})));
		Assert.DoesNotContain("边界提醒", string.Join("\n", _services.Logger.RecentLogs().Select(entry => entry.Message)), StringComparison.Ordinal);
	}
}
