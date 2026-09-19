using Avalonia;
using Avalonia.Controls;

namespace Nori.Desktop.Windows;

/// <summary>普通原生窗口的默认尺寸与首次显示时的工作区收敛。</summary>
internal static class NativeWindowSizing
{
	internal static readonly Size DefaultSize = new(1040, 720);
	internal static readonly Size ChatSize = new(960, 720);
	internal static readonly Size FirstRunSize = new(800, 560);
	internal static readonly Size MinimumSize = new(720, 480);
	private const double WorkAreaMargin = 16;

	internal static void Apply(Window window, Size preferred)
	{
		window.Width = preferred.Width;
		window.Height = preferred.Height;
		window.MinWidth = MinimumSize.Width;
		window.MinHeight = MinimumSize.Height;
		ConstrainOnFirstOpen(window, preferred);
	}

	internal static void ConstrainOnFirstOpen(Window window, Size preferred)
	{
		window.Opened += OnOpened;
		void OnOpened(object? sender, EventArgs args)
		{
			window.Opened -= OnOpened;
			// 构造后显式指定的尺寸属于调用方；隐藏后再次显示也不重置用户调整。
			if (window.Width != preferred.Width || window.Height != preferred.Height) return;
			var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
			if (screen is null) return;
			double scaling = screen.Scaling;
			if (!double.IsFinite(scaling) || scaling <= 0) return;
			Size frame = window.FrameSize ?? window.ClientSize;
			Size decorations = window.WindowDecorations == WindowDecorations.None ? default : new Size(
				Math.Max(16, frame.Width - window.ClientSize.Width),
				Math.Max(48, frame.Height - window.ClientSize.Height));
			Size size = FitToWorkArea(preferred, screen.WorkingArea, scaling, decorations,
				new Size(window.MinWidth, window.MinHeight));
			if (size == preferred) return;
			window.Width = size.Width;
			window.Height = size.Height;
			if (window.WindowStartupLocation == WindowStartupLocation.CenterScreen)
			{
				window.Position = new PixelPoint(
					screen.WorkingArea.X + (int)Math.Round((screen.WorkingArea.Width - (size.Width + decorations.Width) * scaling) / 2),
					screen.WorkingArea.Y + (int)Math.Round((screen.WorkingArea.Height - (size.Height + decorations.Height) * scaling) / 2));
			}
		}
	}

	internal static Size FitToWorkArea(Size preferred, PixelRect workingArea, double scaling, Size decorations, Size minimum)
	{
		if (!double.IsFinite(scaling) || scaling <= 0 || workingArea.Width <= 0 || workingArea.Height <= 0)
			return preferred;
		double width = workingArea.Width / scaling - decorations.Width - WorkAreaMargin * 2;
		double height = workingArea.Height / scaling - decorations.Height - WorkAreaMargin * 2;
		return new Size(
			Math.Min(preferred.Width, Math.Max(minimum.Width, Math.Floor(width))),
			Math.Min(preferred.Height, Math.Max(minimum.Height, Math.Floor(height))));
	}
}
