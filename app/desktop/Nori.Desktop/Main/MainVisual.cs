using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Nori.Desktop.Chat;

namespace Nori.Desktop.Main;

/// <summary>主界面使用统一的线性图标，避免系统字体的符号形状差异。</summary>
internal static class MainVisual
{
	internal static Control Icon(string name, double size = 20, IBrush? brush = null)
	{
		string data = name switch
		{
			"home" => "M3,11 L12,3 21,11 M5,10 L5,21 10,21 10,15 14,15 14,21 19,21 19,10",
			"chat" => "M5,4 L19,4 Q21,4 21,6 L21,16 Q21,18 19,18 L9,18 4,21 4,18 Q2,18 2,16 L2,6 Q2,4 5,4 M7,9 L17,9 M7,13 L14,13",
			"models" => "M12,3 L21,8 21,17 12,22 3,17 3,8 Z M3,8 L12,13 21,8 M12,13 L12,22 M7.5,5.5 L16.5,10.5",
			"memory" => "M4,4 Q8,2 12,5 Q16,2 20,4 L20,20 Q16,18 12,21 Q8,18 4,20 Z M12,5 L12,21 M7,8 L9,9 M15,9 L17,8 M7,12 L9,13 M15,13 L17,12",
			"settings" => "M3,6 L8,6 M12,6 L21,6 M3,12 L14,12 M18,12 L21,12 M3,18 L6,18 M10,18 L21,18 M12,6 A2,2 0 1 1 8,6 A2,2 0 1 1 12,6 M18,12 A2,2 0 1 1 14,12 A2,2 0 1 1 18,12 M10,18 A2,2 0 1 1 6,18 A2,2 0 1 1 10,18",
			"sparkles" => "M10,3 Q11,10 18,11 Q11,12 10,20 Q9,12 2,11 Q9,10 10,3 Z M20,2 L20,8 M17,5 L23,5 M20,17 L20,21 M18,19 L22,19",
			"cpu" => "M7,5 L17,5 Q19,5 19,7 L19,17 Q19,19 17,19 L7,19 Q5,19 5,17 L5,7 Q5,5 7,5 Z M9,9 L15,9 15,15 9,15 Z M9,2 L9,5 M15,2 L15,5 M9,19 L9,22 M15,19 L15,22 M2,9 L5,9 M2,15 L5,15 M19,9 L22,9 M19,15 L22,15",
			"server" => "M4,3 L20,3 20,10 4,10 Z M4,14 L20,14 20,21 4,21 Z M7,6.5 L8,6.5 M7,17.5 L8,17.5 M12,6.5 L17,6.5 M12,17.5 L17,17.5",
			"tool" => "M14,3 A6,6 0 0 0 7,10 L2,17 A3,3 0 0 0 7,22 L14,15 A6,6 0 0 0 21,8 L17,12 12,7 Z",
			"left" => "M15,5 L8,12 15,19",
			"close" => "M6,6 L18,18 M6,18 L18,6",
			"minimize" => "M5,12 L19,12",
			"power" => "M12,2 L12,12 M6,5 A9,9 0 1 0 18,5",
			_ => "M5,12 L19,12 M13,6 L19,12 13,18",
		};
		return new Viewbox
		{
			Width = size, Height = size,
			Child = new Canvas
			{
				Width = 24, Height = 24,
				Children = {new Avalonia.Controls.Shapes.Path
				{
					Data = Geometry.Parse(data), Stroke = brush ?? ChatPalette.Muted,
					StrokeThickness = 1.7, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
				}},
			},
		};
	}
}
