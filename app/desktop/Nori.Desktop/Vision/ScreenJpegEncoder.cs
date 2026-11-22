using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Nori.Core.Vision;

namespace Nori.Desktop.Vision;

internal static class ScreenJpegEncoder
{
	internal const int MaxWidth = 1280;
	internal const int JpegQuality = 75;

	internal static CapturedScreen EncodePng(byte[] png, string title, int? sourceWidth = null)
	{
		using MemoryStream source = new(png);
		// Windows 已知原图宽度，可直接按目标宽度解码，避免为大图保留一份全尺寸位图。
		using Bitmap bitmap = sourceWidth > MaxWidth
			? Bitmap.DecodeToWidth(source, MaxWidth, BitmapInterpolationMode.HighQuality)
			: new Bitmap(source);
		return Encode(bitmap, title);
	}

	internal static unsafe CapturedScreen EncodeBgra(byte[] bgra, int width, int height, string title)
	{
		fixed (byte* pixels = bgra)
		{
			using Bitmap bitmap = new(PixelFormat.Bgra8888, AlphaFormat.Opaque, (nint)pixels, new PixelSize(width, height), new Vector(96, 96), width * 4);
			return Encode(bitmap, title);
		}
	}

	private static CapturedScreen Encode(Bitmap source, string title)
	{
		PixelSize size = source.PixelSize;
		using Bitmap? scaled = size.Width > MaxWidth
			? source.CreateScaledBitmap(new PixelSize(MaxWidth, Math.Max(1, (int)Math.Round(size.Height * (double)MaxWidth / size.Width))),
				BitmapInterpolationMode.HighQuality)
			: null;
		Bitmap bitmap = scaled ?? source;
		using MemoryStream encoded = new();
		bitmap.Save(encoded, new JpegBitmapEncoderOptions { Quality = JpegQuality });
		return new CapturedScreen
		{
			Bytes = encoded.ToArray(), MimeType = "image/jpeg", Window = title,
			Width = bitmap.PixelSize.Width, Height = bitmap.PixelSize.Height,
		};
	}
}
