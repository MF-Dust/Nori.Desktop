using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Nori.Core.Expression;

namespace Nori.Desktop.Expression;

/// <summary>
/// 托盘图标随情绪变色。
///
/// 这是覆盖面最广的一条通道 —— 每个用户都有托盘，不需要任何硬件或第三方软件。而且它一直
/// 在你视野边缘，不占屏幕、不打断操作。
///
/// 侵入等级 Local：只是一个 16 像素的点。
/// </summary>
public sealed class TrayIconChannel : IExpressionChannel
{
	/// <summary>设置项键名。</summary>
	public const string ChannelKey = "expression_tray_icon";

	/// <summary>生成的图标边长。托盘会自行缩放，取 32 兼顾高 DPI。</summary>
	public const int IconSize = 32;

	private readonly Func<TrayIcon?> _tray;
	private readonly Action<Action> _onUi;

	/// <summary>创建通道。</summary>
	/// <param name="tray">取当前托盘图标；托盘不可用时为 null。</param>
	/// <param name="onUi">把操作调度到 UI 线程。</param>
	public TrayIconChannel(Func<TrayIcon?> tray, Action<Action> onUi)
	{
		ArgumentNullException.ThrowIfNull(tray);
		ArgumentNullException.ThrowIfNull(onUi);
		_tray = tray;
		_onUi = onUi;
	}

	/// <inheritdoc />
	public string Key => ChannelKey;

	/// <inheritdoc />
	public Intrusiveness Level => Intrusiveness.Local;

	/// <inheritdoc />
	public bool IsAvailable => _tray() is not null;

	/// <inheritdoc />
	public Task ApplyAsync(ExpressionPalette palette, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(palette);
		if (_tray() is not {} tray) return Task.CompletedTask;

		_onUi(() => tray.Icon = Render(palette));
		return Task.CompletedTask;
	}

	/// <summary>
	/// 图标的渐变两端。
	///
	/// 与绘制分开：绘制要 Avalonia 的渲染后端，在无头测试里跑不起来（这个仓库把需要真实
	/// Skia 画布的测试单独用环境变量门控了）。而「什么情绪画成什么颜色」是纯数据判断，
	/// 抽出来就能无条件测到。
	/// </summary>
	internal static (Color From, Color To) Gradient(ExpressionPalette palette)
	{
		ArgumentNullException.ThrowIfNull(palette);
		return (ExpressionColors.Parse(palette.Primary), ExpressionColors.Parse(palette.Accent));
	}

	/// <summary>
	/// 画一个主色到辅色的渐变圆点。
	///
	/// 现画而不是预置一套图标：情绪强度是连续量，预置的话每个情绪要几档图标，加一个情绪
	/// 就要补一套素材。
	/// </summary>
	internal static WindowIcon Render(ExpressionPalette palette)
	{
		(Color primary, Color accent) = Gradient(palette);

		using RenderTargetBitmap bitmap = new(new PixelSize(IconSize, IconSize), new Vector(96, 96));
		using (DrawingContext context = bitmap.CreateDrawingContext())
		{
			LinearGradientBrush brush = new()
			{
				StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
				EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
				GradientStops =
				{
					new GradientStop(primary, 0),
					new GradientStop(accent, 1),
				},
			};

			// 留一像素边距，避免缩放到 16 像素时边缘被切掉。
			context.DrawEllipse(brush, null, new Rect(1, 1, IconSize - 2, IconSize - 2));
		}

		using MemoryStream stream = new();
		bitmap.Save(stream, PngBitmapEncoderOptions.Default);
		stream.Position = 0;
		return new WindowIcon(stream);
	}
}
