using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Core.Platform;
using Nori.Desktop.Appearance;

namespace Nori.Desktop.Windows;

/// <summary>
/// 所有普通窗口共用的自绘标题栏；外观对齐 Nori.Web 的窗口外壳 (36px 透明标题栏、居中标题、交通灯)。
/// 关闭始终经过窗口原有的保存与隐藏流程。
/// </summary>
internal sealed class NativeWindowChrome : Border
{
	/// <summary>web 标题栏高度 (h-9)。</summary>
	internal const double TitleBarHeight = 36;

	private static readonly ConditionalWeakTable<Window, object> ResizeWindows = new();
	private readonly Window _window;
	private readonly Func<bool> _english;
	private readonly bool _closeOnly;
	private readonly TextBlock? _title;
	private readonly Button _close;
	private readonly Button _minimize;
	private readonly Button _zoom;

	internal NativeWindowChrome(Window window, Func<bool>? english = null, Control? heading = null, Action? onClose = null, bool closeOnly = false)
	{
		_window = window;
		_closeOnly = closeOnly;
		_english = english ?? (() => UiLanguage.IsEnglish(System.Globalization.CultureInfo.CurrentUICulture.Name));
		Name = "NativeWindowTitleBar";
		Height = TitleBarHeight;
		// web：标题栏与窗口同为玻璃，只以冰青发丝线与正文分隔。
		Background = Brushes.Transparent;
		BorderBrush = NoriThemeTokens.Brush("line-subtle");
		BorderThickness = new Thickness(0, 0, 0, 1);
		Padding = new Thickness(12, 0, 12, 0);
		_close = TrafficButton("close");
		_minimize = TrafficButton("minimize");
		_zoom = TrafficButton("zoom");
		_close.Click += (_, _) => { if (onClose is null) window.Close(); else onClose(); };
		_minimize.Click += (_, _) => { if (window.CanMinimize) window.WindowState = WindowState.Minimized; };
		_zoom.Click += (_, _) => ToggleMaximized(window);
		StackPanel lights = new() { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center, Children = { _close, _minimize, _zoom } };
		Grid layout = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
		layout.Children.Add(lights);
		if (heading is null)
		{
			// 标题绝对居中且最多占 60% 宽度，不随交通灯数量偏移。
			_title = new TextBlock
			{
				FontSize = NoriMetrics.FontBase, FontWeight = FontWeight.SemiBold, Foreground = NoriThemeTokens.Brush("text-body"),
				TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
				IsHitTestVisible = false,
			};
			Grid.SetColumnSpan(_title, 2);
			layout.Children.Add(_title);
			layout.SizeChanged += (_, args) => _title.MaxWidth = Math.Max(0, args.NewSize.Width * 0.6);
		}
		else
		{
			Grid.SetColumn(heading, 1);
			layout.Children.Add(heading);
		}
		Child = layout;
		PointerPressed += (_, args) =>
		{
			if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed || args.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<Button>().Any()) return;
			if (args.ClickCount == 2) ToggleMaximized(window);
			else if (PlatformServices.Current.Capabilities.SupportsWindowDrag) window.BeginMoveDrag(args);
			args.Handled = true;
		};
		PointerEntered += (_, _) => RefreshLabels();
		window.PropertyChanged += (_, args) =>
		{
			if (args.Property == Window.TitleProperty || args.Property == Window.WindowStateProperty || args.Property == Window.CanResizeProperty || args.Property == Window.CanMinimizeProperty || args.Property == Window.IsVisibleProperty) RefreshLabels();
		};
		// 仅关闭的窗口由调用方按拖动能力选择系统装饰，不覆盖其回退或接管缩放。
		if (!closeOnly) EnableBorderlessResize(window);
		RefreshLabels();
	}

	internal void RefreshLabels()
	{
		bool english = _english();
		if (_title is not null) _title.Text = _window.Title;
		SetLabel(_close, english ? "Close window" : "关闭窗口");
		SetLabel(_minimize, english ? "Minimize" : "最小化");
		SetLabel(_zoom, _window.WindowState == WindowState.Maximized ? (english ? "Restore" : "还原") : (english ? "Maximize" : "最大化"));
		_zoom.IsVisible = _minimize.IsVisible = !_closeOnly;
		_zoom.IsEnabled = _window.CanResize;
		_minimize.IsEnabled = _window.CanMinimize;
	}

	private static void SetLabel(Button button, string label)
	{
		AutomationProperties.SetName(button, label);
		ToolTip.SetTip(button, label);
	}

	/// <summary>
	/// web 交通灯：12px 圆点、8px 间距、85% 填充加顶部内高光；悬停与键盘焦点改为同色外发光，按下缩放到 0.85，不显示符号。
	/// </summary>
	internal static Button TrafficButton(string kind)
	{
		BoxShadows rest = BoxShadows.Parse("inset 0 1 0 0 " + NoriThemeTokens.Value("traffic-highlight"));
		BoxShadows glow = BoxShadows.Parse("0 0 8 0 " + NoriThemeTokens.Value("traffic-" + kind + "-hover"));
		Border dot = new()
		{
			Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = NoriThemeTokens.Brush("traffic-" + kind), BoxShadow = rest,
			RenderTransformOrigin = RelativePoint.Center,
			Transitions = [new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(120) }],
		};
		Button button = new()
		{
			Name = "Window" + (kind == "zoom" ? "Maximize" : kind == "close" ? "Close" : "Minimize"),
			Width = 20, Height = 28, MinWidth = 0, MinHeight = 0, Padding = default, BorderThickness = default,
			Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
			Template = new FuncControlTemplate<Button>((_, _) => new Border { Background = Brushes.Transparent, Child = dot }),
		};
		void Refresh()
		{
			bool enabled = button.IsEffectivelyEnabled;
			dot.Background = NoriThemeTokens.Brush(enabled ? "traffic-" + kind : "traffic-disabled");
			dot.Opacity = enabled ? 1 : NoriMetrics.OpacityDisabled;
			dot.BoxShadow = enabled && (button.IsPointerOver || button.IsKeyboardFocusWithin) ? glow : rest;
			dot.RenderTransform = TransformOperations.Parse(enabled && button.IsPressed ? "scale(0.85)" : "scale(1)");
		}
		button.PropertyChanged += (_, args) =>
		{
			if (args.Property == InputElement.IsPointerOverProperty || args.Property == Button.IsPressedProperty
				|| args.Property == InputElement.IsEffectivelyEnabledProperty || args.Property == InputElement.IsKeyboardFocusWithinProperty) Refresh();
		};
		Refresh();
		return button;
	}

	/// <summary>
	/// web 窗口外框：自上而下的冰青渐变描边，窗口激活后切换为品牌青渐变；
	/// 圆角与 Windows 11 DWM 的 8px 窗口圆角一致，最大化时取消描边与圆角。
	/// </summary>
	internal static Border WindowFrame(Window window, Control content)
	{
		Border frame = new() { Name = "NativeWindowFrame", Child = content };
		void Refresh()
		{
			bool maximized = window.WindowState == WindowState.Maximized;
			frame.BorderBrush = RingBrush(window.IsActive);
			frame.BorderThickness = new Thickness(maximized ? 0 : 1);
			frame.CornerRadius = new CornerRadius(maximized ? 0 : NoriMetrics.RadiusMd);
		}
		window.PropertyChanged += (_, args) =>
		{
			if (args.Property == WindowBase.IsActiveProperty || args.Property == Window.WindowStateProperty) Refresh();
		};
		Refresh();
		return frame;
	}

	internal static IBrush RingBrush(bool focused) => new LinearGradientBrush
	{
		StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
		EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
		GradientStops =
		{
			new GradientStop(NoriThemeTokens.Color(focused ? "ring-focus-from" : "ring-rest-from"), 0),
			new GradientStop(NoriThemeTokens.Color(focused ? "ring-focus-via" : "ring-rest-via"), 0.5),
			new GradientStop(NoriThemeTokens.Color(focused ? "ring-focus-to" : "ring-rest-to"), 1),
		},
	};

	internal static NativeWindowChrome Attach(Window window, Func<bool>? english = null, Action? onClose = null)
	{
		object? content = window.Content;
		window.Content = null;
		var bar = new NativeWindowChrome(window, english, onClose: onClose);
		Grid frame = new() { RowDefinitions = new RowDefinitions("Auto,*") };
		frame.Children.Add(bar);
		ContentControl body = new() { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
		Grid.SetRow(body, 1);
		frame.Children.Add(body);
		window.Content = WindowFrame(window, frame);
		return bar;
	}

	internal static void ToggleMaximized(Window window)
	{
		if (window.CanResize) window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
	}

	/// <summary>在无系统装饰时保留八方向缩放。</summary>
	internal static void EnableBorderlessResize(Window window)
	{
		window.WindowDecorations = WindowDecorations.None;
		if (ResizeWindows.TryGetValue(window, out _)) return;
		ResizeWindows.Add(window, new object());
		bool resizingCursor = false;
		Cursor? originalCursor = null;
		void ResetCursor()
		{
			if (!resizingCursor) return;
			window.Cursor = originalCursor;
			resizingCursor = false;
		}
		window.PointerMoved += (_, args) =>
		{
			WindowEdge? edge = window.CanResize && window.WindowState == WindowState.Normal ? ResizeEdge(args.GetPosition(window), window.Bounds.Size) : null;
			if (edge is null) { ResetCursor(); return; }
			if (!resizingCursor) { originalCursor = window.Cursor; resizingCursor = true; }
			window.Cursor = new Cursor(edge switch
			{
				WindowEdge.NorthWest => StandardCursorType.TopLeftCorner,
				WindowEdge.NorthEast => StandardCursorType.TopRightCorner,
				WindowEdge.SouthWest => StandardCursorType.BottomLeftCorner,
				WindowEdge.SouthEast => StandardCursorType.BottomRightCorner,
				WindowEdge.North or WindowEdge.South => StandardCursorType.SizeNorthSouth,
				_ => StandardCursorType.SizeWestEast,
			});
		};
		window.PointerExited += (_, _) => ResetCursor();
		window.PropertyChanged += (_, args) =>
		{
			if (args.Property == Window.WindowStateProperty || args.Property == Window.CanResizeProperty) ResetCursor();
		};
		window.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
		{
			if (!window.CanResize || window.WindowState != WindowState.Normal || !PlatformServices.Current.Capabilities.SupportsWindowDrag || !args.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
			if (ResizeEdge(args.GetPosition(window), window.Bounds.Size) is not { } edge) return;
			window.BeginResizeDrag(edge, args);
			args.Handled = true;
		}, RoutingStrategies.Tunnel);
	}

	internal static WindowEdge? ResizeEdge(Point point, Size size)
	{
		const double edge = 5;
		if (point.X < 0 || point.Y < 0 || point.X > size.Width || point.Y > size.Height) return null;
		bool left = point.X < edge, right = point.X >= size.Width - edge;
		bool top = point.Y < edge, bottom = point.Y >= size.Height - edge;
		if (top) return left ? WindowEdge.NorthWest : right ? WindowEdge.NorthEast : WindowEdge.North;
		if (bottom) return left ? WindowEdge.SouthWest : right ? WindowEdge.SouthEast : WindowEdge.South;
		return left ? WindowEdge.West : right ? WindowEdge.East : null;
	}
}
