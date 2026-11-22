using Avalonia;
using Avalonia.Headless;
using Nori.Core.Vision;
using Nori.Desktop.Vision;
using SkiaSharp;

namespace Nori.Desktop.Tests;

[Collection("Native settings")]
public sealed class ScreenJpegEncoderTests
{
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
		.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

	private static readonly Lazy<HeadlessUnitTestSession> Session = new(() =>
		HeadlessUnitTestSession.StartNew(typeof(ScreenJpegEncoderTests), AvaloniaTestIsolationLevel.PerTest));

	[Theory]
	[InlineData(1600, 400, 1280, 320)]
	[InlineData(160, 40, 160, 40)]
	public Task Bgra截屏保留颜色且只缩小不放大(int width, int height, int expectedWidth, int expectedHeight) =>
		Session.Value.Dispatch(() =>
		{
			byte[] bgra = new byte[width * height * 4];
			for (int offset = 0; offset < bgra.Length; offset += 4)
			{
				bgra[offset] = 45;
				bgra[offset + 1] = 90;
				bgra[offset + 2] = 200;
				bgra[offset + 3] = 255;
			}
			CapturedScreen screen = ScreenJpegEncoder.EncodeBgra(bgra, width, height, "X11 窗口");
			AssertJpeg(screen, expectedWidth, expectedHeight, "X11 窗口");
			return true;
		}, CancellationToken.None);

	[Theory]
	[InlineData(null)]
	[InlineData(1600)]
	public Task Png截屏有无尺寸元数据都缩为同尺寸Jpeg(int? sourceWidth) =>
		Session.Value.Dispatch(() =>
		{
			using SKBitmap source = new(1600, 400);
			source.Erase(new SKColor(200, 90, 45));
			using SKData png = source.Encode(SKEncodedImageFormat.Png, 100);
			CapturedScreen screen = ScreenJpegEncoder.EncodePng(png.ToArray(), "选定画面", sourceWidth);
			AssertJpeg(screen, 1280, 320, "选定画面");
			return true;
		}, CancellationToken.None);

	private static void AssertJpeg(CapturedScreen screen, int width, int height, string title)
	{
		Assert.Equal("image/jpeg", screen.MimeType);
		Assert.Equal((width, height, title), (screen.Width, screen.Height, screen.Window));
		using SKCodec codec = SKCodec.Create(new MemoryStream(screen.Bytes));
		Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
		using SKBitmap jpeg = SKBitmap.Decode(codec);
		Assert.Equal((width, height), (jpeg.Width, jpeg.Height));
		SKColor pixel = jpeg.GetPixel(width / 2, height / 2);
		Assert.InRange(pixel.Red, 195, 205);
		Assert.InRange(pixel.Green, 85, 95);
		Assert.InRange(pixel.Blue, 40, 50);
	}
}
