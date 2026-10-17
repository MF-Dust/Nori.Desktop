using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Nori.Desktop.Appearance;

namespace Nori.SplashHost;

/// <summary>各参考布局共用启动状态、Nori 主题和资源生命周期，不包含第三方作品。</summary>
internal sealed partial class SplashWindow : Window
{
	private readonly TextBlock _stage;
	private readonly ProgressBar _progress;
	private readonly Image _background;
	private readonly Bitmap _logo;
	private Bitmap? _customBitmap;
	private bool _closed;

	internal SplashWindow(string theme, string background, string version)
	{
		Title = "Nori · 正在启动";
		(Width, Height) = theme switch
		{
			"android-studio" => (640, 400),
			"microsoft-365" => (400, 250),
			"office-legacy" => (480, 300),
			"island" => (520, 80),
			"minimal" => (520, 400),
			_ => (900, 600),
		};
		if (Screens.Primary is { } screen)
		{
			Width = Math.Min(Width, screen.WorkingArea.Width / screen.Scaling - 32);
			Height = Math.Min(Height, screen.WorkingArea.Height / screen.Scaling - 32);
			if (theme == "island")
			{
				WindowStartupLocation = WindowStartupLocation.Manual;
				Position = new PixelPoint(screen.WorkingArea.X + (screen.WorkingArea.Width - (int)(Width * screen.Scaling)) / 2,
					screen.WorkingArea.Y + (int)(12 * screen.Scaling));
			}
		}
		if (theme != "island") WindowStartupLocation = WindowStartupLocation.CenterScreen;
		if (theme == "alls") WindowState = WindowState.FullScreen;
		CanResize = false;
		WindowDecorations = WindowDecorations.None;
		ShowInTaskbar = false;
		ShowActivated = false;
		Topmost = true;
		TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
		Background = Brushes.Transparent;
		_logo = new Bitmap(AssetLoader.Open(new Uri("avares://Nori.SplashHost/Assets/logo.png")));
		_background = new Image { Stretch = Stretch.UniformToFill };
		_stage = Label("正在准备", NoriMetrics.FontBase, "text-body");
		AutomationProperties.SetLiveSetting(_stage, AutomationLiveSetting.Polite);
		_progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4 };
		AutomationProperties.SetName(_progress, "启动阶段进度");

		Control layout = theme switch
		{
			"android-studio" => BuildAndroid(version),
			"microsoft-365" => Build365(),
			"office-legacy" => BuildOffice(version),
			"island" => BuildIsland(version),
			"alls" => BuildAlls(version),
			_ => BuildStandard(theme != "minimal", version),
		};
		Content = new Border
		{
			CornerRadius = new CornerRadius(theme == "alls" ? 0 : NoriMetrics.RadiusXl), ClipToBounds = true,
			Background = NoriThemeTokens.Brush("bg-base"), BorderBrush = NoriThemeTokens.Brush("line-strong"), BorderThickness = new Thickness(1), Child = layout,
		};
		Closed += (_, _) =>
		{
			_closed = true;
			_customBitmap?.Dispose();
			_logo.Dispose();
			(Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
		};
		if (theme is "character" or "android-studio" && !string.IsNullOrWhiteSpace(background)) _ = LoadBackgroundAsync(background);
	}

	internal void UpdateStage(int progress, string stage)
	{
		_progress.Value = Math.Max(_progress.Value, Math.Clamp(progress, 0, 100));
		_stage.Text = stage;
	}

	private static TextBlock Label(string text, double size, string color) => new()
	{
		Text = text, FontSize = size, Foreground = NoriThemeTokens.Brush(color), TextWrapping = TextWrapping.Wrap,
	};

	private Image Logo(double size) => new() { Source = _logo, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };

	private Button Dismiss()
	{
		Button button = new() { Content = "隐藏", FontSize = NoriMetrics.FontXs, Padding = new Thickness(8, 4), MinHeight = 24 };
		button.Classes.Add("ghost");
		AutomationProperties.SetName(button, "隐藏启动画面，继续启动 Nori");
		button.Click += (_, _) => Close();
		return button;
	}

	private StackPanel Status() => new() { Spacing = NoriMetrics.Spacing3, Children = { _stage, _progress } };

	private Control Brand(string version, double logoSize = 40, bool compact = false)
	{
		Grid brand = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = NoriMetrics.Spacing3 };
		brand.Children.Add(Logo(logoSize));
		StackPanel identity = new() { Spacing = NoriMetrics.Spacing1 };
		TextBlock title = Label("Nori", compact ? NoriMetrics.FontLg : NoriMetrics.Font3xl, "text-primary");
		title.FontWeight = FontWeight.SemiBold;
		identity.Children.Add(title);
		TextBlock versionText = Label(version, NoriMetrics.FontXs, "text-muted");
		versionText.TextWrapping = TextWrapping.NoWrap;
		versionText.TextTrimming = TextTrimming.CharacterEllipsis;
		ToolTip.SetTip(versionText, version);
		identity.Children.Add(versionText);
		Grid.SetColumn(identity, 1);
		brand.Children.Add(identity);
		return brand;
	}

	private async Task LoadBackgroundAsync(string path)
	{
		Bitmap? bitmap = null;
		try
		{
			bitmap = await Task.Run(() =>
			{
				if (!Path.IsPathFullyQualified(path) || path.Replace('\\', '/').StartsWith("//", StringComparison.Ordinal)) return null;
				FileInfo file = new(path);
				if (!file.Exists || file.Length > 16 * 1024 * 1024) return null;
				using FileStream stream = File.OpenRead(path);
				return Bitmap.DecodeToWidth(stream, 1200);
			});
			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (_closed) { bitmap?.Dispose(); return; }
				_customBitmap = bitmap;
				_background.Source = bitmap;
			});
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			bitmap?.Dispose();
			// 文件失效或无法解码时保持作品区留白。
		}
	}
}
