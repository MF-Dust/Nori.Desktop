using System.Reflection;
using Avalonia.Controls;

namespace Nori.Desktop.Platform;

/// <summary>把表面操作送到 Avalonia 12.1.1 的 Wayland 线程，避免在 UI 线程上碰 wl_surface。</summary>
internal static class WaylandSurfacePoster
{
	private static readonly object Missing = new();
	private static int _broken;

	internal enum Result
	{
		Posted,
		NotReady,
		Unsupported,
	}

	internal static bool IsBroken => Volatile.Read(ref _broken) != 0;

	internal static Exception? Failure { get; private set; }

	/// <summary>workerAction 在 Wayland 线程执行，参数是 worker 侧表面对象。</summary>
	internal static Result Post(Window window, Action<object> workerAction)
	{
		if (IsBroken) return Result.Unsupported;
		if (!TryResolve(window, out object? client, out object? surface) || client is null || surface is null)
			return Result.NotReady;
		MethodInfo? post = client.GetType().GetMethod("PostWithCommit", BindingFlags.Instance | BindingFlags.Public);
		if (post is null)
		{
			MarkBroken(new MissingMethodException(client.GetType().FullName, "PostWithCommit"));
			return Result.Unsupported;
		}
		Action action = () =>
		{
			try
			{
				if (Disposed(Member(surface, "WlSurface"))) return;
				workerAction(surface);
			}
			catch (Exception exception)
			{
				MarkBroken(exception);
			}
		};
		try
		{
			post.Invoke(client, [action]);
			return Result.Posted;
		}
		catch (Exception exception)
		{
			MarkBroken(exception);
			return Result.Unsupported;
		}
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

	private static void MarkBroken(Exception exception)
	{
		Failure = exception;
		Volatile.Write(ref _broken, 1);
	}
}
