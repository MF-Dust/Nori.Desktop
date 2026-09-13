using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Nori.Core.Expression;

namespace Nori.Desktop.Expression;

/// <summary>
/// 壁纸随情绪换成一张渐变图。
///
/// 现画而不是换用户自己的图：预置素材要为每个情绪准备一张，加一个情绪就补一套；而且渐变
/// 能连续表达强度，预置的图做不到。
///
/// 侵入等级 Global：默认关，改之前备份原壁纸路径，关掉时还回去。
/// </summary>
public sealed class WallpaperChannel : IExpressionChannel
{
	/// <summary>设置项键名。</summary>
	public const string ChannelKey = "expression_wallpaper";

	/// <summary>生成的壁纸尺寸。渐变图按此编码后不到百 KB。</summary>
	public const int Width = 1920;

	/// <summary>生成的壁纸高度。</summary>
	public const int Height = 1080;

	private readonly IDesktopAppearance _appearance;
	private readonly DesktopStateBackup _backup;
	private readonly string _outputPath;

	/// <summary>创建通道。</summary>
	/// <param name="appearance">桌面外观读写。</param>
	/// <param name="backup">原值备份。</param>
	/// <param name="outputPath">生成的壁纸写到哪。</param>
	public WallpaperChannel(IDesktopAppearance appearance, DesktopStateBackup backup, string outputPath)
	{
		ArgumentNullException.ThrowIfNull(appearance);
		ArgumentNullException.ThrowIfNull(backup);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
		_appearance = appearance;
		_backup = backup;
		_outputPath = outputPath;
	}

	/// <inheritdoc />
	public string Key => ChannelKey;

	/// <inheritdoc />
	public Intrusiveness Level => Intrusiveness.Global;

	/// <inheritdoc />
	public bool IsAvailable => _appearance.IsAvailable;

	/// <inheritdoc />
	public Task ApplyAsync(ExpressionPalette palette, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(palette);
		if (!_appearance.IsAvailable) return Task.CompletedTask;

		_backup.Remember(ChannelKey, _appearance.GetWallpaper());

		Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
		Write(palette, _outputPath);

		// 每次写同一个路径：Windows 按路径缓存壁纸，换内容不换路径时要靠 SPI_SETDESKWALLPAPER
		// 的通知触发重读，这一步在 SetWallpaper 里做了。
		_appearance.SetWallpaper(_outputPath);
		return Task.CompletedTask;
	}

	/// <summary>还原用户原来的壁纸。原值为空（用户本来就是纯色背景）时不动。</summary>
	public void Restore()
	{
		if (_backup.Original(ChannelKey) is not {Length: > 0} saved) return;

		if (File.Exists(saved)) _appearance.SetWallpaper(saved);
		_backup.Forget(ChannelKey);
	}

	/// <summary>画一张从主色到辅色的对角渐变并存成 JPEG。</summary>
	internal static void Write(ExpressionPalette palette, string path)
	{
		(Color primary, Color accent) = (
			ExpressionColors.Parse(palette.Primary), ExpressionColors.Parse(palette.Accent));

		using RenderTargetBitmap bitmap = new(new PixelSize(Width, Height), new Vector(96, 96));
		using (DrawingContext context = bitmap.CreateDrawingContext())
		{
			context.FillRectangle(
				new LinearGradientBrush
				{
					StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
					EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
					GradientStops =
					{
						new GradientStop(primary, 0),
						new GradientStop(accent, 1),
					},
				},
				new Rect(0, 0, Width, Height));
		}

		using FileStream file = File.Create(path);
		bitmap.Save(file, new JpegBitmapEncoderOptions {Quality = 90});
	}
}
