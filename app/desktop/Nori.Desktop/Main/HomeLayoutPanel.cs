using Avalonia;
using Avalonia.Controls;

namespace Nori.Desktop.Main;

/// <summary>首页卡片随可用宽度切换四列、两列或单列，每行等高且填满容器。</summary>
internal sealed class HomeLayoutPanel : Panel
{
	private const double Gap = 12;
	internal double FourColumnMinimum { get; init; } = 720;
	internal double TwoColumnMinimum { get; init; } = 360;

	protected override Size MeasureOverride(Size availableSize)
	{
		double width = double.IsInfinity(availableSize.Width) ? FourColumnMinimum : availableSize.Width;
		int columns = Columns(width);
		double cellWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
		double height = 0;
		for (int start = 0; start < Children.Count; start += columns)
		{
			double rowHeight = 0;
			for (int index = start; index < Math.Min(start + columns, Children.Count); index++)
			{
				Children[index].Measure(new Size(cellWidth, double.PositiveInfinity));
				rowHeight = Math.Max(rowHeight, Children[index].DesiredSize.Height);
			}
			height += rowHeight + (start > 0 ? Gap : 0);
		}
		return new Size(width, height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		int columns = Columns(finalSize.Width);
		double cellWidth = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
		double y = 0;
		for (int start = 0; start < Children.Count; start += columns)
		{
			double rowHeight = 0;
			for (int index = start; index < Math.Min(start + columns, Children.Count); index++)
				rowHeight = Math.Max(rowHeight, Children[index].DesiredSize.Height);
			for (int index = start; index < Math.Min(start + columns, Children.Count); index++)
				Children[index].Arrange(new Rect((index - start) * (cellWidth + Gap), y, cellWidth, rowHeight));
			y += rowHeight + Gap;
		}
		return finalSize;
	}

	private int Columns(double width) => width >= FourColumnMinimum ? 4 : width >= TwoColumnMinimum ? 2 : 1;
}

/// <summary>角色信息和操作在宽屏并排，窄屏将操作移到下一行。</summary>
internal sealed class HomeHeroPanel : Panel
{
	private const double Gap = 16;
	private const double HorizontalMinimum = 600;

	protected override Size MeasureOverride(Size availableSize)
	{
		if (Children.Count != 2) return base.MeasureOverride(availableSize);
		double width = double.IsInfinity(availableSize.Width) ? HorizontalMinimum : availableSize.Width;
		Control identity = Children[0];
		Control actions = Children[1];
		actions.Measure(new Size(width, double.PositiveInfinity));
		bool horizontal = width >= HorizontalMinimum;
		identity.Measure(new Size(horizontal ? Math.Max(0, width - actions.DesiredSize.Width - Gap) : width, double.PositiveInfinity));
		return new Size(width, horizontal ? Math.Max(identity.DesiredSize.Height, actions.DesiredSize.Height)
			: identity.DesiredSize.Height + Gap + actions.DesiredSize.Height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		if (Children.Count != 2) return base.ArrangeOverride(finalSize);
		Control identity = Children[0];
		Control actions = Children[1];
		if (finalSize.Width >= HorizontalMinimum)
		{
			double identityWidth = Math.Max(0, finalSize.Width - actions.DesiredSize.Width - Gap);
			identity.Arrange(new Rect(0, 0, identityWidth, finalSize.Height));
			actions.Arrange(new Rect(identityWidth + Gap, 0, actions.DesiredSize.Width, finalSize.Height));
		}
		else
		{
			identity.Arrange(new Rect(0, 0, finalSize.Width, identity.DesiredSize.Height));
			actions.Arrange(new Rect(0, identity.DesiredSize.Height + Gap, finalSize.Width, actions.DesiredSize.Height));
		}
		return finalSize;
	}
}
