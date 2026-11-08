using System.Reflection;
using Avalonia.Controls;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Platform;

/// <summary>用 wl_surface.set_input_region 设置点击区域。空区域整窗穿透，null 恢复整窗可点，不自己 commit。</summary>
internal static class WaylandInputRegion
{
	private static int _observedSurfaceWidth;
	private static int _observedSurfaceHeight;
	private static int _failed;

	internal static int ObservedSurfaceWidth => Volatile.Read(ref _observedSurfaceWidth);
	internal static int ObservedSurfaceHeight => Volatile.Read(ref _observedSurfaceHeight);
	internal static bool Failed => Volatile.Read(ref _failed) != 0;
	internal static Exception? Failure { get; private set; }

	internal static WaylandSurfacePoster.Result Apply(Window window, WaylandHitRegions.PixelRect[] regions, int logicalWidth, int logicalHeight)
	{
		if (Failed) return WaylandSurfacePoster.Result.Unsupported;
		WaylandHitRegions.PixelRect[] snapshot = regions.ToArray();
		return WaylandSurfacePoster.Post(window, surface =>
		{
			try
			{
				(int surfaceWidth, int surfaceHeight) = ReadGeometry(surface);
				if (surfaceWidth > 0 && surfaceHeight > 0)
				{
					Volatile.Write(ref _observedSurfaceWidth, surfaceWidth);
					Volatile.Write(ref _observedSurfaceHeight, surfaceHeight);
				}
				WaylandHitRegions.PixelRect[] fitted = WaylandHitRegions.FitSurface(snapshot, logicalWidth, logicalHeight, surfaceWidth, surfaceHeight);
				SetRegion(surface, fitted);
			}
			catch (Exception exception) when (WaylandSurfacePoster.IsSurfaceGone(exception))
			{
				throw;
			}
			catch (Exception)
			{
				try { SetInfinite(surface); }
				catch (Exception restore) when (WaylandSurfacePoster.IsSurfaceGone(restore)) { throw; }
				catch
				{
					// 恢复整窗可点失败时仍把这次异常交给调用方，停止后续同步。
				}
				throw;
			}
		}, exception =>
		{
			Failure = exception;
			Volatile.Write(ref _failed, 1);
		});
	}

	private static (int Width, int Height) ReadGeometry(object surface)
	{
		object? geometry = WaylandSurfacePoster.Member(surface, "LastWindowGeometry");
		if (WaylandSurfacePoster.IsMissing(geometry)) return (0, 0);
		Type type = geometry!.GetType();
		FieldInfo? width = type.GetField("Item3");
		FieldInfo? height = type.GetField("Item4");
		if (width?.GetValue(geometry) is not int surfaceWidth || height?.GetValue(geometry) is not int surfaceHeight)
			return (0, 0);
		return (surfaceWidth, surfaceHeight);
	}

	private static void SetRegion(object surface, WaylandHitRegions.PixelRect[] regions)
	{
		object region = CreateRegion(surface);
		MethodInfo? add = region.GetType().GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
		if (add is null) throw new MissingMethodException(region.GetType().FullName, "Add");
		foreach (WaylandHitRegions.PixelRect rect in regions)
		{
			if (rect.Width <= 0 || rect.Height <= 0) continue;
			add.Invoke(region, [rect.X, rect.Y, rect.Width, rect.Height]);
		}
		InputRegion(surface, region);
		region.GetType().GetMethod("Destroy", BindingFlags.Instance | BindingFlags.Public)?.Invoke(region, null);
	}

	/// <summary>null 输入区表示无限，点击恢复到整窗。</summary>
	private static void SetInfinite(object surface) => InputRegion(surface, null);

	private static object CreateRegion(object surface)
	{
		object? compositor = Compositor(surface);
		MethodInfo? create = compositor?.GetType().GetMethod("CreateRegion", BindingFlags.Instance | BindingFlags.Public);
		if (compositor is null || create is null) throw new MissingMethodException("WlCompositor", "CreateRegion");
		return create.Invoke(compositor, [null, null]) ?? throw new InvalidOperationException("CreateRegion 返回空区域");
	}

	private static void InputRegion(object surface, object? region)
	{
		object? wlSurface = WaylandSurfacePoster.Member(surface, "WlSurface");
		if (WaylandSurfacePoster.Disposed(wlSurface)) throw new ObjectDisposedException("WlSurface");
		MethodInfo? set = wlSurface!.GetType().GetMethod("SetInputRegion", BindingFlags.Instance | BindingFlags.Public);
		if (set is null) throw new MissingMethodException(wlSurface.GetType().FullName, "SetInputRegion");
		set.Invoke(wlSurface, [region]);
	}

	private static object? Compositor(object surface)
	{
		object? globals = WaylandSurfacePoster.Member(surface, "Globals");
		if (WaylandSurfacePoster.IsMissing(globals)) return null;
		object? compositor = WaylandSurfacePoster.Member(globals!, "WlCompositor");
		return WaylandSurfacePoster.IsMissing(compositor) ? null : compositor;
	}
}
