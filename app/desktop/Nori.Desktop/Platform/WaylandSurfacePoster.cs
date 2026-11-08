using System.Reflection;
using Avalonia.Controls;

namespace Nori.Desktop.Platform;

/// <summary>把表面操作送到 Avalonia 12.1.1 的 Wayland 线程，避免在 UI 线程上碰 wl_surface。</summary>
internal static class WaylandSurfacePoster
{
	private static readonly object Missing = new();

	internal enum Result
	{
		Posted,
		NotReady,
		Unsupported,
	}

	/// <summary>workerAction 在 Wayland 线程执行，参数是 worker 侧表面对象。失败只交给这次调用，不连坐其他能力。</summary>
	internal static Result Post(Window window, Action<object> workerAction, Action<Exception>? onFailure = null)
	{
		if (!TryResolve(window, out object? client, out object? surface) || client is null || surface is null)
			return Result.NotReady;
		MethodInfo? post = client.GetType().GetMethod("PostWithCommit", BindingFlags.Instance | BindingFlags.Public);
		if (post is null) return Result.Unsupported;
		Action action = () =>
		{
			try
			{
				if (Disposed(Member(surface, "WlSurface"))) return;
				workerAction(surface);
			}
			catch (Exception exception) when (IsSurfaceGone(exception))
			{
				// 合成器重连后表面会重建，下一轮再用新的 wl_surface。
			}
			catch (Exception exception)
			{
				onFailure?.Invoke(Unwrap(exception));
			}
		};
		try
		{
			post.Invoke(client, [action]);
			return Result.Posted;
		}
		catch (Exception exception) when (IsSurfaceGone(exception))
		{
			return Result.NotReady;
		}
		catch (Exception)
		{
			return Result.Unsupported;
		}
	}

	internal static bool IsMissing(object? value) => value is null || ReferenceEquals(value, Missing);

	internal static bool IsSurfaceGone(Exception exception)
	{
		Exception current = Unwrap(exception);
		return current is ObjectDisposedException;
	}

	internal static nint SurfaceHandle(Window window)
	{
		if (!TryResolve(window, out _, out object? surface) || surface is null) return 0;
		object? wlSurface = Member(surface, "WlSurface");
		if (Disposed(wlSurface)) return 0;
		return wlSurface is null ? 0 : WaylandNative.HandleOf(wlSurface);
	}

	internal static object? Connection(Window window)
	{
		if (!TryResolve(window, out _, out object? surface) || surface is null) return null;
		object? globals = Member(surface, "Globals");
		if (IsMissing(globals)) return null;
		object? connection = Member(globals!, "Connection");
		return IsMissing(connection) ? null : connection;
	}

	private static bool TryResolve(Window window, out object? client, out object? surface)
	{
		client = null;
		surface = null;
		object? impl = Member(window, "PlatformImpl");
		if (impl is null || impl == Missing) return false;
		if (impl.GetType().FullName?.Contains("Wayland", StringComparison.Ordinal) != true) return false;
		object? proxy = Member(impl, "SurfaceProxy");
		if (proxy is null || proxy == Missing) return false;
		object? target = Member(proxy, "ProxyTarget");
		if (target is null || target == Missing || Disposed(Member(target, "WlSurface"))) return false;
		object? worker = Member(impl, "Client");
		if (worker is null || worker == Missing) return false;
		client = worker;
		surface = target;
		return true;
	}

	internal static object? Member(object target, string name)
	{
		for (Type? type = target.GetType(); type is not null; type = type.BaseType)
		{
			PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
			if (property is not null) return property.GetValue(target);
		}
		return Missing;
	}

	internal static bool Disposed(object? proxy)
	{
		if (proxy is null || proxy == Missing) return true;
		PropertyInfo? property = proxy.GetType().GetProperty("IsDisposed", BindingFlags.Instance | BindingFlags.Public);
		return property?.GetValue(proxy) is true;
	}

	private static Exception Unwrap(Exception exception)
	{
		Exception current = exception;
		while (current is TargetInvocationException { InnerException: { } inner }) current = inner;
		return current;
	}
}
