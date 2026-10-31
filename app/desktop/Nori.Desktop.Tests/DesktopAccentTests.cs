using Nori.Core.Platform;

namespace Nori.Desktop.Tests;

public sealed class DesktopAccentTests
{
	[Fact]
	public void SDK强调色读数保留原有Windows备份格式()
	{
		if (!System.OperatingSystem.IsWindows()) return;
		Assert.Equal(0xDDCCBBAAu, Expression.WindowsDesktopAppearance.PackAccent(new DesktopAccent(0xAA, 0xBB, 0xCC, 0xDD)));
	}
}
