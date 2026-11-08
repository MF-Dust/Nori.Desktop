using System.Reflection;
using Avalonia.Controls;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Platform;

/// <summary>用 wl_surface.set_input_region 设置点击区域。空区域整窗穿透，不自己 commit。</summary>
internal static class WaylandInputRegion
{
	private static int _observedSurfaceWidth;
	private static int _observedSurfaceHeight;

	internal static int ObservedSurfaceWidth => Volatile.Read(ref _observedSurfaceWidth);
	internal static int ObservedSurfaceHeight => Volatile.Read(ref _observedSurfaceHeight);

	internal static WaylandSurfacePoster.Result Apply(Window window, WaylandHitRegions.PixelRect[] regions, int logicalWidth, int logicalHeight)
	{
		if (WaylandSurfacePoster.IsBroken) return WaylandSurfacePoster.Result.Unsupported;
		WaylandHitRegions.PixelRect[] snapshot = regions.ToArray();
		return WaylandSurfacePoster.Post(window, surface =>
		{
			(int surfaceWidth, int surfaceHeight) = ReadGeometry(surface);
			if (surfaceWidth > 0 && surfaceHeight > 0)
			{
				Volatile.Write(ref _observedSurfaceWidth, surfaceWidth);
				Volatile.Write(ref _observedSurfaceHeight, surfaceHeight);
			}
			WaylandHitRegions.PixelRect[] fitted = WaylandHitRegions.FitSurface(snapshot, logicalWidth, logicalHeight, surfaceWidth, surfaceHeight);
			SetRegion(surface, fitted);
		});
	}

	private static (int Width, int Height) ReadGeometry(object surface)
	{
		object? geometry = WaylandSurfacePoster.Member(surface, "LastWindowGeometry");
		if (geometry is null) return (0, 0);
		Type type = geometry.GetType();
		FieldInfo? width = type.GetField("Item3");
		FieldInfo? height = type.GetField("Item4");
		if (width?.GetValue(geometry) is not int surfaceWidth || height?.GetValue(geometry) is not int surfaceHeight)
			return (0, 0);
		return (surfaceWidth, surfaceHeight);
	}

	private static void SetRegion(object surface, WaylandHitRegions.PixelRect[] regions)
	{
		object? wlSurface = WaylandSurfacePoster.Member(surface, "WlSurface");
		object? globals = WaylandSurfacePoster.Member(surface, "Globals");
		object? compositor = globals is null ? null : WaylandSurfacePoster.Member(globals, "WlCompositor");
		if (wlSurface is null || compositor is null || WaylandSurfacePoster.Disposed(wlSurface)) return;
		MethodInfo? create = compositor.GetType().GetMethod("CreateRegion", BindingFlags.Instance | BindingFlags.Public);
		MethodInfo? set = wlSurface.GetType().GetMethod("SetInputRegion", BindingFlags.Instance | BindingFlags.Public);
		if (create is null || set is null) throw new MissingMethodException(wlSurface.GetType().FullName, "SetInputRegion");
		object region = create.Invoke(compositor, [null, null]) ?? throw new InvalidOperationException("CreateRegion 返回空区域");
		MethodInfo? add = region.GetType().GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
		if (add is null) throw new MissingMethodException(region.GetType().FullName, "Add");
		foreach (WaylandHitRegions.PixelRect rect in regions)
		{
			if (rect.Width <= 0 || rect.Height <= 0) continue;
			add.Invoke(region, [rect.X, rect.Y, rect.Width, rect.Height]);
		}
		set.Invoke(wlSurface, [region]);
		region.GetType().GetMethod("Destroy", BindingFlags.Instance | BindingFlags.Public)?.Invoke(region, null);
	}
}
