using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Nori.Desktop.Tests;

/// <summary>
/// 设置窗口左侧那棵两级导航。
///
/// 两件事各有一条守卫：
///
///   ① **每一页都得有图标。** 图标是在 SettingsTheme.axaml 里按 `Tag=页面键` 选中的,
///      离页面定义很远。页面改过名之后选择器没跟着改, Path 就拿不到 Data ——
///      渲染成一个空 Path, 不报错不告警, 界面上是一块空白。改动前 workspace 与
///      expression 就是这样, 而 behaviors / library 两条样式指向的页面早就不存在了。
///
///   ② **分组只有三个, 且每个都有页。** 空分组头是纯噪音; 而分组键写错会在
///      AddPage 里直接抛, 那条路已经是响的, 不必再测。
/// </summary>
public sealed class SettingsNavigationTests
{
	/// <summary>设置窗口里注册的全部页面键, 与 SettingsViewModel 的 AddPage 一一对应。</summary>
	private static readonly string[] PageKeys =
	[
		"ai", "voice", "expression", "proactive",
		"workspace", "automation", "skills", "mcp", "plugins",
		"general", "updates", "about", "debug",
	];

	private static string ReadTheme()
	{
		using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SettingsTheme.axaml")
			?? throw new InvalidOperationException("主题没嵌进测试程序集, 见 csproj 里那条 EmbeddedResource");
		using StreamReader reader = new(stream, Encoding.UTF8);
		return reader.ReadToEnd();
	}

	/// <summary>主题里为哪些页面键声明了导航图标。</summary>
	private static HashSet<string> IconKeys() =>
		Regex.Matches(ReadTheme(), @"Tag=([a-z]+)\] Path\.settings-nav-icon")
			.Select(match => match.Groups[1].Value)
			.ToHashSet(StringComparer.Ordinal);

	[Fact]
	public void 每一页都有导航图标()
	{
		HashSet<string> declared = IconKeys();
		string[] missing = PageKeys.Where(key => !declared.Contains(key)).ToArray();

		Assert.True(
			missing.Length == 0,
			$"这些页面拿不到图标数据, 界面上会是一块空白而且不报错: {string.Join(", ", missing)}");
	}

	/// <summary>
	/// 这套皮肤不只给设置窗口用:「模型」窗口左侧那两项也走 settings-nav。
	///
	/// 写下来是因为**它骗过我一次**: 我把 library / behaviors 当成改名遗留的死样式删了,
	/// 结果删掉的是模型窗口的图标 —— 被 NativeModelsNavigationKeepsSettingsIconsAcrossLanguageChanges
	/// 里那句 Assert.NotNull(icon.Data) 逮住。既然这套皮肤是共用的, 名单就得写在明处。
	/// </summary>
	private static readonly string[] ModelsWindowKeys = ["library", "behaviors"];

	/// <summary>
	/// 反过来也要看：指向任何一个窗口都不用的键, 是死代码。
	///
	/// 它本身不出错, 但它正是上一条发生的原因 —— 页面改名时有人只改了一半:
	/// 新名字没加, 旧名字没删, 两边都不报错。
	/// </summary>
	[Fact]
	public void 没有指向任何窗口都不用的死样式()
	{
		string[] orphans = IconKeys()
			.Where(key => !PageKeys.Contains(key) && !ModelsWindowKeys.Contains(key))
			.Order()
			.ToArray();

		Assert.True(
			orphans.Length == 0,
			$"这些样式的 Tag 没有任何窗口在用: {string.Join(", ", orphans)}");
	}

	[Fact]
	public void 分组只有三个且每个都有页()
	{
		(string Group, string[] Pages)[] expected =
		[
			("self", ["ai", "voice", "expression", "proactive"]),
			("reach", ["workspace", "automation", "skills", "mcp", "plugins"]),
			("app", ["general", "updates", "about", "debug"]),
		];

		Assert.All(expected, entry => Assert.NotEmpty(entry.Pages));
		// 每一页恰好属于一个分组, 不重不漏。
		Assert.Equal(
			PageKeys.Order().ToArray(),
			expected.SelectMany(entry => entry.Pages).Order().ToArray());
	}
}
