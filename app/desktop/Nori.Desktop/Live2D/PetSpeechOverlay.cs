using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nori.Desktop.Live2D;

/// <summary>伴侣视窗原生短句气泡，不参与窗口命中测试。</summary>
public sealed class PetSpeechOverlay : Border
{
	private readonly TextBlock _text;
	private readonly DispatcherTimer _hideTimer;

	public PetSpeechOverlay()
	{
		IsVisible = false;
		IsHitTestVisible = false;
		HorizontalAlignment = HorizontalAlignment.Center;
		VerticalAlignment = VerticalAlignment.Top;
		MaxWidth = 280;
		Margin = new Avalonia.Thickness(8, 10);
		Padding = new Avalonia.Thickness(12, 8);
		Background = new SolidColorBrush(Color.FromArgb(235, 10, 26, 40));
		BorderBrush = new SolidColorBrush(Color.FromArgb(180, 125, 227, 255));
		BorderThickness = new Avalonia.Thickness(1);
		CornerRadius = new Avalonia.CornerRadius(10);
		BoxShadow = new BoxShadows(new BoxShadow
		{
			Blur = 18,
			Spread = 0,
			OffsetX = 0,
			OffsetY = 4,
			Color = Color.FromArgb(110, 0, 0, 0),
		});

		_text = new TextBlock
		{
			Foreground = new SolidColorBrush(Color.FromRgb(220, 240, 255)),
			FontSize = 13,
			TextWrapping = TextWrapping.Wrap,
			TextAlignment = TextAlignment.Center,
		};
		Child = _text;

		_hideTimer = new DispatcherTimer {Interval = TimeSpan.FromSeconds(6)};
		_hideTimer.Tick += (_, _) =>
		{
			_hideTimer.Stop();
			IsVisible = false;
		};
	}

	/// <summary>显示一条临时短句。</summary>
	public void ShowText(string text)
	{
		string value = text.Trim();
		if (value.Length == 0)
		{
			ClearText();
			return;
		}
		_hideTimer.Stop();
		_text.Text = value;
		IsVisible = true;
		_hideTimer.Start();
	}

	/// <summary>立即清除短句。</summary>
	public void ClearText()
	{
		_hideTimer.Stop();
		_text.Text = "";
		IsVisible = false;
	}
}
