using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.Core.Automation;
using Nori.Core.Platform;
using Nori.Desktop.Appearance;
using Nori.Desktop.Automation.Windows;

namespace Nori.Desktop.Vision;

/// <summary>覆盖单个物理显示器的窗口选择与扫描效果；输入不会传给下方应用。</summary>
internal sealed class WindowScanOverlay : Window
{
	private readonly PixelRect _screenBounds;
	private readonly Action<PixelPoint> _hover;
	private readonly Action<PixelPoint> _select;
	private readonly Action _cancel;
	private readonly Action<int> _cycle;
	private readonly Action _confirm;
	private readonly ScanCanvas _canvas;
	private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
	private readonly Stopwatch _elapsed = new();
	private WindowsTopLevelWindow? _target;
	private bool _scanning;
	private bool _fried;
	private bool _english;
	private bool _reducedMotion;

	internal WindowScanOverlay(PixelRect screenBounds, Action<PixelPoint> hover, Action<PixelPoint> select,
		Action cancel, Action<int> cycle, Action confirm)
	{
		_screenBounds = screenBounds;
		_hover = hover;
		_select = select;
		_cancel = cancel;
		_cycle = cycle;
		_confirm = confirm;
		Title = "Nori · 窗口扫描";
		Topmost = true;
		WindowDecorations = WindowDecorations.None;
		ShowInTaskbar = false;
		ShowActivated = true;
		CanResize = false;
		Focusable = true;
		Background = AlphaBrush("scan-background", 0);
		TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
		WindowStartupLocation = WindowStartupLocation.Manual;
		Position = screenBounds.Position;
		Width = screenBounds.Width / RenderScaling;
		Height = screenBounds.Height / RenderScaling;
		_canvas = new ScanCanvas(this);
		Content = _canvas;
		_canvas.Refresh();

		AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
		AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
		AddHandler(PointerReleasedEvent, (_, args) => args.Handled = true, RoutingStrategies.Tunnel);
		AddHandler(PointerWheelChangedEvent, (_, args) => args.Handled = true, RoutingStrategies.Tunnel);
		AddHandler(KeyDownEvent, OnOverlayKeyDown, RoutingStrategies.Tunnel);
		ScalingChanged += (_, _) => ApplyScreenBounds();
		Opened += (_, _) =>
		{
			_reducedMotion = PlatformServices.Current.PrefersReducedMotion;
			ApplyScreenBounds();
			_elapsed.Restart();
			_timer.Start();
			Focus();
		};
		_timer.Tick += (_, _) =>
		{
			if (_scanning && _target is not null && !_reducedMotion) _canvas.InvalidateVisual();
		};
		Closed += (_, _) => { _timer.Stop(); _elapsed.Stop(); };
	}

	internal bool English
	{
		get => _english;
		set { if (_english == value) return; _english = value; _canvas.Refresh(); }
	}

	internal void SetTarget(WindowsTopLevelWindow? target, bool scanning, bool fried = false)
	{
		if (_target == target && _scanning == scanning && _fried == fried) return;
		if (scanning && !_scanning) _elapsed.Restart();
		_target = target;
		_scanning = scanning;
		_fried = fried;
		_canvas.Refresh();
	}

	private void ApplyScreenBounds()
	{
		// 打开后使用本窗口所在显示器的缩放，不借用启动窗口或目标应用的 DPI。
		Width = _screenBounds.Width / RenderScaling;
		Height = _screenBounds.Height / RenderScaling;
		Position = _screenBounds.Position;
		_canvas.InvalidateMeasure();
		_canvas.InvalidateVisual();
	}

	private static Rect ToLocalBounds(AutomationBounds bounds, PixelPoint origin, double scaling) => new(
		(bounds.Left - (double)origin.X) / scaling, (bounds.Top - (double)origin.Y) / scaling,
		bounds.Width / scaling, bounds.Height / scaling);

	private void OnPointerMoved(object? sender, PointerEventArgs args)
	{
		args.Handled = true;
		_hover(this.PointToScreen(args.GetPosition(this)));
	}

	private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
	{
		args.Handled = true;
		if (args.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
			_select(this.PointToScreen(args.GetPosition(this)));
	}

	private void OnOverlayKeyDown(object? sender, KeyEventArgs args)
	{
		switch (args.Key)
		{
			case Key.Escape: args.Handled = true; _cancel(); break;
			case Key.Tab: args.Handled = true; _cycle(args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); break;
			case Key.Enter: args.Handled = true; _confirm(); break;
		}
	}

	private static Color AlphaColor(string token, byte alpha)
	{
		Color color = NoriThemeTokens.Color(token);
		return Color.FromArgb(alpha, color.R, color.G, color.B);
	}

	private static SolidColorBrush AlphaBrush(string token, byte alpha) => new(AlphaColor(token, alpha));

	private sealed class ScanCanvas : Control
	{
		private readonly WindowScanOverlay _owner;
		private readonly TextBlock _title = new()
		{
			Name = "WindowScanTarget", FontFamily = NoriTypography.Scan, FontSize = 12,
			TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
		};
		private readonly TextBlock _hint = new()
		{
			Name = "WindowScanHint", FontFamily = NoriTypography.Scan, FontSize = 12,
			TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
		};
		private readonly Border _titleTag;
		private readonly Border _hintTag;
		private readonly IBrush _shade = AlphaBrush("scan-background", 153);
		private IBrush _accent = NoriThemeTokens.Brush("scan-accent");
		private IBrush _tint = AlphaBrush("scan-accent", 8);
		private Pen _outline = new(NoriThemeTokens.Brush("scan-accent"), 2);
		private BoxShadows _glow;
		private BoxShadows _lineGlow;

		internal ScanCanvas(WindowScanOverlay owner)
		{
			_owner = owner;
			ClipToBounds = true;
			_titleTag = new Border
			{
				Background = AlphaBrush("scan-background", 230), CornerRadius = new CornerRadius(3),
				Padding = new Thickness(8, 4), Child = _title, IsHitTestVisible = false,
			};
			_hintTag = new Border
			{
				Background = AlphaBrush("scan-background", 235), CornerRadius = new CornerRadius(5),
				BorderBrush = NoriThemeTokens.Brush("scan-border"), BorderThickness = new Thickness(1),
				Padding = new Thickness(18, 10), Child = _hint, IsHitTestVisible = false,
			};
			VisualChildren.Add(_titleTag);
			LogicalChildren.Add(_titleTag);
			VisualChildren.Add(_hintTag);
			LogicalChildren.Add(_hintTag);
			AutomationProperties.SetLiveSetting(_title, AutomationLiveSetting.Polite);
			AutomationProperties.SetLiveSetting(_hint, AutomationLiveSetting.Polite);
		}

		private Rect? TargetRect => _owner._target is { } target
			? ToLocalBounds(target.Bounds, _owner.Position, _owner.RenderScaling) : null;

		internal void Refresh()
		{
			string token = _owner._fried ? "scan-danger" : "scan-accent";
			_accent = NoriThemeTokens.Brush(token);
			_tint = AlphaBrush(token, _owner._fried ? (byte)24 : (byte)8);
			_outline = new Pen(_accent, 2);
			_glow = new BoxShadows(new BoxShadow { Blur = 32, Color = AlphaColor(token, 107) });
			_lineGlow = new BoxShadows(new BoxShadow { Blur = 20, Spread = 5, Color = AlphaColor(token, 80) });
			_title.Text = _owner._target?.Title.ReplaceLineEndings(" ") ?? "";
			_title.Foreground = _accent;
			_hint.Foreground = _accent;
			_hint.Text = _owner._scanning
				? (_owner.English ? "Scanning..." : "扫描中...")
				: (_owner.English ? "Choose a window to send its screenshot to AI for analysis · Esc" : "选择窗口，将其截图发送给 AI 分析 · Esc");
			_owner.Title = _owner.English ? "Nori · Window scan" : "Nori · 窗口扫描";
			AutomationProperties.SetName(_owner, string.IsNullOrEmpty(_title.Text) ? _owner.Title : $"{_owner.Title} · {_title.Text}");
			AutomationProperties.SetHelpText(_owner, _owner.English
				? "Tab / Shift+Tab: choose a window. Enter: scan. Escape: cancel."
				: "Tab / Shift+Tab 切换窗口，Enter 扫描，Esc 取消。");
			AutomationProperties.SetName(_title, _title.Text);
			AutomationProperties.SetName(_hint, _hint.Text);
			InvalidateMeasure();
			InvalidateVisual();
		}

		protected override Size MeasureOverride(Size availableSize)
		{
			_titleTag.Measure(availableSize);
			_hintTag.Measure(availableSize);
			return default;
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			Rect visible = TargetRect?.Intersect(new Rect(finalSize)) ?? default;
			_titleTag.IsVisible = visible.Width > 0 && visible.Height > 0;
			if (_titleTag.IsVisible)
			{
				// 窗口跨屏或部分越界时，标题仍从当前屏幕可见的区域开始显示。
				double inset = Math.Min(10, visible.Width / 4);
				_titleTag.Measure(new Size(Math.Max(0, visible.Width - inset * 2), finalSize.Height));
				Size size = _titleTag.DesiredSize;
				double x = Math.Clamp(visible.X + inset, 0, Math.Max(0, finalSize.Width - size.Width));
				double y = Math.Clamp(visible.Y + 8, 0, Math.Max(0, finalSize.Height - size.Height));
				_titleTag.Arrange(new Rect(new Point(x, y), size));
			}
			_hintTag.Measure(new Size(Math.Max(0, finalSize.Width - 32), finalSize.Height));
			Size hintSize = _hintTag.DesiredSize;
			_hintTag.Arrange(new Rect(new Point(Math.Max(0, (finalSize.Width - hintSize.Width) / 2),
				Math.Max(0, finalSize.Height - 100 - hintSize.Height)), hintSize));
			return finalSize;
		}

		public override void Render(DrawingContext context)
		{
			base.Render(context);
			Rect viewport = new(Bounds.Size);
			if (TargetRect is not { } target || !target.Intersects(viewport))
			{
				context.FillRectangle(_shade, viewport);
				return;
			}
			RectangleGeometry hole = new(target, 12, 12);
			context.DrawGeometry(_shade, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(viewport), hole));
			context.DrawRectangle(_tint, _outline, target, 12, 12, _glow);
			if (!_owner._scanning) return;
			double progress = _owner._reducedMotion ? 0.5 : _owner._elapsed.Elapsed.TotalMilliseconds % 1400 / 1400;
			using (context.PushGeometryClip(hole))
				context.DrawRectangle(_accent, null, new Rect(target.X, target.Y + target.Height * progress - 1, target.Width, 2),
					0, 0, _lineGlow);
		}
	}
}
