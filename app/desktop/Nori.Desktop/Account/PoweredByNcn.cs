using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Nori.Desktop.Chat;

namespace Nori.Desktop.Account;

/// <summary>
/// 「▲ Nyco Cloud Network」标识。
///
/// ── 挂在哪里 ──────────────────────────────────────────────────────────────
/// 只挂在**要联网、要账户**的地方：登录窗、条款确认、云端同步窗、以及设置里的账户页。
/// 本机功能不挂 —— 那些不经过 NCN，挂上去等于声称一件不成立的事。
///
/// ── 为什么是一个控件而不是各处拼一遍 ──────────────────────────────────────
/// 三扇窗各拼一次的话，字号、间距、透明度会各自漂移，而且改品牌名要翻三个文件。
/// 图片也只解码一次（静态字段）。
/// </summary>
internal static class PoweredByNcn
{
	/// <summary>品牌名。改这里等于改全部出现的地方。</summary>
	internal const string Brand = "Nyco Cloud Network";

	/// <summary>
	/// 标识图。
	///
	/// 只解码一次并共享同一个 <see cref="Bitmap"/> —— 三扇窗各自解一遍 64KB 的 PNG
	/// 没有意义。Avalonia 允许同一个位图被多个 Image 引用。
	/// </summary>
	private static Bitmap? _mark;

	private static Bitmap Mark => _mark ??= new Bitmap(AssetLoader.Open(
		new Uri("avares://Nori.Desktop/Assets/ncn-logo.png")));

	/// <summary>
	/// 造一条标识。
	///
	/// <paramref name="align">决定它靠哪边。窗口底部居中，设置类的面板里左对齐。</paramref>
	/// </summary>
	internal static Control Build(HorizontalAlignment align = HorizontalAlignment.Center)
	{
		Image mark = new()
		{
			Source = Mark,
			Height = 16,
			// 等比缩放。标识被拉扁比不放标识更糟。
			Stretch = Stretch.Uniform,
			VerticalAlignment = VerticalAlignment.Center,
		};

		TextBlock name = new()
		{
			Text = Brand,
			FontSize = 11,
			FontWeight = FontWeight.Medium,
			// 低于正文的对比度：归属标识不参与主要信息层级。
			Foreground = ChatPalette.Muted,
			VerticalAlignment = VerticalAlignment.Center,
		};

		return new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 7,
			HorizontalAlignment = align,
			Opacity = 0.9,
			Children = {mark, name},
		};
	}
}
