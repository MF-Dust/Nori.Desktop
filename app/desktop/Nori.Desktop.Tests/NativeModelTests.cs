using Nori.Live2D;

namespace Nori.Desktop.Tests;

[Collection("Native settings")]
public sealed class NativeModelTests
{
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(4)]
	[InlineData(63)]
	[InlineData(64)]
	[InlineData(512)]
	public void 损坏输入在原生创建前明确失败(int size)
	{
		var bytes = new byte[size];
		if (size >= 4) "MOC3"u8.CopyTo(bytes);
		Assert.Throws<InvalidDataException>(() => new NativeModel(bytes));
	}
}
