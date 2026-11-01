using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.Desktop.Appearance;
using Nori.Desktop.Chat;
using Nori.Desktop.Main;

namespace Nori.Desktop.FirstRun;

/// <summary>
/// 使用方法一步：完成向导之前，把「去哪找她、怎么和她说话」讲一遍。
///
/// 用户反馈装好之后不知道怎么用：快捷输入栏、拖动、右键菜单、托盘图标此前都没有
/// 任何地方提过。这里的每一句都对应一处真实交互，改交互时要一起改：
/// 快捷聊天与 <c>WindowManager.OnQuickChatShortcut</c>（Ctrl/⌘+K），
/// 拖动与点按在 <c>PetWindow</c> 的指针处理，右键菜单在 <c>PetWindow.BuildContextMenu</c>，
/// 托盘在 <c>TrayMenu</c>，工具确认来自默认的逐次确认授权档。
/// </summary>
public sealed partial class FirstRunSteps
{
	private const int GuideColumns = 2;

	private static Control BuildGuide(bool english)
	{
		(string Icon, string Title, string Body)[] cards = GuideCards(english,
			OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());
		Grid grid = new()
		{
			Name = "FirstRunGuide",
			Width = 700, ColumnSpacing = 12, RowSpacing = 12,
			ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", GuideColumns))),
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		for (int row = 0; row * GuideColumns < cards.Length; row++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		for (int index = 0; index < cards.Length; index++)
		{
			Control card = GuideCard(cards[index].Icon, cards[index].Title, cards[index].Body);
			Grid.SetRow(card, index / GuideColumns);
			Grid.SetColumn(card, index % GuideColumns);
			grid.Children.Add(card);
		}

		return Stage(
			Heading(english ? "Getting along with Nori" : "怎么和 Nori 相处", NoriMetrics.FontXl),
			Muted(english
				? "A quick look at where to find Nori and how to talk to her."
				: "花半分钟看看去哪找她、怎么和她说话。", 520),
			grid);
	}

	/// <summary>六张卡片的文案。平台差异只体现在快捷键写法和托盘的叫法上。</summary>
	internal static (string Icon, string Title, string Body)[] GuideCards(bool english, bool mac, bool windows)
	{
		string shortcut = mac ? "⌘K" : "Ctrl+K";
		string trayHint = windows
			? english ? " On Windows it may be tucked under ^ on the taskbar." : "Windows 上可能收在任务栏右下角的 ^ 里。"
			: "";
		return english
			?
			[
				("chat", "Chat with her",
					$"Type in the bar beside Nori and press Enter to send. When a Nori window is focused, press {shortcut} to start typing."),
				("move", "Move & touch",
					"Drag Nori anywhere on your screen. Tap her and she'll change expression or move."),
				("mouse", "Right-click menu",
					"Right-click Nori to open the main window, play a random motion, reset her position, hide her or quit."),
				("tray", mac ? "Menu bar icon" : "Tray icon",
					"Open the main window, show or hide Nori, toggle Quick Chat or open Settings from here. Closing the main window keeps Nori running." + trayHint),
				("home", "Main window",
					"Summon or hide Nori. The sidebar opens Chat (full history and voice input), Models, Memory and Settings."),
				("shield", "She asks first",
					"Actions like editing files or controlling your computer show a request first and only run after you click Allow."),
			]
			:
			[
				("chat", "和她聊天",
					$"在伴侣身旁的输入栏打字，按回车发送。Nori 的窗口在前台时，按 {shortcut} 可以直接开始输入。"),
				("move", "拖动与触摸",
					"按住伴侣可以把她拖到屏幕任意位置；轻点她会换个表情或做个动作。"),
				("mouse", "右键菜单",
					"在伴侣身上点右键，可以打开主界面、播放随机动作、重置位置、隐藏或退出。"),
				("tray", mac ? "菜单栏图标" : "托盘图标",
					"在这里打开主界面、显示或隐藏 Nori、开关快捷聊天、进入设置。关掉主界面后 Nori 仍在后台陪你。" + trayHint),
				("home", "主界面",
					"唤出或收起 Nori；侧栏可以打开对话（完整记录和语音输入）、模型、记忆和设置。"),
				("shield", "放心让她帮忙",
					"读写文件、操作电脑这类操作会先弹出请求，你点「允许」后才会执行。"),
			];
	}

	private static Control GuideCard(string icon, string title, string body)
	{
		Border tile = new()
		{
			Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top,
			CornerRadius = new CornerRadius(NoriMetrics.RadiusMd),
			Background = ChatPalette.Overlay, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1),
			Child = MainVisual.Icon(icon, 17, ChatPalette.Teal),
		};
		StackPanel text = new()
		{
			Spacing = 4,
			Children =
			{
				new TextBlock {Text = title, FontSize = NoriMetrics.FontBase, FontWeight = FontWeight.SemiBold, Foreground = ChatPalette.Primary},
				new TextBlock {Text = body, FontSize = NoriMetrics.FontXs, Foreground = ChatPalette.Muted, TextWrapping = TextWrapping.Wrap, LineHeight = 17},
			},
		};
		Grid row = new() {ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12};
		Grid.SetColumn(text, 1);
		row.Children.Add(tile);
		row.Children.Add(text);

		Border card = new()
		{
			Background = ChatPalette.Deep, BorderBrush = ChatPalette.Line, BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(NoriMetrics.RadiusLg), Padding = new Thickness(12),
			Child = row,
		};
		AutomationProperties.SetName(card, $"{title}. {body}");
		return card;
	}
}
