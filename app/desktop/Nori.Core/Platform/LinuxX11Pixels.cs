using System.Numerics;

namespace Nori.Core.Platform;

internal static class LinuxX11Pixels
{
	internal static byte[] DecodeBgra(ReadOnlySpan<byte> source, int width, int height, int bytesPerLine,
		int bitsPerPixel, bool bigEndian, uint redMask, uint greenMask, uint blueMask)
	{
		if (bitsPerPixel is not (8 or 16 or 24 or 32) || redMask == 0 || greenMask == 0 || blueMask == 0)
			throw new InvalidOperationException("不支持该 X11 截图像素格式");

		int pixelBytes = bitsPerPixel / 8;
		Channel red = new(redMask), green = new(greenMask), blue = new(blueMask);
		byte[] bgra = new byte[checked(width * height * 4)];
		int destination = 0;
		for (int y = 0; y < height; y++)
		{
			ReadOnlySpan<byte> row = source.Slice(y * bytesPerLine, width * pixelBytes);
			for (int offset = 0; offset < row.Length; offset += pixelBytes)
			{
				uint pixel = 0;
				for (int i = 0; i < pixelBytes; i++)
					pixel |= (uint)row[offset + i] << (8 * (bigEndian ? pixelBytes - 1 - i : i));
				bgra[destination++] = blue.Read(pixel);
				bgra[destination++] = green.Read(pixel);
				bgra[destination++] = red.Read(pixel);
				bgra[destination++] = 255;
			}
		}
		return bgra;
	}

	private readonly struct Channel(uint mask)
	{
		private readonly int _shift = BitOperations.TrailingZeroCount(mask);
		private readonly uint _maximum = mask >> BitOperations.TrailingZeroCount(mask);

		internal byte Read(uint pixel)
		{
			uint value = (pixel & mask) >> _shift;
			return _maximum == 255 ? (byte)value : (byte)((value * 255UL + _maximum / 2) / _maximum);
		}
	}
}
