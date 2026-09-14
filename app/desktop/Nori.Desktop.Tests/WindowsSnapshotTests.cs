using System.Text.Json;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

/// <summary>
/// 快照里的 windows 段。
///
/// 侧边栏那四项点下去是**另开一个窗口**，不是切页。界面要说出「它已经开着了」，
/// 靠的就是这四个布尔量。
///
/// 这一段此前**不存在**，而 <c>VisibilityChanged</c> 里一直在调
/// <c>InvalidateSnapshot("windows")</c> —— 也就是说刷新那条路早就接好了，缺的是
/// 被刷新的东西本身，而缺了不报错：界面只是永远拿不到状态，于是只能画一个恒为假的
/// 「未选中」。这一族钉住它别再消失。
/// </summary>
public partial class BridgeCommandsTests
{
	[Fact]
	public void 快照带出四个窗口的开关状态()
	{
		JsonElement windows = JsonSerializer
			.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options)
			.GetProperty("windows");

		// 四个键都要在。少一个, 侧边栏对应那一项就永远不会亮, 而且不报错。
		foreach (string key in new[] {"chat", "models", "memory", "settings"})
		{
			Assert.True(windows.TryGetProperty(key, out JsonElement value), key);
			Assert.Equal(JsonValueKind.False, value.ValueKind);
		}
	}

	[Fact]
	public void 窗口打开之后快照跟着变()
	{
		_windows.SetVisible(WindowLabels.Chat, true);
		_windows.SetVisible(WindowLabels.Memory, true);

		JsonElement windows = JsonSerializer
			.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options)
			.GetProperty("windows");

		Assert.True(windows.GetProperty("chat").GetBoolean());
		Assert.True(windows.GetProperty("memory").GetBoolean());
		// 没开的仍然是 false —— 四个键各报各的, 不是一个总开关。
		Assert.False(windows.GetProperty("models").GetBoolean());
		Assert.False(windows.GetProperty("settings").GetBoolean());
	}

	/// <summary>
	/// 伴侣窗口不在这一段里。
	///
	/// 它有自己的 <c>pet.visible</c>，而且语义不同：那四个是「你打开的窗口」，
	/// 伴侣是「她在不在桌面上」。混成一段会让界面把两件事画成一件。
	/// </summary>
	[Fact]
	public void 伴侣窗口仍然单独一段()
	{
		_windows.SetVisible(WindowLabels.Pet, true);
		JsonElement snapshot = JsonSerializer.SerializeToElement(_runtime.BuildSnapshot(), BridgeJson.Options);

		Assert.True(snapshot.GetProperty("pet").GetProperty("visible").GetBoolean());
		Assert.False(snapshot.GetProperty("windows").TryGetProperty("pet", out _));
	}
}
