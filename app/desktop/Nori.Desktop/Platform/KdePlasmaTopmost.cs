using System.Runtime.InteropServices;

namespace Nori.Desktop.Platform;

/// <summary>
/// 短连接绑定 org_kde_plasma_window_management，只把本进程、标题为 Nori 的窗口设为 KeepAbove。
/// 没有 WAYLAND_DISPLAY、协议或 pid 事件时立即放弃，不抛给界面。
/// </summary>
internal static class KdePlasmaTopmost
{
	private const string InterfaceName = "org_kde_plasma_window_management";
	private const uint KeepAbove = 0x10;
	private const int ProtocolVersion = 8;
	private static int _running;
	private static int _logged;

	internal static bool TryStart(Action<Exception>? onFailure = null)
	{
		if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return false;
		if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return true;
		try
		{
			Task.Run(() =>
			{
				try
				{
					Run();
				}
				catch (Exception exception)
				{
					if (Interlocked.Exchange(ref _logged, 1) == 0) onFailure?.Invoke(exception);
				}
				finally
				{
					Interlocked.Exchange(ref _running, 0);
				}
			});
			return true;
		}
		catch (Exception exception)
		{
			Interlocked.Exchange(ref _running, 0);
			if (Interlocked.Exchange(ref _logged, 1) == 0) onFailure?.Invoke(exception);
			return false;
		}
	}

	private static void Run()
	{
		using Session session = new();
		if (!session.Connect()) return;
		DateTime deadline = DateTime.UtcNow.AddSeconds(2);
		while (DateTime.UtcNow < deadline && !session.Applied)
		{
			if (!session.Dispatch(50)) break;
		}
	}

	private sealed class Session : IDisposable
	{
		private readonly Dictionary<nint, WindowState> _windows = [];
		private readonly RegistryGlobal _onGlobal;
		private readonly RegistryRemove _onRemove;
		private readonly ManagerShowDesktop _onShowDesktop;
		private readonly ManagerWindow _onWindow;
		private readonly TitleChanged _onTitle;
		private readonly StringEvent _onAppId;
		private readonly StateChanged _onState;
		private readonly DesktopChanged _onDesktop;
		private readonly StringEvent _onIcon;
		private readonly SimpleEvent _onUnmapped;
		private readonly SimpleEvent _onInitial;
		private readonly ParentChanged _onParent;
		private readonly GeometryChanged _onGeometry;
		private readonly SimpleEvent _onIconChanged;
		private readonly PidChanged _onPid;
		private readonly StringEvent _onDesktopEntered;
		private readonly StringEvent _onDesktopLeft;
		private nint _display;
		private nint _registry;
		private nint _manager;
		private nint _registryInterface;
		private nint _managerInterface;
		private nint _windowInterface;
		private nint _registryListener;
		private nint _managerListener;
		private nint _windowListener;
		private GCHandle _handle;
		private uint _processId;
		private uint _globalName;
		private bool _sawGlobal;

		internal bool Applied { get; private set; }

		internal Session()
		{
			_onGlobal = OnGlobal;
			_onRemove = (_, _, _) => { };
			_onShowDesktop = (_, _, _) => { };
			_onWindow = OnWindow;
			_onTitle = OnTitle;
			_onAppId = (_, _, _) => { };
			_onState = (_, _, _) => { };
			_onDesktop = (_, _, _) => { };
			_onIcon = (_, _, _) => { };
			_onUnmapped = OnUnmapped;
			_onInitial = OnInitial;
			_onParent = (_, _, _) => { };
			_onGeometry = (_, _, _, _, _, _) => { };
			_onIconChanged = (_, _) => { };
			_onPid = OnPid;
			_onDesktopEntered = (_, _, _) => { };
			_onDesktopLeft = (_, _, _) => { };
			_processId = (uint)Environment.ProcessId;
		}

		internal bool Connect()
		{
			_display = WaylandNative.DisplayConnect(0);
			if (_display == 0) return false;
			_registryInterface = ProtocolInterfaces.Registry();
			_registry = WaylandNative.Send(_display, 1, _registryInterface, 1, new WaylandNative.Argument());
			if (_registry == 0) return false;
			_handle = GCHandle.Alloc(this);
			_registryListener = Listener(_onGlobal, _onRemove);
			if (WaylandNative.AddListener(_registry, _registryListener, GCHandle.ToIntPtr(_handle)) != 0) return false;
			if (WaylandNative.DisplayRoundtrip(_display) < 0 || !_sawGlobal) return false;
			_windowInterface = ProtocolInterfaces.Window();
			_managerInterface = ProtocolInterfaces.Manager(_windowInterface);
			_manager = WaylandNative.Send(_registry, 0, _managerInterface, ProtocolVersion,
				new WaylandNative.Argument { UInt = _globalName },
				new WaylandNative.Argument { Pointer = ReadInterfaceName(_managerInterface) },
				new WaylandNative.Argument { UInt = ProtocolVersion },
				new WaylandNative.Argument());
			if (_manager == 0) return false;
			_windowListener = WindowListener();
			_managerListener = Listener(_onShowDesktop, _onWindow);
			if (WaylandNative.AddListener(_manager, _managerListener, GCHandle.ToIntPtr(_handle)) != 0) return false;
			return WaylandNative.DisplayRoundtrip(_display) >= 0;
		}

		internal bool Dispatch(int timeoutMs)
		{
			if (_display == 0) return false;
			while (WaylandNative.DisplayPrepareRead(_display) != 0)
			{
				if (WaylandNative.DisplayDispatchPending(_display) < 0) return false;
			}
			WaylandNative.DisplayFlush(_display);
			WaylandNative.PollFd poll = new() { Fd = WaylandNative.DisplayGetFd(_display), Events = WaylandNative.PollIn };
			int ready = WaylandNative.Poll(ref poll, 1, timeoutMs);
			if (ready > 0) WaylandNative.DisplayReadEvents(_display);
			else WaylandNative.DisplayCancelRead(_display);
			return ready >= 0 && WaylandNative.DisplayDispatchPending(_display) >= 0;
		}

		public void Dispose()
		{
			if (_display != 0) WaylandNative.DisplayDisconnect(_display);
			_display = 0;
			if (_handle.IsAllocated) _handle.Free();
			Free(_registryListener);
			Free(_managerListener);
			Free(_windowListener);
		}

		private void OnGlobal(nint data, nint registry, uint name, nint interfaceName, uint version)
		{
			if (_sawGlobal || Marshal.PtrToStringUTF8(interfaceName) != InterfaceName || version < ProtocolVersion) return;
			_sawGlobal = true;
			_globalName = name;
		}

		private static nint ReadInterfaceName(nint interfacePointer)
		{
			WaylandNative.Interface iface = Marshal.PtrToStructure<WaylandNative.Interface>(interfacePointer);
			return iface.Name;
		}

		private void OnWindow(nint data, nint manager, uint id)
		{
			if (_windowInterface == 0 || _windowListener == 0) return;
			nint window = WaylandNative.Send(manager, 1, _windowInterface, ProtocolVersion,
				new WaylandNative.Argument(),
				new WaylandNative.Argument { UInt = id });
			if (window == 0) return;
			WaylandNative.AddListener(window, _windowListener, GCHandle.ToIntPtr(_handle));
			_windows[window] = new WindowState();
		}

		private void OnTitle(nint data, nint window, nint title)
		{
			if (!_windows.TryGetValue(window, out WindowState? state)) return;
			state.Title = Marshal.PtrToStringUTF8(title);
			TryKeepAbove(window, state);
		}

		private void OnPid(nint data, nint window, uint pid)
		{
			if (!_windows.TryGetValue(window, out WindowState? state)) return;
			state.Pid = pid;
			state.HasPid = true;
			TryKeepAbove(window, state);
		}

		private void OnInitial(nint data, nint window)
		{
			if (!_windows.TryGetValue(window, out WindowState? state)) return;
			state.Initial = true;
			TryKeepAbove(window, state);
		}

		private void OnUnmapped(nint data, nint window) => _windows.Remove(window);

		private void TryKeepAbove(nint window, WindowState state)
		{
			if (Applied || !state.Initial || !state.HasPid || state.Pid != _processId || state.Title != "Nori") return;
			WaylandNative.Send(window, 0, 0, ProtocolVersion,
				new WaylandNative.Argument { UInt = KeepAbove },
				new WaylandNative.Argument { UInt = KeepAbove });
			Applied = true;
		}

		private nint WindowListener() => Listener(
			_onTitle, _onAppId, _onState, _onDesktop, _onIcon, _onUnmapped, _onInitial, _onParent,
			_onGeometry, _onIconChanged, _onPid, _onDesktopEntered, _onDesktopLeft);

		private static nint Listener(params Delegate[] callbacks)
		{
			nint pointer = Marshal.AllocHGlobal(IntPtr.Size * callbacks.Length);
			for (int index = 0; index < callbacks.Length; index++)
				Marshal.WriteIntPtr(pointer, index * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(callbacks[index]));
			return pointer;
		}

		private static void Free(nint pointer)
		{
			if (pointer != 0) Marshal.FreeHGlobal(pointer);
		}
	}

	private sealed class WindowState
	{
		internal string? Title;
		internal uint Pid;
		internal bool HasPid;
		internal bool Initial;
	}

	private static class ProtocolInterfaces
	{
		private static nint _registry;
		private static nint _window;
		private static nint _manager;

		internal static nint Registry() => _registry != 0 ? _registry : _registry = WaylandNative.DefineInterface("wl_registry", 1,
			[("bind", "usun", WaylandNative.Types(0, 0, 0, 0))],
			[
				("global", "usu", WaylandNative.Types(0, 0, 0)),
				("global_remove", "u", WaylandNative.Types(0)),
			]);

		internal static nint Manager(nint windowInterface) => _manager != 0 ? _manager : _manager = WaylandNative.DefineInterface(InterfaceName, ProtocolVersion,
			[
				("show_desktop", "u", WaylandNative.Types(0)),
				("get_window", "nu", WaylandNative.Types(windowInterface, 0)),
			],
			[
				("show_desktop_changed", "u", WaylandNative.Types(0)),
				("window", "u", WaylandNative.Types(0)),
			]);

		internal static nint Window() => _window != 0 ? _window : _window = WaylandNative.DefineInterface("org_kde_plasma_window", ProtocolVersion,
			[("set_state", "uu", WaylandNative.Types(0, 0))],
			[
				("title_changed", "s", WaylandNative.Types(0)),
				("app_id_changed", "s", WaylandNative.Types(0)),
				("state_changed", "u", WaylandNative.Types(0)),
				("virtual_desktop_changed", "i", WaylandNative.Types(0)),
				("themed_icon_name_changed", "s", WaylandNative.Types(0)),
				("unmapped", "", 0),
				("initial_state", "", 0),
				("parent_window", "?o", WaylandNative.Types(0)),
				("geometry", "iiuu", WaylandNative.Types(0, 0, 0, 0)),
				("icon_changed", "", 0),
				("pid_changed", "u", WaylandNative.Types(0)),
				("virtual_desktop_entered", "s", WaylandNative.Types(0)),
				("virtual_desktop_left", "s", WaylandNative.Types(0)),
			]);
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void RegistryGlobal(nint data, nint registry, uint name, nint interfaceName, uint version);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void RegistryRemove(nint data, nint registry, uint name);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void ManagerShowDesktop(nint data, nint manager, uint state);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void ManagerWindow(nint data, nint manager, uint id);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void TitleChanged(nint data, nint window, nint title);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void StringEvent(nint data, nint window, nint text);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void StateChanged(nint data, nint window, uint flags);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void DesktopChanged(nint data, nint window, int number);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void SimpleEvent(nint data, nint window);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void ParentChanged(nint data, nint window, nint parent);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void GeometryChanged(nint data, nint window, int x, int y, uint width, uint height);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void PidChanged(nint data, nint window, uint pid);
}
