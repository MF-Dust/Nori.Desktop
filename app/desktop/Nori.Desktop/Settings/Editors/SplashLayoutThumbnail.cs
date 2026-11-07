using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.Desktop.Appearance;
using Nori.Desktop.Chat;

namespace Nori.Desktop.Settings;

/// <summary>启动画面布局示意。只用色块，不加载图片素材。</summary>
internal sealed class SplashLayoutThumbnail : Border
{
	private readonly Schematic _schematic;

	/// <summary>按布局 id 创建 16:10 缩略图。</summary>
	public SplashLayoutThumbnail(string theme)
	{
		Background = NoriThemeTokens.Brush("bg-base");
		BorderBrush = NoriThemeTokens.Brush("line-strong");
		BorderThickness = new Thickness(1);
		CornerRadius = new CornerRadius(NoriMetrics.RadiusSm);
		ClipToBounds = true;
		IsHitTestVisible = false;
		HorizontalAlignment = HorizontalAlignment.Stretch;
		VerticalAlignment = VerticalAlignment.Top;
		Child = _schematic = Schematic.Create(theme);
	}

	/// <inheritdoc />
	protected override Size MeasureOverride(Size availableSize)
	{
		double width = double.IsFinite(availableSize.Width) && availableSize.Width > 1 ? availableSize.Width : 160;
		double height = width * 10d / 16d;
		double innerWidth = Math.Max(0, width - BorderThickness.Left - BorderThickness.Right);
		double innerHeight = Math.Max(0, height - BorderThickness.Top - BorderThickness.Bottom);
		_schematic.Measure(new Size(innerWidth, innerHeight));
		return new Size(width, height);
	}

	private static void Place(Control child, double x, double y, double width, double height)
	{
		Canvas.SetLeft(child, x);
		Canvas.SetTop(child, y);
		child.Width = Math.Max(0, width);
		child.Height = Math.Max(0, height);
	}

	private static Border Bar(IBrush fill) => new()
	{
		Background = fill,
		CornerRadius = new CornerRadius(NoriMetrics.RadiusXs),
		IsHitTestVisible = false,
	};

	private static Ellipse Dot(IBrush fill) => new()
	{
		Fill = fill,
		IsHitTestVisible = false,
	};

	private sealed class Schematic : Canvas
	{
		private readonly Action<Rect> _apply;

		private Schematic(Action<Rect> apply)
		{
			_apply = apply;
			IsHitTestVisible = false;
		}

		public static Schematic Create(string theme) => theme switch
		{
			"minimal" => Standard(false),
			"android-studio" => Android(),
			"microsoft-365" => Microsoft365(),
			"office-legacy" => Office(),
			"island" => Island(),
			"alls" => Alls(),
			_ => Standard(true),
		};

		protected override Size ArrangeOverride(Size finalSize)
		{
			if (finalSize.Width > 0 && finalSize.Height > 0)
				_apply(new Rect(finalSize));
			return base.ArrangeOverride(finalSize);
		}

		private static Schematic Host(Action<Rect> apply, params Control[] children)
		{
			Schematic canvas = new(apply);
			foreach (Control child in children) canvas.Children.Add(child);
			return canvas;
		}

		/// <summary>Photoshop：左信息列、竖分隔线、右圆角作品区。简约只有信息列。</summary>
		private static Schematic Standard(bool artwork)
		{
			IBrush muted = NoriThemeTokens.Brush("text-muted");
			IBrush faint = NoriThemeTokens.Brush("text-faint");
			Ellipse logo = Dot(ChatPalette.Teal);
			Border title = Bar(muted);
			Border line1 = Bar(faint);
			Border line2 = Bar(faint);
			Border progress = Bar(ChatPalette.Teal);
			Border? divider = artwork ? Bar(NoriThemeTokens.Brush("line-strong")) : null;
			Border? art = artwork
				? new Border { Background = NoriThemeTokens.Brush("bg-deep"), IsHitTestVisible = false }
				: null;
			List<Control> children = [logo, title, line1, line2, progress];
			if (art is not null) children.Add(art);
			if (divider is not null) children.Add(divider);
			return Host(rect =>
			{
				double w = rect.Width;
				double h = rect.Height;
				double split = artwork ? w * 0.42 : w;
				double inset = w * 0.06;
				double dot = Math.Max(6, h * 0.11);
				Place(logo, inset, h * 0.12, dot, dot);
				Place(title, inset + dot + w * 0.02, h * 0.12 + dot * 0.28, Math.Min(w * 0.18, Math.Max(0, split - inset - dot - w * 0.08)), Math.Max(2, h * 0.045));
				Place(line1, inset, h * 0.12 + dot + h * 0.06, artwork ? w * 0.26 : w * 0.55, Math.Max(2, h * 0.03));
				Place(line2, inset, h * 0.12 + dot + h * 0.12, artwork ? w * 0.2 : w * 0.4, Math.Max(2, h * 0.03));
				Place(progress, inset, h * 0.82, artwork ? w * 0.24 : w * 0.42, Math.Max(2, h * 0.035));
				if (divider is not null) Place(divider, split, h * 0.08, 1, h * 0.84);
				if (art is null) return;
				double x = split + w * 0.05;
				Place(art, x, h * 0.1, Math.Max(0, w - x - w * 0.06), h * 0.8);
				art.CornerRadius = new CornerRadius(Math.Max(NoriMetrics.RadiusSm, h * 0.08));
			}, children.ToArray());
		}

		/// <summary>左 3/5 作品区，右侧标志与阶段文字，底部通栏进度。</summary>
		private static Schematic Android()
		{
			Border art = new() { Background = NoriThemeTokens.Brush("bg-deep"), IsHitTestVisible = false };
			Ellipse logo = Dot(ChatPalette.Teal);
			Border title = Bar(NoriThemeTokens.Brush("text-muted"));
			Border stage = Bar(NoriThemeTokens.Brush("text-faint"));
			Border progress = Bar(ChatPalette.Teal);
			return Host(rect =>
			{
				double w = rect.Width;
				double h = rect.Height;
				double bar = Math.Max(4, h * 0.08);
				Place(art, 0, 0, w * 0.6, Math.Max(0, h - bar));
				double dot = Math.Max(8, h * 0.16);
				double x = w * 0.68;
				Place(logo, x, h * 0.14, dot, dot);
				Place(title, x, h * 0.14 + dot + h * 0.05, w * 0.24, Math.Max(2, h * 0.045));
				Place(stage, x, h * 0.14 + dot + h * 0.13, w * 0.18, Math.Max(2, h * 0.03));
				double thickness = Math.Max(2, h * 0.03);
				Place(progress, w * 0.06, h - bar + (bar - thickness) / 2, w * 0.88, thickness);
			}, art, logo, title, stage, progress);
		}

		/// <summary>居中小窗：左上标签、居中大标志、底部进度。</summary>
		private static Schematic Microsoft365()
		{
			Border window = new()
			{
				Background = NoriThemeTokens.Brush("bg-panel"),
				BorderBrush = NoriThemeTokens.Brush("line-strong"),
				BorderThickness = new Thickness(1),
				IsHitTestVisible = false,
			};
			Border label = Bar(NoriThemeTokens.Brush("text-muted"));
			Ellipse logo = Dot(ChatPalette.Teal);
			Border progress = Bar(ChatPalette.Teal);
			return Host(rect =>
			{
				double w = rect.Width;
				double h = rect.Height;
				double ww = w * 0.72;
				double wh = h * 0.74;
				double x = (w - ww) / 2;
				double y = (h - wh) / 2;
				Place(window, x, y, ww, wh);
				window.CornerRadius = new CornerRadius(Math.Max(NoriMetrics.RadiusSm, h * 0.06));
				Place(label, x + ww * 0.08, y + wh * 0.1, ww * 0.28, Math.Max(3, wh * 0.08));
				double dot = wh * 0.3;
				Place(logo, x + (ww - dot) / 2, y + (wh - dot) / 2 - wh * 0.04, dot, dot);
				Place(progress, x + ww * 0.12, y + wh * 0.82, ww * 0.52, Math.Max(2, wh * 0.045));
			}, window, label, logo, progress);
		}

		/// <summary>顶部小字，中部标志与标题，底部进度。</summary>
		private static Schematic Office()
		{
			Border kicker = Bar(NoriThemeTokens.Brush("text-faint"));
			Ellipse logo = Dot(ChatPalette.Teal);
			Border title = Bar(NoriThemeTokens.Brush("text-muted"));
			Border progress = Bar(ChatPalette.Teal);
			return Host(rect =>
			{
				double w = rect.Width;
				double h = rect.Height;
				Place(kicker, w * 0.08, h * 0.12, w * 0.36, Math.Max(2, h * 0.035));
				double dot = Math.Max(8, h * 0.18);
				double titleWidth = w * 0.28;
				double titleHeight = Math.Max(3, h * 0.05);
				double x = (w - (dot + w * 0.03 + titleWidth)) / 2;
				double row = h * 0.4;
				Place(logo, x, row, dot, dot);
				Place(title, x + dot + w * 0.03, row + (dot - titleHeight) / 2, titleWidth, titleHeight);
				Place(progress, w * 0.08, h * 0.82, w * 0.5, Math.Max(2, h * 0.035));
			}, kicker, logo, title, progress);
		}

		/// <summary>顶部居中的胶囊条，其余留空。</summary>
		private static Schematic Island()
		{
			Border capsule = new()
			{
				Background = NoriThemeTokens.Brush("bg-panel"),
				BorderBrush = NoriThemeTokens.Brush("line-strong"),
				BorderThickness = new Thickness(1),
				IsHitTestVisible = false,
			};
			Ellipse logo = Dot(ChatPalette.Teal);
			Border text = Bar(NoriThemeTokens.Brush("text-muted"));
			Border progress = Bar(ChatPalette.Teal);
			return Host(rect =>
			{
				double w = rect.Width;
				double h = rect.Height;
				double capWidth = w * 0.78;
				double capHeight = Math.Max(12, h * 0.26);
				double x = (w - capWidth) / 2;
				double y = h * 0.08;
				Place(capsule, x, y, capWidth, capHeight);
				capsule.CornerRadius = new CornerRadius(capHeight / 2);
				double dot = capHeight * 0.36;
				Place(logo, x + capHeight * 0.28, y + capHeight * 0.16, dot, dot);
				Place(text, x + capHeight * 0.28 + dot + w * 0.02, y + capHeight * 0.26, capWidth * 0.42, Math.Max(2, capHeight * 0.16));
				Place(progress, x + capWidth * 0.1, y + capHeight * 0.7, capWidth * 0.8, Math.Max(1.5, capHeight * 0.08));
			}, capsule, logo, text, progress);
		}

		/// <summary>全幅居中：大标志、标题、版本小字和进度。</summary>
		private static Schematic Alls()
		{
			Ellipse logo = Dot(ChatPalette.Teal);
			Border title = Bar(NoriThemeTokens.Brush("text-muted"));
			Border version = Bar(NoriThemeTokens.Brush("text-faint"));
			Border progress = Bar(ChatPalette.Teal);
			return Host(rect =>
			{
				double w = rect.Width;
				double h = rect.Height;
				double dot = Math.Max(10, h * 0.22);
				double titleWidth = w * 0.24;
				double titleHeight = Math.Max(3, h * 0.06);
				double x = (w - (dot + w * 0.03 + titleWidth)) / 2;
				double row = h * 0.22;
				Place(logo, x, row, dot, dot);
				Place(title, x + dot + w * 0.03, row + (dot - titleHeight) / 2, titleWidth, titleHeight);
				Place(version, (w - w * 0.18) / 2, row + dot + h * 0.08, w * 0.18, Math.Max(2, h * 0.03));
				Place(progress, (w - w * 0.42) / 2, row + dot + h * 0.16, w * 0.42, Math.Max(2, h * 0.035));
			}, logo, title, version, progress);
		}
	}
}
