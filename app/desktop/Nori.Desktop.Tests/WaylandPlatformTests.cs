using System.Reflection;
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
	public void NWayland表面接口沿非公开基类属性取得原生指针()
	{
		Assembly nwayland = Assembly.Load("NWayland");
		Type surface = nwayland.GetType("NWayland.Protocols.Wayland.WlSurface")
			?? throw new InvalidOperationException("缺少 WlSurface");
		PropertyInfo? iface = WaylandNative.FindInstanceProperty(surface, "Interface");
		Assert.NotNull(iface);
		Assert.False(iface!.GetGetMethod(true)!.IsPublic);
		Assert.NotEqual(0, WaylandNative.InterfaceOfType(nwayland, "NWayland.Protocols.Wayland.WlSurface"));
		Assert.NotEqual(0, WaylandNative.InterfaceOfType(nwayland, "NWayland.Protocols.Wayland.WlRegion"));
	}

	[Fact]
	public void Plasma窗口管理只绑定版本17并覆盖该版本事件()
	{
		Assert.Equal(0, KdePlasmaTopmost.SelectBindVersion(8));
		Assert.Equal(0, KdePlasmaTopmost.SelectBindVersion(16));
		Assert.Equal(17, KdePlasmaTopmost.SelectBindVersion(17));
		Assert.Equal(17, KdePlasmaTopmost.SelectBindVersion(22));
		Assert.Equal(
			["show_desktop_changed", "window", "stacking_order_changed", "stacking_order_uuid_changed", "window_with_uuid", "stacking_order_changed_2"],
			KdePlasmaTopmost.ManagerEvents.Select(item => item.Name).ToArray());
		Assert.Equal("a", KdePlasmaTopmost.ManagerEvents[2].Signature);
		Assert.Equal("us", KdePlasmaTopmost.ManagerEvents[4].Signature);
		Assert.Equal(17, KdePlasmaTopmost.WindowEvents.Length);
		Assert.Equal("ss", KdePlasmaTopmost.WindowEvents[13].Signature);
		Assert.Equal("pid_changed", KdePlasmaTopmost.WindowEvents[10].Name);
	}

	[Fact]
	public void 置顶请求先刷新再等合成器确认()
	{
		int step = 0;
		Assert.True(KdePlasmaTopmost.ConfirmQueuedRequest(
			() => { Assert.Equal(0, step); step = 1; return 0; },
			() => { Assert.Equal(1, step); step = 2; return 0; }));
		Assert.Equal(2, step);
		Assert.False(KdePlasmaTopmost.ConfirmQueuedRequest(() => -1, () => throw new InvalidOperationException("不应在刷新失败后 roundtrip")));
		Assert.False(KdePlasmaTopmost.ConfirmQueuedRequest(() => 0, () => -1));
	}

	[Fact]
	public void 新连接复用旧表面指针时仍要重设输入区()
	{
		nint reused = 0x42;
		Assert.True(WaylandInputRegion.NeedsApply(1, reused, 2, reused, sameRequest: true));
		Assert.True(WaylandInputRegion.NeedsApply(1, reused, 1, 0x43, sameRequest: true));
		Assert.True(WaylandInputRegion.NeedsApply(0, reused, 1, reused, sameRequest: true));
		Assert.False(WaylandInputRegion.NeedsApply(1, reused, 1, reused, sameRequest: true));
		Assert.True(WaylandInputRegion.NeedsApply(1, reused, 1, reused, sameRequest: false));
	}

	[Fact]
	public void 表面销毁不算穿透永久失败且与模糊故障分开()
	{
		Assert.True(WaylandSurfacePoster.IsSurfaceGone(new ObjectDisposedException("WlSurface")));
		Assert.True(WaylandSurfacePoster.IsSurfaceGone(new TargetInvocationException(new ObjectDisposedException("WlSurface"))));
		Assert.False(WaylandSurfacePoster.IsSurfaceGone(new MissingMethodException("WlSurface", "SetInputRegion")));
		Assert.False(WaylandSurfacePoster.IsSurfaceGone(new TargetInvocationException(new MissingMethodException("WlSurface", "SetInputRegion"))));
		Assert.False(WaylandInputRegion.Failed);
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
