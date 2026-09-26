using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Nori.Core.Configuration;
using Nori.Core.Platform;
using Nori.Desktop.Appearance;

namespace Nori.Desktop.Windows;

/// <summary>所有普通窗口共用的自绘标题栏；关闭始终经过窗口原有的保存与隐藏流程。</summary>
internal sealed class NativeWindowChrome : Border
{
	private static readonly ConditionalWeakTable<Window, object> ResizeWindows = new();
	private readonly Window _window;
	private readonly Func<bool> _english;
	private readonly TextBlock? _title;
	private readonly Button _close;
	private readonly Button _minimize;
	private readonly Button _zoom;

	internal NativeWindowChrome(Window window, Func<bool>? english = null, Control? heading = null, Action? onClose = null)
	{
		_window = window;
		_english = english ?? (() => UiLanguage.IsEnglish(System.Globalization.CultureInfo.CurrentUICulture.Name));
		Name = "NativeWindowTitleBar";
		Height = 40;
		Background = NoriThemeTokens.Brush("bg-sidebar");
		BorderBrush = NoriThemeTokens.Brush("line-subtle");
		BorderThickness = new Thickness(0, 0, 0, 1);
		Padding = new Thickness(10, 0, 16, 0);
		_close = TrafficButton("close", "×");
		_minimize = TrafficButton("minimize", "−");
		_zoom = TrafficButton("zoom", "+");
		_close.Click += (_, _) => { if (onClose is null) window.Close(); else onClose(); };
		_minimize.Click += (_, _) => { if (window.CanMinimize) window.WindowState = WindowState.Minimized; };
		_zoom.Click += (_, _) => ToggleMaximized(window);
		StackPanel lights = new() { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center, Children = { _close, _minimize, _zoom } };
		if (heading is null)
		{
			_title = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = NoriThemeTokens.Brush("text-primary"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
			heading = _title;
		}
		Grid layout = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14 };
		layout.Children.Add(lights);
		Grid.SetColumn(heading, 1);
		layout.Children.Add(heading);
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
		EnableBorderlessResize(window);
		RefreshLabels();
	}

	internal void RefreshLabels()
	{
		bool english = _english();
		if (_title is not null) _title.Text = _window.Title;
		SetLabel(_close, english ? "Close window" : "关闭窗口");
		SetLabel(_minimize, english ? "Minimize" : "最小化");
		SetLabel(_zoom, _window.WindowState == WindowState.Maximized ? (english ? "Restore" : "还原") : (english ? "Maximize" : "最大化"));
		_zoom.IsEnabled = _window.CanResize;
		_zoom.Opacity = _window.CanResize ? 1 : .35;
		_minimize.IsEnabled = _window.CanMinimize;
		_minimize.Opacity = _window.CanMinimize ? 1 : .35;
	}

	private static void SetLabel(Button button, string label)
	{
		AutomationProperties.SetName(button, label);
		ToolTip.SetTip(button, label);
	}

	internal static Button TrafficButton(string kind, string glyph)
	{
		var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = NoriThemeTokens.Brush("traffic-" + kind) };
		var symbol = new TextBlock { Text = glyph, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = NoriThemeTokens.Brush("bg-base"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
		dot.Child = symbol;
		Button button = new()
		{
			Name = "Window" + (kind == "zoom" ? "Maximize" : kind == "close" ? "Close" : "Minimize"),
			Width = 24, Height = 28, MinWidth = 0, MinHeight = 0, Padding = default, BorderThickness = default,
			Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
			Template = new FuncControlTemplate<Button>((_, _) => new Border { Background = Brushes.Transparent, Child = dot }),
		};
		button.PointerEntered += (_, _) => { dot.Background = NoriThemeTokens.Brush("traffic-" + kind + "-hover"); symbol.Opacity = 1; };
		button.PointerExited += (_, _) => { dot.Background = NoriThemeTokens.Brush("traffic-" + kind); symbol.Opacity = 0; };
		button.GotFocus += (_, _) => symbol.Opacity = 1;
		button.LostFocus += (_, _) => symbol.Opacity = 0;
		return button;
	}

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
		window.Content = frame;
		return bar;
	}

	internal static void ToggleMaximized(Window window)
	{
		if (window.CanResize) window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
	}

	/// <summary>在无系统装饰时保留八方向缩放；WebView 宿主需为这些边缘预留原生空域。</summary>
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
