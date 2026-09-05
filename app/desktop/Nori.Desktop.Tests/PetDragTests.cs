using Avalonia;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Tests;

public sealed class PetDragTests
{
	[Fact]
	public void PhysicalDistanceUsesScaledThreshold()
	{
		var start = new PixelPoint(100, 200);

		Assert.False(PetWindow.HasExceededDragThreshold(start, new PixelPoint(103, 200), 1.0));
		Assert.True(PetWindow.HasExceededDragThreshold(start, new PixelPoint(104, 200), 1.0));
		Assert.False(PetWindow.HasExceededDragThreshold(start, new PixelPoint(107, 200), 2.0));
		Assert.True(PetWindow.HasExceededDragThreshold(start, new PixelPoint(108, 200), 2.0));
	}

	[Fact]
	public void PhysicalDistanceUsesEuclideanLength()
	{
		var start = new PixelPoint(100, 200);

		Assert.False(PetWindow.HasExceededDragThreshold(start, new PixelPoint(102, 202), 1.0));
		Assert.True(PetWindow.HasExceededDragThreshold(start, new PixelPoint(103, 203), 1.0));
	}
}
