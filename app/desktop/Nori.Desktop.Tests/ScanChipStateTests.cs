using Nori.Desktop.Vision;

namespace Nori.Desktop.Tests;

public sealed class ScanChipStateTests
{
	[Fact]
	public void FiveScansExhaustChipAndCoolOneChargeEveryFiveMinutes()
	{
		ScanChipState chip = new();
		DateTimeOffset start = DateTimeOffset.UtcNow;
		Assert.Equal(5, chip.Charges(start));
		for (int i = 0; i < 5; i++) Assert.True(chip.Consume(start));
		Assert.False(chip.Consume(start));
		Assert.Equal(TimeSpan.FromMinutes(5), chip.NextChargeIn(start));
		Assert.Equal(0, chip.Charges(start.AddMinutes(4).AddSeconds(59)));
		Assert.Equal(1, chip.Charges(start.AddMinutes(5)));
		Assert.Equal(2, chip.Charges(start.AddMinutes(10)));
		Assert.Equal(5, chip.Charges(start.AddDays(1)));
		Assert.Equal(TimeSpan.Zero, chip.NextChargeIn(start.AddDays(1)));
		Assert.True(chip.Consume(start.AddDays(1)));
		Assert.Equal(TimeSpan.FromMinutes(5), chip.NextChargeIn(start.AddDays(1)));
	}

	[Fact]
	public void ScanningDuringCooldownDoesNotResetTheCoolingAnchor()
	{
		ScanChipState chip = new();
		DateTimeOffset start = DateTimeOffset.UtcNow;
		Assert.True(chip.Consume(start));
		Assert.True(chip.Consume(start.AddMinutes(3)));
		Assert.Equal(3, chip.Charges(start.AddMinutes(4)));
		Assert.Equal(4, chip.Charges(start.AddMinutes(5)));
		Assert.Equal(5, chip.Charges(start.AddMinutes(10)));
	}

	[Fact]
	public void ReadoutReplayDoesNotConsumeChargeAndRetainsNoScreenshot()
	{
		ScanChipState chip = new();
		Assert.False(chip.TryReplay("fingerprint", out _));
		chip.Remember("fingerprint", "分析结果");
		Assert.True(chip.TryReplay("fingerprint", out string text));
		Assert.Equal("分析结果", text);
		Assert.Equal(5, chip.Charges(DateTimeOffset.UtcNow));
		for (int i = 0; i < 32; i++) chip.Remember(i.ToString(), "读数");
		Assert.False(chip.TryReplay("fingerprint", out _));
		Assert.True(chip.TryReplay("31", out _));
	}
}
