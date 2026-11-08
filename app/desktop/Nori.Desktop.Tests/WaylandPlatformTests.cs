using Avalonia;
using Nori.Desktop.Live2D;
using Nori.Desktop.Platform;

namespace Nori.Desktop.Tests;

public class WaylandPlatformTests
{
	[Fact]
	public void Wayland结构与libwayland布局一致()
	{
		Assert.Equal(24, WaylandNative.MessageSize);
		Assert.Equal(40, WaylandNative.InterfaceSize);
		Assert.Equal(8, WaylandNative.ArgumentSize);
		Assert.Equal(16, WaylandNative.MethodsOffset);
		Assert.Equal(32, WaylandNative.EventsOffset);
	}

	[Fact]
	public void 输入区把模型网格映射到控件位置并保留嵌入聊天()
	{
		PetHitMask.Bounds mask = new(0, 0, 0, 0, true);
		Rect model = new(10.2, 20.4, 96, 128);
		Rect chat = new(1.2, 3.4, 10, 8);
		WaylandHitRegions.PixelRect[] regions = WaylandHitRegions.Compose(false, mask, model, [chat]);
		Assert.Equal(new WaylandHitRegions.PixelRect(10, 20, 2, 2), regions[0]);
		Assert.Equal(new WaylandHitRegions.PixelRect(1, 3, 11, 9), regions[1]);

		WaylandHitRegions.PixelRect[] through = WaylandHitRegions.Compose(true, mask, model, [chat]);
		Assert.Equal([new WaylandHitRegions.PixelRect(1, 3, 11, 9)], through);
		Assert.Empty(WaylandHitRegions.Compose(true, PetHitMask.Bounds.Empty, default, []));
	}

	[Fact]
	public void 表面尺寸不同时输入区按比例向外取整()
	{
		WaylandHitRegions.PixelRect[] source = [new(1, 3, 11, 9)];
		Assert.Same(source, WaylandHitRegions.FitSurface(source, 200, 100, 200, 100));
		WaylandHitRegions.PixelRect[] scaled = WaylandHitRegions.FitSurface(source, 200, 100, 400, 200);
		Assert.Equal(new WaylandHitRegions.PixelRect(2, 6, 22, 18), scaled[0]);
	}

	[Fact]
	public void 没有Wayland显示时置顶请求立即失败()
	{
		string? previous = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
		try
		{
			Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
			Assert.False(KdePlasmaTopmost.TryStart());
			Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", " ");
			Assert.False(KdePlasmaTopmost.TryStart());
		}
		finally
		{
			Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", previous);
		}
	}
}
