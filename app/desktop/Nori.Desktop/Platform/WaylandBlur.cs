using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Nori.Desktop.Platform;

/// <summary>普通窗口在合成器提供 org_kde_kwin_blur_manager 时请求整窗背景模糊。</summary>
internal static class WaylandBlur
{
	private const string ManagerName = "org_kde_kwin_blur_manager";
	private static readonly ConditionalWeakTable<Window, BlurLease> Leases = new();
	private static readonly object Gate = new();
	private static nint _managerInterface;
	private static nint _blurInterface;
	private static nint _manager;
	private static object? _connection;
	private static bool _globalsChecked;
	private static bool _globalMissing;

	internal static void Request(Window window, bool enabled, Action<bool> completed)
	{
		WaylandSurfacePoster.Result result = WaylandSurfacePoster.Post(window, surface =>
		{
			bool active = Apply(window, surface, enabled);
			Dispatcher.UIThread.Post(() => completed(active));
		}, static _ => { });
		if (result != WaylandSurfacePoster.Result.Posted) completed(false);
	}

	private static bool Apply(Window window, object surface, bool enabled)
	{
		object? wlSurface = WaylandSurfacePoster.Member(surface, "WlSurface");
		if (wlSurface is null || WaylandSurfacePoster.Disposed(wlSurface)) return false;
		nint surfaceHandle = WaylandNative.HandleOf(wlSurface);
		if (surfaceHandle == 0) return false;
		object? globals = WaylandSurfacePoster.Member(surface, "Globals");
		object? connection = WaylandSurfacePoster.IsMissing(globals) ? null : WaylandSurfacePoster.Member(globals!, "Connection");
		if (WaylandSurfacePoster.IsMissing(connection)) return false;
		lock (Gate)
		{
			BlurLease lease = Leases.GetOrCreateValue(window);
			if (!ReferenceEquals(lease.Connection, connection))
			{
				// 旧连接上的代理已经失效，不能再 release。
				lease.Proxy = 0;
				lease.Surface = 0;
				lease.Connection = connection;
			}
			ForgetManagerIfConnectionChanged(connection);
			if (!enabled)
			{
				Release(lease, surfaceHandle);
				return false;
			}
			if (lease.Proxy != 0 && lease.Surface == surfaceHandle) return true;
			if (lease.Proxy != 0) Release(lease, lease.Surface);
			if (!EnsureManager(surface, wlSurface, connection)) return false;
			nint blur = WaylandNative.Send(_manager, 0, _blurInterface, 1,
				new WaylandNative.Argument(),
				new WaylandNative.Argument { Pointer = surfaceHandle });
			if (blur == 0) return false;
			WaylandNative.Send(blur, 1, 0, 1, new WaylandNative.Argument());
			WaylandNative.Send(blur, 0, 0, 1);
			lease.Proxy = blur;
			lease.Surface = surfaceHandle;
			return true;
		}
	}

	private static void ForgetManagerIfConnectionChanged(object? connection)
	{
		if (ReferenceEquals(_connection, connection)) return;
		_connection = connection;
		_manager = 0;
		_globalsChecked = false;
		_globalMissing = false;
	}

	private static bool EnsureManager(object surface, object wlSurface, object? connection)
	{
		if (_manager != 0 && ReferenceEquals(_connection, connection)) return true;
		if (_globalMissing && ReferenceEquals(_connection, connection)) return false;
		object? globals = WaylandSurfacePoster.Member(surface, "Globals");
		object? registry = WaylandSurfacePoster.IsMissing(globals) ? null : WaylandSurfacePoster.Member(globals!, "Registry");
		if (WaylandSurfacePoster.IsMissing(globals) || WaylandSurfacePoster.IsMissing(registry)) return false;
		if (_globalsChecked) return false;
		_globalsChecked = true;
		if (!TryGlobal(globals!, out uint name, out uint version) || version < 1)
		{
			_globalMissing = true;
			return false;
		}
		nint registryHandle = WaylandNative.HandleOf(registry!);
		Assembly protocolAssembly = wlSurface.GetType().Assembly;
		nint surfaceInterface = WaylandNative.InterfaceOfType(protocolAssembly, "NWayland.Protocols.Wayland.WlSurface");
		if (surfaceInterface == 0) surfaceInterface = WaylandNative.InterfaceOf(wlSurface);
		nint regionInterface = WaylandNative.InterfaceOfType(protocolAssembly, "NWayland.Protocols.Wayland.WlRegion");
		if (registryHandle == 0 || surfaceInterface == 0 || regionInterface == 0)
		{
			_globalMissing = true;
			return false;
		}
		if (_managerInterface == 0)
		{
			_blurInterface = WaylandNative.DefineInterface("org_kde_kwin_blur", 1,
			[
				("commit", "", 0),
				("set_region", "?o", WaylandNative.Types(regionInterface)),
				("release", "", 0),
			], []);
			_managerInterface = WaylandNative.DefineInterface(ManagerName, 1,
			[
				("create", "no", WaylandNative.Types(_blurInterface, surfaceInterface)),
				("unset", "o", WaylandNative.Types(surfaceInterface)),
			], []);
		}
		_manager = WaylandNative.Send(registryHandle, 0, _managerInterface, 1,
			new WaylandNative.Argument { UInt = name },
			new WaylandNative.Argument { Pointer = ReadName(_managerInterface) },
			new WaylandNative.Argument { UInt = 1 },
			new WaylandNative.Argument());
		if (_manager == 0) _globalMissing = true;
		return _manager != 0;
	}

	private static void Release(BlurLease lease, nint surfaceHandle)
	{
		if (lease.Proxy == 0) return;
		if (_manager != 0 && surfaceHandle != 0)
			WaylandNative.Send(_manager, 1, 0, 1, new WaylandNative.Argument { Pointer = surfaceHandle });
		WaylandNative.Send(lease.Proxy, 2, 0, 1);
		lease.Proxy = 0;
		lease.Surface = 0;
	}

	private static bool TryGlobal(object globals, out uint name, out uint version)
	{
		name = 0;
		version = 0;
		FieldInfo? field = globals.GetType().GetField("_knownGlobals", BindingFlags.Instance | BindingFlags.NonPublic);
		if (field?.GetValue(globals) is not IDictionary globalsMap) return false;
		foreach (DictionaryEntry entry in globalsMap)
		{
			if (entry.Key is not string interfaceName || interfaceName != ManagerName) continue;
			if (entry.Value is not ValueTuple<uint, uint> global) return false;
			name = global.Item1;
			version = global.Item2;
			return true;
		}
		return false;
	}

	private static nint ReadName(nint interfacePointer)
	{
		WaylandNative.Interface iface = Marshal.PtrToStructure<WaylandNative.Interface>(interfacePointer);
		return iface.Name;
	}

	private sealed class BlurLease
	{
		internal object? Connection;
		internal nint Proxy;
		internal nint Surface;
	}
}
