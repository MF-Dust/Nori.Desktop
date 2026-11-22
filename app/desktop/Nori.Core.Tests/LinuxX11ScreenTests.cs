using Nori.Core.Platform;

namespace Nori.Core.Tests;

public sealed class LinuxX11ScreenTests
{
	[Fact]
	public void RGB565按通道位数缩放颜色并跳过行填充()
	{
		byte[] bgra = LinuxX11Pixels.DecodeBgra([0x00, 0xFC, 0xFF, 0xFF, 0x1F, 0x00, 0xFF, 0xFF],
			1, 2, 4, 16, false, 0xF800, 0x07E0, 0x001F);
		Assert.Equal<byte>([0, 130, 255, 255, 255, 0, 0, 255], bgra);
	}

	[Theory]
	[InlineData(24)]
	[InlineData(32)]
	public void 大端像素按掩码而非固定通道顺序解码(int bitsPerPixel)
	{
		byte[] row = bitsPerPixel == 24 ? [10, 20, 240, 255] : [0, 10, 20, 240];
		byte[] bgra = LinuxX11Pixels.DecodeBgra(row, 1, 1, 4, bitsPerPixel, true, 0xFF, 0xFF00, 0xFF0000);
		Assert.Equal<byte>([10, 20, 240, 255], bgra);
	}

	[Fact]
	public void 三十位颜色不会截断且忽略未使用的高位()
	{
		byte[] bgra = LinuxX11Pixels.DecodeBgra([0x00, 0x02, 0xF0, 0xFF], 1, 1, 4, 32, false, 0x3FF00000, 0x000FFC00, 0x000003FF);
		Assert.Equal<byte>([128, 0, 255, 255], bgra);
	}

	[Fact]
	public void 不会将没有颜色掩码的索引色当成黑屏()
	{
		Assert.Throws<InvalidOperationException>(() => LinuxX11Pixels.DecodeBgra([1], 1, 1, 1, 8, false, 0, 0, 0));
	}

	[Theory]
	[InlineData(0, 640, 480, false)]
	[InlineData(1, 640, 480, false)]
	[InlineData(2, 0, 480, false)]
	[InlineData(2, 640, 0, false)]
	[InlineData(2, 640, 480, true)]
	public void 只枚举可显示而且有实际区域的窗口(int mapState, int width, int height, bool visible) =>
		Assert.Equal(visible, LinuxX11WindowProvider.IsVisibleWindow(mapState, width, height));
}
