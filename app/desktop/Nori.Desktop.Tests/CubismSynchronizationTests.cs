using Live2DCSharpSDK.Framework;

namespace Nori.Desktop.Tests;

public sealed class CubismSynchronizationTests
{
	[Fact]
	public void RunSynchronized把状态传给缓存委托()
	{
		int seen = 0;
		CubismFramework.RunSynchronized(7, value => seen = value);
		Assert.Equal(7, seen);
	}
}
