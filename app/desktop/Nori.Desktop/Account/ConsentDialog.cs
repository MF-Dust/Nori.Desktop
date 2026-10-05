using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Nori.Core.Cloud;
using Nori.Core.Platform;
using Nori.Desktop.Chat;
using Nori.Desktop.Runtime;

namespace Nori.Desktop.Account;

/// <summary>
/// 条款确认。
///
/// ── 为什么不把正文放进来 ──────────────────────────────────────────────────
/// 五份文档合计近 80 KB，还是双语排版。塞进一个 500px 的窗口，结果是一个没有人会读
/// 的滚动区 —— 而「同意」这个动作的前提恰恰是能读。所以这里只列标题与版本，正文交给
/// 系统浏览器打开服务端那份（<c>/legal/&lt;key&gt;</c>，公开可访问）。
///
/// ── 为什么没有「全部已读」的勾选框 ────────────────────────────────────────
/// 勾选框声称自己在核验「读过了」，而它核验不了任何东西。一个明确写着做什么的按钮
/// 是同样的法律效力，且不假装。
/// </summary>
internal sealed class ConsentDialog : Window
{
	private readonly TaskCompletionSource<bool> _answer = new();

	private ConsentDialog(ConsentRequest request)
	{
		Title = "条款确认";
		Width = 520;
		SizeToContent = SizeToContent.Height;
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		RequestedThemeVariant = ThemeVariant.Dark;
		Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/"))
		{
			Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml"),
		});
		Background = ChatPalette.Background;
		WindowDecorations = PlatformServices.Current.Capabilities.SupportsWindowDrag
			? WindowDecorations.None
			: WindowDecorations.Full;

		Grid root = new() {RowDefinitions = new RowDefinitions("Auto,*")};
		Control chrome = BuildChrome();
		Control body = BuildBody(request);
		root.Children.Add(chrome);
		root.Children.Add(body);
		Grid.SetRow(chrome, 0);
		Grid.SetRow(body, 1);
		Content = root;

		KeyDown += (_, args) =>
		{
			if (args.Key != Key.Escape) return;
			args.Handled = true;
			Answer(false);
		};
		// 直接点关闭等同于不同意。默认值必须是「不同意」—— 关窗不能被读成同意。
		Closed += (_, _) => _answer.TrySetResult(false);
	}

	/// <summary>视觉测试用：只造出来看样子，不进入模态等待。</summary>
	internal static ConsentDialog ForTests(ConsentRequest request) => new(request);

	/// <summary>弹出并等待答复。返回 true 表示用户明确同意。</summary>
	internal static Task<bool> AskAsync(Window owner, ConsentRequest request)
	{
		ConsentDialog dialog = new(request);
		// 不等 ShowDialog 的 Task：它在窗口关闭时完成，而答复由 _answer 给出。
		// 两个都等会多一次往返，且顺序不保证。
		_ = dialog.ShowDialog(owner);
		return dialog._answer.Task;
	}

	/// <summary>
	/// 顶上的一条。
	///
	/// 摘掉系统边框之后，这扇窗原来既没有标题也拖不动 —— 它是模态的，人只能在原地
	/// 读完五份文档的标题，挪不开去看后面的东西。
	///
	/// 只有标题，**没有 ✕**：关掉这扇窗等于「不同意」，而那句话已经由「暂不登录」
	/// 那个按钮明确说了。再放一个没有标签的 ✕，等于给同一个决定两个入口，其中一个
	/// 不说明自己做什么。
	/// </summary>
	private Control BuildChrome()
	{
		Border bar = new()
		{
			Height = 36,
			Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Panel,
			BorderThickness = new Thickness(0, 0, 0, 1),
			Padding = new Thickness(20, 0, 0, 0),
			Child = new TextBlock
			{
				Text = "条款确认",
				FontSize = 12,
				FontWeight = FontWeight.SemiBold,
				Foreground = ChatPalette.Accent,
				VerticalAlignment = VerticalAlignment.Center,
			},
		};
		bar.PointerPressed += (_, args) =>
		{
			if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(args);
		};
		return bar;
	}

	private Control BuildBody(ConsentRequest request)
	{
		StackPanel body = new() {Spacing = 14, Margin = new Thickness(24, 20, 24, 20)};

		body.Children.Add(new TextBlock
		{
			Text = request.IsRenewal ? "条款有更新" : "使用云端服务前请先确认条款",
			FontSize = 16, FontWeight = FontWeight.SemiBold,
			Foreground = ChatPalette.Primary,
		});

		body.Children.Add(new TextBlock
		{
			// 两种情形下人处在完全不同的位置：一个是第一次来，一个是用了很久突然被拦下。
			Text = request.IsRenewal
				? "以下文档已更新。继续登录即表示您确认其当前版本。"
				: "以下文档构成您与我们之间的协议。继续登录即表示您确认其内容。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13, LineHeight = 21,
			Foreground = ChatPalette.Body,
		});

		StackPanel list = new() {Spacing = 1};
		foreach (LegalDocument document in request.Documents)
		{
			list.Children.Add(BuildRow(document, request.UrlOf(document.Key)));
		}
		body.Children.Add(new Border
		{
			Background = ChatPalette.Deep,
			BorderBrush = ChatPalette.Panel,
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(6),
			Padding = new Thickness(1),
			Child = list,
		});

		body.Children.Add(new TextBlock
		{
			Text = "文档将在系统浏览器中打开。",
			FontSize = 12,
			Foreground = ChatPalette.Faint,
		});

		Button agree = new()
		{
			// 按钮写按下之后发生什么。「确定」在这里说明不了任何事。
			Content = "我已阅读并同意",
			Padding = new Thickness(18, 9),
			Background = ChatPalette.Teal,
			Foreground = ChatPalette.OnTeal,
			BorderThickness = default,
			CornerRadius = new CornerRadius(6),
			FontSize = 13, FontWeight = FontWeight.SemiBold,
			Cursor = new Cursor(StandardCursorType.Hand),
		};
		agree.Click += (_, _) => Answer(true);

		Button cancel = new()
		{
			Content = "暂不登录",
			Padding = new Thickness(14, 9),
			Background = Brushes.Transparent,
			Foreground = ChatPalette.Muted,
			BorderBrush = ChatPalette.Panel,
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(6),
			FontSize = 13,
			Cursor = new Cursor(StandardCursorType.Hand),
		};
		cancel.Click += (_, _) => Answer(false);

		body.Children.Add(new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Spacing = 10,
			Children = {cancel, agree},
		});

		// 这些条款是谁的条款，要在同意按钮旁边说清楚。
		Control badge = PoweredByNcn.Build(HorizontalAlignment.Left);
		badge.Margin = new Thickness(0, 2, 0, 0);
		body.Children.Add(badge);

		return body;
	}

	/// <summary>一份文档一行：标题与版本在左，打开在右。</summary>
	private static Control BuildRow(LegalDocument document, string url)
	{
		TextBlock title = new()
		{
			Text = document.Title.Length > 0 ? document.Title : document.Key,
			FontSize = 13,
			Foreground = ChatPalette.Body,
			VerticalAlignment = VerticalAlignment.Center,
			TextWrapping = TextWrapping.NoWrap,
			TextTrimming = TextTrimming.CharacterEllipsis,
		};

		TextBlock version = new()
		{
			// 版本要显示：同意的是哪一版，事后只有这个能对得上。
			Text = document.Version.Length > 0 ? "v" + document.Version : "",
			FontSize = 12,
			Foreground = ChatPalette.Faint,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(10, 0, 0, 0),
		};

		Button open = new()
		{
			Content = "打开",
			Padding = new Thickness(12, 5),
			Background = Brushes.Transparent,
			Foreground = ChatPalette.Accent,
			BorderThickness = default,
			FontSize = 12,
			Cursor = new Cursor(StandardCursorType.Hand),
			VerticalAlignment = VerticalAlignment.Center,
		};
		// 打不开浏览器不该让这扇窗崩掉 —— 用户仍然可以在别处读到这些文档。
		open.Click += (_, _) => { try { ShellOpen.OpenUrl(url); } catch (Exception) { /* 见上 */ } };

		Grid row = new()
		{
			ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
			Background = ChatPalette.Background,
			Margin = new Thickness(0),
			Height = 40,
		};
		row.Children.Add(title);
		row.Children.Add(version);
		row.Children.Add(open);
		Grid.SetColumn(title, 0);
		Grid.SetColumn(version, 1);
		Grid.SetColumn(open, 2);
		title.Margin = new Thickness(14, 0, 0, 0);
		open.Margin = new Thickness(6, 0, 8, 0);
		return row;
	}

	private void Answer(bool agreed)
	{
		_answer.TrySetResult(agreed);
		Close();
	}
}
