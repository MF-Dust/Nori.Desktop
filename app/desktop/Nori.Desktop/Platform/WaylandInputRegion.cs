using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Platform;

/// <summary>用 wl_surface.set_input_region 设置点击区域。空区域整窗穿透，null 恢复整窗可点，不自己 commit。</summary>
internal static class WaylandInputRegion
{
	private static int _failed;
	private static int _appliedConnection;
	private static nint _appliedHandle;
	private static int _appliedLogicalWidth;
	private static int _appliedLogicalHeight;
	private static int _appliedSurfaceWidth;
	private static int _appliedSurfaceHeight;
	private static WaylandHitRegions.PixelRect[]? _appliedRegions;
	private static object? _seenConnection;
	private static int _seenConnectionId;

	internal static bool Failed => Volatile.Read(ref _failed) != 0;
	internal static Exception? Failure { get; private set; }

	/// <summary>
	/// 连接代际不同就必须重设，即使新表面复用了旧的原生指针。
	/// 连接号为 0 表示还没有成功应用过。
	/// </summary>
	internal static bool NeedsApply(int previousConnection, nint previousHandle, int nextConnection, nint nextHandle, bool sameRequest) =>
		previousConnection == 0 || nextConnection == 0 || previousConnection != nextConnection || previousHandle != nextHandle || !sameRequest;

	/// <summary>在 Wayland 线程读取当前连接代际，再把代号交回 UI。没有连接时代号为 0。</summary>
	internal static WaylandSurfacePoster.Result Observe(Window window, Action<int> onEpoch)
	{
		int epoch = 0;
		return WaylandSurfacePoster.Post(window, surface =>
		{
			object? connection = ConnectionOf(surface);
			if (!WaylandSurfacePoster.IsMissing(connection)) epoch = ConnectionId(connection!);
		}, finished: () => Dispatcher.UIThread.Post(() => onEpoch(epoch)));
	}

	internal static WaylandSurfacePoster.Result Apply(Window window, WaylandHitRegions.PixelRect[] regions, int logicalWidth, int logicalHeight, Action? completed = null)
	{
		if (Failed) return WaylandSurfacePoster.Result.Unsupported;
		WaylandHitRegions.PixelRect[] snapshot = regions.ToArray();
		WaylandSurfacePoster.Result result = WaylandSurfacePoster.Post(window, surface =>
		{
			try
			{
				ApplyOnWorker(surface, snapshot, logicalWidth, logicalHeight);
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
		}, completed is null ? null : () => Dispatcher.UIThread.Post(completed));
		if (result != WaylandSurfacePoster.Result.Posted) completed?.Invoke();
		return result;
	}

	private static object? ConnectionOf(object surface)
	{
		object? globals = WaylandSurfacePoster.Member(surface, "Globals");
		if (WaylandSurfacePoster.IsMissing(globals)) return null;
		object? connection = WaylandSurfacePoster.Member(globals!, "Connection");
		return WaylandSurfacePoster.IsMissing(connection) ? null : connection;
	}

	private static nint HandleOf(object surface)
	{
		object? wlSurface = WaylandSurfacePoster.Member(surface, "WlSurface");
		if (WaylandSurfacePoster.Disposed(wlSurface)) throw new ObjectDisposedException("WlSurface");
		nint handle = WaylandNative.HandleOf(wlSurface!);
		if (handle == 0) throw new ObjectDisposedException("WlSurface");
		return handle;
	}

	private static void ApplyOnWorker(object surface, WaylandHitRegions.PixelRect[] regions, int logicalWidth, int logicalHeight)
	{
		object? connection = ConnectionOf(surface);
		if (WaylandSurfacePoster.IsMissing(connection)) throw new ObjectDisposedException("WaylandConnection");
		int connectionId = ConnectionId(connection!);
		nint handle = HandleOf(surface);
		(int surfaceWidth, int surfaceHeight) = ReadGeometry(surface);
		bool sameRequest = logicalWidth == _appliedLogicalWidth && logicalHeight == _appliedLogicalHeight
			&& surfaceWidth == _appliedSurfaceWidth && surfaceHeight == _appliedSurfaceHeight
			&& WaylandHitRegions.Same(_appliedRegions, regions);
		if (!NeedsApply(_appliedConnection, _appliedHandle, connectionId, handle, sameRequest)) return;
		WaylandHitRegions.PixelRect[] fitted = WaylandHitRegions.FitSurface(regions, logicalWidth, logicalHeight, surfaceWidth, surfaceHeight);
		SetRegion(surface, fitted);
		_appliedConnection = connectionId;
		_appliedHandle = handle;
		_appliedLogicalWidth = logicalWidth;
		_appliedLogicalHeight = logicalHeight;
		_appliedSurfaceWidth = surfaceWidth;
		_appliedSurfaceHeight = surfaceHeight;
		_appliedRegions = regions;
	}

	private static int ConnectionId(object connection)
	{
		if (ReferenceEquals(_seenConnection, connection)) return _seenConnectionId;
		_seenConnection = connection;
		return _seenConnectionId = _seenConnectionId + 1;
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
