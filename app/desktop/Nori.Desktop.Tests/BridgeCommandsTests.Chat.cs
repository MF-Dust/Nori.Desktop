using System.Text.Json;
using Nori.Core.Chat.LuoLiCore;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	/// <summary>
	/// 安全模式禁用一切外部调用（AGENTS.md §1），重置远端会话是一次真正的出网请求。
	///
	/// 但清空本地记录本身是纯本地操作，不该被一起禁掉 —— 排障的时候连清个记录都做不到是过度
	/// 收紧。所以这一条要的是：跳过远端、本地照清、并且把「远端没清」如实报给调用方。
	/// </summary>
	[Fact]
	public async Task 安全模式下清空聊天记录不去碰远端()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture.ConfigureUnreachableLuoLiCore();
		fixture._services.Chat.SaveMessage("user", "在吗");
		fixture._services.Chat.SaveMessage("assistant", "在的");

		object? result = await fixture.CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "chat_clear", Args(new { }));

		// 连不上那个端口的话这里会抛，能返回就说明确实没发那次请求。
		string json = JsonSerializer.Serialize(result, BridgeJson.Options);
		Assert.Contains("\"remoteReset\":false", json, StringComparison.Ordinal);
		Assert.Contains("安全模式", json, StringComparison.Ordinal);
		Assert.Empty(fixture._services.Chat.GetHistory());
		Assert.Equal("", fixture._config.GetStringOr(LuoLiCoreSettingsStore.KeySessionId, ""));
		Assert.Equal("", fixture._config.GetStringOr(LuoLiCoreSettingsStore.KeySessionOwner, ""));
	}

	/// <summary>
	/// 非安全模式下这条命令**必须**真的去调远端 —— 否则上一条用例是恒真的。
	///
	/// 远端不可达时整条命令失败、本地记录原样留着：两边宁可都不清，也不能一边清了一边没清。
	/// </summary>
	[Fact]
	public async Task 非安全模式下清空会真的去调远端()
	{
		ConfigureUnreachableLuoLiCore();
		_services.Chat.SaveMessage("user", "在吗");

		await Assert.ThrowsAnyAsync<Exception>(() => CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "chat_clear", Args(new { })));

		Assert.NotEmpty(_services.Chat.GetHistory());
	}

	/// <summary>没接外部后端时不该给出那句提示 —— 它只会让人以为有什么东西没清干净。</summary>
	[Fact]
	public async Task 没接外部后端时安全模式下照常清空且不提示()
	{
		using BridgeCommandsTests fixture = new(safeMode: true);
		fixture._services.Chat.SaveMessage("user", "在吗");

		object? result = await fixture.CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "chat_clear", Args(new { }));

		string json = JsonSerializer.Serialize(result, BridgeJson.Options);
		Assert.DoesNotContain("安全模式", json, StringComparison.Ordinal);
		Assert.Empty(fixture._services.Chat.GetHistory());
	}

	[Fact]
	public async Task 安全模式仍可安装内置市场技能()
	{
		using BridgeCommandsTests fixture = new(true);
		object? result = await fixture.CreateCommands().InvokeAsync(
			new FakeBridgeSource(WindowLabels.Main), "skills_install_marketplace", Args(new {skillId = "gaming-partner"}));

		string json = JsonSerializer.Serialize(result, BridgeJson.Options);
		Assert.Contains("\"id\":\"gaming-partner\"", json, StringComparison.Ordinal);
		Assert.DoesNotContain("安全模式", json, StringComparison.Ordinal);
	}
}
