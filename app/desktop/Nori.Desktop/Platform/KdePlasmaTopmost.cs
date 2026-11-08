using System.Runtime.InteropServices;

namespace Nori.Desktop.Platform;

/// <summary>
/// 短连接绑定 org_kde_plasma_window_management。协议要求至少版本 17，因此只绑定 17，
/// 并接住该版本会发送的全部事件。只把本进程、标题为 Nori 的窗口设为 KeepAbove。
/// set_state 入队后先 flush 并 roundtrip，确认发出后才断开。
/// </summary>
internal static class KdePlasmaTopmost
{
	internal const int BoundProtocolVersion = 17;
	private const string InterfaceName = "org_kde_plasma_window_management";
	private const uint KeepAbove = 0x10;
	private static int _running;
	private static int _logged;

	internal static readonly (string Name, string Signature)[] ManagerEvents =
	[
		("show_desktop_changed", "u"),
		("window", "u"),
		("stacking_order_changed", "a"),
		("stacking_order_uuid_changed", "s"),
		("window_with_uuid", "us"),
		("stacking_order_changed_2", ""),
	];

	internal static readonly (string Name, string Signature)[] WindowEvents =
	[
		("title_changed", "s"),
		("app_id_changed", "s"),
		("state_changed", "u"),
		("virtual_desktop_changed", "i"),
		("themed_icon_name_changed", "s"),
		("unmapped", ""),
		("initial_state", ""),
		("parent_window", "?o"),
		("geometry", "iiuu"),
		("icon_changed", ""),
		("pid_changed", "u"),
		("virtual_desktop_entered", "s"),
		("virtual_desktop_left", "s"),
		("application_menu", "ss"),
		("activity_entered", "s"),
		("activity_left", "s"),
		("resource_name_changed", "s"),
	];

	/// <summary>广告版本低于 17 不绑定；更高版本也只绑定 17，避免收到未实现的事件。</summary>
	internal static int SelectBindVersion(uint advertised) => advertised < BoundProtocolVersion ? 0 : BoundProtocolVersion;

	/// <summary>先把已入队的请求写到套接字，再等合成器处理完这一轮。</summary>
	internal static bool ConfirmQueuedRequest(Func<int> flush, Func<int> roundtrip)
	{
		if (flush() < 0) return false;
		return roundtrip() >= 0;
	}

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
		while (DateTime.UtcNow < deadline && !session.Confirmed)
		{
			if (!session.Dispatch(50)) break;
			if (session.PendingSend && !session.ConfirmPending()) break;
		}
	}

	private sealed class Session : IDisposable
	{
		private readonly Dictionary<nint, WindowState> _windows = [];
		private readonly HashSet<uint> _seenIds = [];
		private readonly RegistryGlobal _onGlobal;
		private readonly RegistryRemove _onRemove;
		private readonly ManagerShowDesktop _onShowDesktop;
		private readonly ManagerWindow _onWindow;
		private readonly ArrayEvent _onStacking;
		private readonly StringEvent _onStackingUuids;
		private readonly WindowWithUuid _onWindowUuid;
		private readonly SimpleEvent _onStacking2;
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
		private readonly TwoStringEvent _onApplicationMenu;
		private readonly StringEvent _onActivityEntered;
		private readonly StringEvent _onActivityLeft;
		private readonly StringEvent _onResourceName;
		private nint _display;
		private nint _registry;
		private nint _manager;
		private nint _registryListener;
		private nint _managerListener;
		private nint _windowListener;
		private GCHandle _handle;
		private uint _processId;
		private uint _globalName;
		private bool _sawGlobal;

		internal bool PendingSend { get; private set; }
		internal bool Confirmed { get; private set; }

		internal Session()
		{
			_onGlobal = OnGlobal;
			_onRemove = (_, _, _) => { };
			_onShowDesktop = (_, _, _) => { };
			_onWindow = OnWindow;
			_onStacking = (_, _, _) => { };
			_onStackingUuids = (_, _, _) => { };
			_onWindowUuid = OnWindowUuid;
			_onStacking2 = (_, _) => { };
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
			_onApplicationMenu = (_, _, _, _) => { };
			_onActivityEntered = (_, _, _) => { };
			_onActivityLeft = (_, _, _) => { };
			_onResourceName = (_, _, _) => { };
			_processId = (uint)Environment.ProcessId;
		}

		internal bool Connect()
		{
			_display = WaylandNative.DisplayConnect(0);
			if (_display == 0) return false;
			nint registryInterface = ProtocolInterfaces.Registry();
			_registry = WaylandNative.Send(_display, 1, registryInterface, 1, new WaylandNative.Argument());
			if (_registry == 0) return false;
			_handle = GCHandle.Alloc(this);
			_registryListener = Listener(_onGlobal, _onRemove);
			if (WaylandNative.AddListener(_registry, _registryListener, GCHandle.ToIntPtr(_handle)) != 0) return false;
			if (WaylandNative.DisplayRoundtrip(_display) < 0 || !_sawGlobal) return false;
			nint windowInterface = ProtocolInterfaces.Window();
			nint managerInterface = ProtocolInterfaces.Manager(windowInterface);
			_manager = WaylandNative.Send(_registry, 0, managerInterface, BoundProtocolVersion,
				new WaylandNative.Argument { UInt = _globalName },
				new WaylandNative.Argument { Pointer = ReadInterfaceName(managerInterface) },
				new WaylandNative.Argument { UInt = BoundProtocolVersion },
				new WaylandNative.Argument());
			if (_manager == 0) return false;
			_windowListener = WindowListener();
			_managerListener = ManagerListener();
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

		internal bool ConfirmPending()
		{
			if (!PendingSend || _display == 0 || Confirmed) return Confirmed;
			bool confirmed = ConfirmQueuedRequest(() => WaylandNative.DisplayFlush(_display), () => WaylandNative.DisplayRoundtrip(_display));
			if (!confirmed) return false;
			PendingSend = false;
			Confirmed = true;
			return true;
		}

		public void Dispose()
		{
			if (_display != 0 && PendingSend && !Confirmed) ConfirmPending();
			// roundtrip 期间若又入队了请求，断开前再刷一次，避免 set_state 留在缓冲里。
			if (_display != 0) WaylandNative.DisplayFlush(_display);
			if (_display != 0) WaylandNative.DisplayDisconnect(_display);
			_display = 0;
			if (_handle.IsAllocated) _handle.Free();
			Free(_registryListener);
			Free(_managerListener);
			Free(_windowListener);
		}

		private void OnGlobal(nint data, nint registry, uint name, nint interfaceName, uint version)
		{
			if (_sawGlobal || Marshal.PtrToStringUTF8(interfaceName) != InterfaceName || SelectBindVersion(version) == 0) return;
			_sawGlobal = true;
			_globalName = name;
		}

		private static nint ReadInterfaceName(nint interfacePointer)
		{
			WaylandNative.Interface iface = Marshal.PtrToStructure<WaylandNative.Interface>(interfacePointer);
			return iface.Name;
		}

		private void OnWindow(nint data, nint manager, uint id) => TrackWindow(manager, id);

		private void OnWindowUuid(nint data, nint manager, uint id, nint uuid) => TrackWindow(manager, id);

		private void TrackWindow(nint manager, uint id)
		{
			if (_windowListener == 0 || !_seenIds.Add(id)) return;
			nint window = WaylandNative.Send(manager, 1, ProtocolInterfaces.Window(), BoundProtocolVersion,
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
			if (state.Sent || Confirmed || !state.Initial || !state.HasPid || state.Pid != _processId || state.Title != "Nori") return;
			WaylandNative.Send(window, 0, 0, BoundProtocolVersion,
				new WaylandNative.Argument { UInt = KeepAbove },
				new WaylandNative.Argument { UInt = KeepAbove });
			state.Sent = true;
			PendingSend = true;
		}

		private nint ManagerListener()
		{
			Delegate[] callbacks = [_onShowDesktop, _onWindow, _onStacking, _onStackingUuids, _onWindowUuid, _onStacking2];
			if (callbacks.Length != ManagerEvents.Length)
				throw new InvalidOperationException("plasma 窗口管理事件与监听数量不一致");
			return Listener(callbacks);
		}

		private nint WindowListener()
		{
			Delegate[] callbacks =
			[
				_onTitle, _onAppId, _onState, _onDesktop, _onIcon, _onUnmapped, _onInitial, _onParent,
				_onGeometry, _onIconChanged, _onPid, _onDesktopEntered, _onDesktopLeft, _onApplicationMenu,
				_onActivityEntered, _onActivityLeft, _onResourceName,
			];
			if (callbacks.Length != WindowEvents.Length)
				throw new InvalidOperationException("plasma 窗口事件与监听数量不一致");
			return Listener(callbacks);
		}

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
		internal bool Sent;
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

		internal static nint Manager(nint windowInterface) => _manager != 0 ? _manager : _manager = WaylandNative.DefineInterface(InterfaceName, BoundProtocolVersion,
			[
				("show_desktop", "u", WaylandNative.Types(0)),
				("get_window", "nu", WaylandNative.Types(windowInterface, 0)),
			],
			ManagerEvents.Select(item => (item.Name, item.Signature, TypesFor(item.Signature))).ToArray());

		internal static nint Window() => _window != 0 ? _window : _window = WaylandNative.DefineInterface("org_kde_plasma_window", BoundProtocolVersion,
			[("set_state", "uu", WaylandNative.Types(0, 0))],
			WindowEvents.Select(item => (item.Name, item.Signature, TypesFor(item.Signature))).ToArray());

		private static nint TypesFor(string signature)
		{
			int count = 0;
			for (int index = 0; index < signature.Length; index++)
			{
				if (signature[index] == '?') continue;
				count++;
			}
			if (count == 0) return 0;
			nint[] types = new nint[count];
			return WaylandNative.Types(types);
		}
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
	private delegate void WindowWithUuid(nint data, nint manager, uint id, nint uuid);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void ArrayEvent(nint data, nint proxy, nint array);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void TitleChanged(nint data, nint window, nint title);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void StringEvent(nint data, nint window, nint text);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void TwoStringEvent(nint data, nint window, nint first, nint second);

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
