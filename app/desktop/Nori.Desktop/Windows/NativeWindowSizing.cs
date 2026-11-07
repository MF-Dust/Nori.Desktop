using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Nori.Desktop.Appearance;

namespace Nori.Desktop.Windows;

/// <summary>普通原生窗口的默认尺寸与首次显示时的工作区收敛。</summary>
internal static class NativeWindowSizing
{
	internal static readonly Size DefaultSize = new(1040, 720);
	internal static readonly Size ChatSize = new(960, 720);
	internal static readonly Size FirstRunSize = new(800, 560);
	internal static readonly Size MinimumSize = new(720, 480);
	private const double WorkAreaMargin = 16;

	// 未缩放时的最小尺寸。0 表示该方向不限制，乘倍率后仍为 0。
	private sealed class MinBaseline
	{
		public double Width;
		public double Height;
	}

	private static readonly ConditionalWeakTable<Window, MinBaseline> Baselines = new();

	internal static void Apply(Window window, Size preferred)
	{
		double scale = CurrentScale();
		Size sized = ScalePreferred(preferred, scale);
		window.Width = sized.Width;
		window.Height = sized.Height;
		SetBaseline(window, MinimumSize.Width, MinimumSize.Height);
		ApplyScale(window, scale);
		ConstrainOnFirstOpen(window, preferred);
	}

	/// <summary>按界面缩放更新最小尺寸；放大出来的部分在能取到屏幕时夹进工作区，不低于未缩放的基准。</summary>
	internal static void ApplyScale(Window window, double scale)
	{
		MinBaseline baseline = Baselines.GetValue(window, static w => new MinBaseline { Width = w.MinWidth, Height = w.MinHeight });
		double width = baseline.Width * scale;
		double height = baseline.Height * scale;
		if (TryWorkAreaLimit(window, out double limitWidth, out double limitHeight))
		{
			width = Math.Min(width, Math.Max(baseline.Width, limitWidth));
			height = Math.Min(height, Math.Max(baseline.Height, limitHeight));
		}
		window.MinWidth = width;
		window.MinHeight = height;
	}

	/// <summary>首次显示时把默认尺寸收进工作区；<paramref name="scaled"/> 为 false 的窗口内容不随界面缩放，默认尺寸也不放大。</summary>
	internal static void ConstrainOnFirstOpen(Window window, Size preferred, bool scaled = true)
	{
		double scale = scaled ? CurrentScale() : 1;
		Size sized = ScalePreferred(preferred, scale);
		// 仍等于传入尺寸的窗口按当前倍率放大；Opened 再用放大后的尺寸判断调用方有没有改过。
		if (scale != 1 && window.Width == preferred.Width && window.Height == preferred.Height)
		{
			window.Width = sized.Width;
			window.Height = sized.Height;
		}
		window.Opened += OnOpened;
		void OnOpened(object? sender, EventArgs args)
		{
			window.Opened -= OnOpened;
			// 构造后显式指定的尺寸属于调用方；隐藏后再次显示也不重置用户调整。
			if (window.Width != sized.Width || window.Height != sized.Height) return;
			var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
			if (screen is null) return;
			double scaling = screen.Scaling;
			if (!double.IsFinite(scaling) || scaling <= 0) return;
			Size frame = window.FrameSize ?? window.ClientSize;
			Size decorations = window.WindowDecorations == WindowDecorations.None ? default : new Size(
				Math.Max(16, frame.Width - window.ClientSize.Width),
				Math.Max(48, frame.Height - window.ClientSize.Height));
			Size size = FitToWorkArea(sized, screen.WorkingArea, scaling, decorations,
				new Size(window.MinWidth, window.MinHeight));
			if (size == sized) return;
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

	private static void SetBaseline(Window window, double width, double height)
	{
		MinBaseline baseline = Baselines.GetValue(window, static _ => new MinBaseline());
		baseline.Width = width;
		baseline.Height = height;
	}

	private static double CurrentScale()
	{
		double scale = NoriAppearance.Current?.UiScale ?? 1;
		return double.IsFinite(scale) && scale > 0 ? scale : 1;
	}

	private static Size ScalePreferred(Size preferred, double scale) =>
		scale == 1 ? preferred : new Size(preferred.Width * scale, preferred.Height * scale);

	private static bool TryWorkAreaLimit(Window window, out double limitWidth, out double limitHeight)
	{
		limitWidth = 0;
		limitHeight = 0;
		try
		{
			var screens = window.Screens;
			var screen = screens.ScreenFromWindow(window) ?? screens.Primary;
			if (screen is null) return false;
			double scaling = screen.Scaling;
			if (!double.IsFinite(scaling) || scaling <= 0 || screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0)
				return false;
			limitWidth = Math.Floor(screen.WorkingArea.Width / scaling - WorkAreaMargin * 2);
			limitHeight = Math.Floor(screen.WorkingArea.Height / scaling - WorkAreaMargin * 2);
			return limitWidth > 0 && limitHeight > 0;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}
}
