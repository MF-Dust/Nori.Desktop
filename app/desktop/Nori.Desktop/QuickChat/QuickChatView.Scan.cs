using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.Desktop.Appearance;
using Nori.Desktop.Vision;

namespace Nori.Desktop.QuickChat;

public sealed partial class QuickChatView
{
	private readonly Border _scanGlow = new() { CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
	private readonly Button _scanButton = new() { Name = "QuickChatScan", VerticalAlignment = VerticalAlignment.Center };
	private readonly Avalonia.Controls.Shapes.Path _scanIcon = new()
	{
		Data = Geometry.Parse("M4 7 V4 H7 M17 4 H20 V7 M20 17 V20 H17 M7 20 H4 V17 M15 15 L18 18 M16 11 A5 5 0 1 1 6 11 A5 5 0 1 1 16 11"),
		Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 2.5,
		StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
	};
	private readonly Border _scanReadout = new()
	{
		Name = "QuickChatScanReadout", IsVisible = false,
		MaxWidth = 263, HorizontalAlignment = HorizontalAlignment.Left,
		Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 0, 10),
		CornerRadius = new CornerRadius(5, 5, 5, 2), BorderThickness = new Thickness(1),
	};
	private readonly TextBlock _scanText = new() { FontSize = 12, LineHeight = 18, TextWrapping = TextWrapping.Wrap, FontFamily = NoriTypography.Scan };
	private readonly TextBlock _scanHeader = new() { FontSize = 12, LineHeight = 18, FontFamily = NoriTypography.Scan };
	private ScanChipState _scanChip = new();
	private Func<Task>? _toggleScan;
	private bool _scanPicking;
	private string _scanFullText = "";
	private string[] _scanCharacters = [];
	private DateTimeOffset _scanReceivedAt;
	private int _scanCount = -1;
	private DateTimeOffset _scanStyleAt;

	internal bool ScanEnglish => _state.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);

	internal void AttachScan(ScanChipState chip, Func<Task> toggle)
	{
		_scanChip = chip; _toggleScan = toggle;
		RenderScan(_now(), forceStyle: true);
	}

	internal void SetScanPicking(bool picking)
	{
		if (_disposed) return;
		_scanPicking = picking;
		RenderScan(_now(), forceStyle: true);
	}

	internal void ShowScanReadout(string text)
	{
		if (_disposed) return;
		_scanFullText = text;
		_scanCharacters = text.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
		_scanReceivedAt = _now(); _scanCount = -1;
		AutomationProperties.SetName(_scanReadout, text);
		RenderScan(_scanReceivedAt);
	}

	private static IBrush ScanBrush(string key, byte alpha = 255) => new SolidColorBrush(QuickChatPalette.Tint(key, alpha));

	private void BuildScanButton()
	{
		_scanButton.Classes.Add("quick-send");
		_scanButton.Content = _scanIcon;
		_scanGlow.Child = _scanButton;
		_scanButton.Click += (_, _) => { if (_toggleScan is { } toggle) Run(toggle()); };
	}

	private void BuildScanReadout()
	{
		_scanHeader.Foreground = ScanBrush("scan-muted");
		_scanText.Foreground = ScanBrush("scan-accent");
		Border header = new()
		{
			Child = _scanHeader, BorderThickness = new Thickness(0, 0, 0, 1),
			BorderBrush = ScanBrush("scan-border", 64), Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(0, 0, 0, 3),
		};
		StackPanel content = new();
		content.Children.Add(header);
		content.Children.Add(new ScrollViewer
		{
			Content = _scanText, MaxHeight = 160,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
		});
		Grid readoutLayers = new();
		readoutLayers.Children.Add(new ScanReadoutLines { IsHitTestVisible = false });
		readoutLayers.Children.Add(content);
		_scanReadout.Child = readoutLayers;
		_scanReadout.BorderBrush = ScanBrush("scan-border", 176);
		_scanReadout.Background = new LinearGradientBrush
		{
			StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
			GradientStops =
			{
				new GradientStop(QuickChatPalette.Tint("scan-background", 250), 0),
				new GradientStop(QuickChatPalette.Tint("scan-surface", 242), 0.55),
				new GradientStop(QuickChatPalette.Tint("scan-background", 250), 1),
			},
		};
		_scanReadout.BoxShadow = Shadows(ShadowValue(0, 4, 16, QuickChatPalette.Tint("scrim", 102)),
			ShadowValue(0, 0, 12, QuickChatPalette.Tint("scan-accent", 31)),
			ShadowValue(0, 1, 0, QuickChatPalette.Tint("scan-accent", 41), inset: true));
		AutomationProperties.SetLiveSetting(_scanReadout, AutomationLiveSetting.Polite);
	}

	private sealed class ScanReadoutLines : Control
	{
		private readonly Pen _line = new(ScanBrush("scan-accent", 11), 1);
		public override void Render(DrawingContext context)
		{
			for (double y = 0.5; y < Bounds.Height; y += 3)
				context.DrawLine(_line, new Point(0, y), new Point(Bounds.Width, y));
		}
	}

	private void RenderScan(DateTimeOffset now, bool forceStyle = false)
	{
		if (forceStyle || now - _scanStyleAt >= TimeSpan.FromMilliseconds(500)) RenderScanButton(now);
		bool visible = _scanFullText.Length > 0 && now < _scanReceivedAt.AddSeconds(30);
		bool layout = _scanReadout.IsVisible != visible;
		_scanReadout.IsVisible = visible;
		if (visible)
		{
			_scanHeader.Text = T("【分析芯片】", "[ANALYSIS CHIP]");
			int count = _reduceMotion() ? _scanCharacters.Length : Math.Clamp((int)((now - _scanReceivedAt).TotalMilliseconds / 28), 0, _scanCharacters.Length);
			if (count != _scanCount)
			{
				_scanCount = count;
				_scanText.Text = string.Concat(_scanCharacters.Take(count)) + (count < _scanCharacters.Length ? "▌" : "");
				layout = true;
			}
		}
		else if (_scanFullText.Length > 0)
		{
			_scanFullText = ""; _scanCharacters = []; _scanText.Text = "";
		}
		if (layout) DesiredLayoutChanged?.Invoke();
	}

	private void RenderScanButton(DateTimeOffset now)
	{
		_scanStyleAt = now;
		int charges = _scanChip.Charges(now);
		_scanButton.IsEnabled = !_preparing;
		_scanButton.Opacity = _state.SafeMode || !_state.Configured ? 0.35 : 1;
		_scanIcon.Opacity = 0.5 + 0.5 * charges / ScanChipState.Capacity;
		_scanIcon.Stroke = _scanPicking ? ScanBrush("scan-background")
			: _composer.IsKeyboardFocusWithin ? ScanBrush("chat-composer", 144) : QuickChatPalette.Placeholder;
		_scanButton.Background = _scanPicking ? Gradient(QuickChatPalette.Tint("scan-accent"), QuickChatPalette.Tint("scan-accent-end")) : Brushes.Transparent;
		_scanGlow.BoxShadow = _scanPicking ? Shadows(ShadowValue(0, 2, 8, QuickChatPalette.Tint("scan-accent", 128))) : default;
		string label = T("分析窗口", "Analyze window");
		AutomationProperties.SetName(_scanButton, label);
		int minutes = Math.Max(1, (int)Math.Ceiling(_scanChip.NextChargeIn(now).TotalMinutes));
		ToolTip.SetTip(_scanButton, _state.SafeMode ? T("安全模式已暂停扫描", "Scanning is paused in safe mode")
			: !_state.Configured ? T("请先配置支持图片的 AI", "Configure an AI model with image support first")
			: charges > 0 ? T($"芯片余量：{charges}/{ScanChipState.Capacity} 次扫描", $"Chip charge: {charges}/{ScanChipState.Capacity} scans")
			: T($"散热中；约 {minutes} 分钟后可扫描", $"Cooling down; scan available in ~{minutes} min"));
	}
}
