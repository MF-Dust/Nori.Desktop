using Nori.Desktop.Tray;

namespace Nori.Desktop.Tests;

/// <summary>
/// 托盘菜单的标题。
///
/// 托盘是这个应用唯一常驻的入口 —— 主窗口关掉之后, 用户只剩这四条。所以这四条
/// 必须说人话, 而改动前它们有三处不是:
///
///   ① 「显示/隐藏 Nori」把两个互斥的结果并排摆着, 点之前不知道会得到哪一个;
///   ② 四条里只有「设置」跟着语言走, 英文用户看到的是三句中文夹一句英文;
///   ③ 语言在启动时读一次就定死了, 之后改设置不生效。
///
/// 这一族钉的是 ① 和 ②。③ 由 Refresh 承担, 它是调用点问题, 在 BridgeCommands 那边。
/// </summary>
public sealed class TrayMenuLabelTests
{
	[Theory]
	[InlineData(true, false, "隐藏 Nori")]
	[InlineData(false, false, "显示 Nori")]
	[InlineData(true, true, "Hide Nori")]
	[InlineData(false, true, "Show Nori")]
	public void 切换那条写的是这次点下去会发生什么(bool petVisible, bool english, string expected) =>
		Assert.Equal(expected, TrayMenu.ToggleLabel(petVisible, english));

	/// <summary>
	/// 一条菜单项里不能同时出现两个互斥的动作。
	///
	/// 这是原来那句「显示/隐藏 Nori」的毛病: 它读起来像分类名, 而菜单项是动词。
	/// </summary>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void 切换那条不会同时出现显示和隐藏(bool petVisible)
	{
		string chinese = TrayMenu.ToggleLabel(petVisible, english: false);
		Assert.False(chinese.Contains('显') && chinese.Contains('隐'), chinese);
		Assert.DoesNotContain("/", chinese, StringComparison.Ordinal);

		string english = TrayMenu.ToggleLabel(petVisible, english: true);
		Assert.False(
			english.Contains("Show", StringComparison.Ordinal)
			&& english.Contains("Hide", StringComparison.Ordinal),
			english);
	}

	/// <summary>
	/// 每一条都要跟着语言走。
	///
	/// 改动前只有「设置」做了这件事, 于是英文界面的托盘长这样:
	/// 打开主界面 / Settings / 显示/隐藏 Nori / 退出应用。
	/// </summary>
	[Fact]
	public void 四条标题都跟着语言变()
	{
		(string Chinese, string English)[] labels =
		[
			(TrayMenu.ToggleLabel(true, false), TrayMenu.ToggleLabel(true, true)),
			(TrayMenu.MainLabel(false), TrayMenu.MainLabel(true)),
			(TrayMenu.SettingsLabel(false), TrayMenu.SettingsLabel(true)),
			(TrayMenu.QuitLabel(false), TrayMenu.QuitLabel(true)),
			(TrayMenu.Tooltip(false), TrayMenu.Tooltip(true)),
		];

		foreach ((string chinese, string english) in labels)
		{
			Assert.NotEqual(chinese, english);
			Assert.NotEmpty(chinese);
			Assert.NotEmpty(english);
			// 英文那份不该混汉字 —— 混着就是漏翻。("Nori" 是名字, 两边都出现是对的。)
			Assert.All(english.ToCharArray(), character => Assert.True(character < 0x4E00 || character > 0x9FFF, english));
		}
	}

	/// <summary>退出要说清楚退的是谁: 托盘上常常还蹲着别的程序。</summary>
	[Fact]
	public void 退出那条带上名字()
	{
		Assert.Contains("Nori", TrayMenu.QuitLabel(false), StringComparison.Ordinal);
		Assert.Contains("Nori", TrayMenu.QuitLabel(true), StringComparison.Ordinal);
	}
}
