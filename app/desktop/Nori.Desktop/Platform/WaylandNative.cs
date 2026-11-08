using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Nori.Desktop.Platform;

/// <summary>libwayland-client 的最小调用层。只在 Linux 上真正发起请求。</summary>
internal static class WaylandNative
{
	private const string Library = "libwayland-client.so.0";

	[StructLayout(LayoutKind.Sequential)]
	internal struct Message
	{
		public nint Name;
		public nint Signature;
		public nint Types;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct Interface
	{
		public nint Name;
		public int Version;
		public int MethodCount;
		public nint Methods;
		public int EventCount;
		public int Reserved;
		public nint Events;
	}

	[StructLayout(LayoutKind.Explicit, Size = 8)]
	internal struct Argument
	{
		[FieldOffset(0)] public int Int;
		[FieldOffset(0)] public uint UInt;
		[FieldOffset(0)] public nint Pointer;
	}

	internal static int MessageSize => Marshal.SizeOf<Message>();
	internal static int InterfaceSize => Marshal.SizeOf<Interface>();
	internal static int ArgumentSize => Marshal.SizeOf<Argument>();
	internal static int MethodsOffset => (int)Marshal.OffsetOf<Interface>(nameof(Interface.Methods));
	internal static int EventsOffset => (int)Marshal.OffsetOf<Interface>(nameof(Interface.Events));

	internal static nint Send(nint proxy, uint opcode, nint newInterface, uint version, params Argument[] args)
	{
		if (proxy == 0) return 0;
		if (args.Length == 0) return MarshalFlags(proxy, opcode, newInterface, version, 0, 0);
		int size = ArgumentSize;
		nint buffer = Marshal.AllocHGlobal(size * args.Length);
		try
		{
			for (int index = 0; index < args.Length; index++)
				Marshal.StructureToPtr(args[index], buffer + index * size, false);
			return MarshalFlags(proxy, opcode, newInterface, version, 0, buffer);
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	internal static nint HandleOf(object proxy)
	{
		PropertyInfo? property = proxy.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public);
		return property?.GetValue(proxy) is nint handle ? handle : 0;
	}

	/// <summary>沿基类查找实例属性。NWayland 的 <c>WlProxy.Interface</c> 是 internal，只查 public 会漏掉。</summary>
	internal static PropertyInfo? FindInstanceProperty(Type type, string name)
	{
		const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		for (Type? current = type; current is not null; current = current.BaseType)
		{
			PropertyInfo? property = current.GetProperty(name, flags);
			if (property is not null) return property;
		}
		return null;
	}

	internal static nint InterfaceOf(object proxy)
	{
		PropertyInfo? property = FindInstanceProperty(proxy.GetType(), "Interface");
		object? description = property?.GetValue(proxy);
		if (description is null) return 0;
		MethodInfo? native = description.GetType().GetMethod("GetNative", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		return Unbox(native?.Invoke(description, null));
	}

	internal static nint InterfaceOfType(Assembly assembly, string typeName)
	{
		Type? type = assembly.GetType(typeName);
		object? proxyType = type?.GetProperty("ProxyType", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
		if (proxyType is null) return 0;
		PropertyInfo? iface = proxyType.GetType().GetProperty("Interface", BindingFlags.Instance | BindingFlags.Public);
		object? description = iface?.GetValue(proxyType);
		if (description is null) return 0;
		MethodInfo? native = description.GetType().GetMethod("GetNative", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		return Unbox(native?.Invoke(description, null));
	}

	private static unsafe nint Unbox(object? pointer)
	{
		if (pointer is Pointer boxed) return (nint)Pointer.Unbox(boxed);
		return pointer is nint value ? value : 0;
	}

	internal static nint DefineInterface(string name, int version, (string Name, string Signature, nint Types)[] methods, (string Name, string Signature, nint Types)[] events)
	{
		nint methodArray = DefineMessages(methods);
		nint eventArray = DefineMessages(events);
		nint pointer = Marshal.AllocHGlobal(InterfaceSize);
		Interface iface = new()
		{
			Name = Utf8(name),
			Version = version,
			MethodCount = methods.Length,
			Methods = methodArray,
			EventCount = events.Length,
			Events = eventArray,
		};
		Marshal.StructureToPtr(iface, pointer, false);
		return pointer;
	}

	internal static nint Types(params nint[] interfaces)
	{
		if (interfaces.Length == 0) return 0;
		nint pointer = Marshal.AllocHGlobal(IntPtr.Size * interfaces.Length);
		for (int index = 0; index < interfaces.Length; index++)
			Marshal.WriteIntPtr(pointer, index * IntPtr.Size, interfaces[index]);
		return pointer;
	}

	[DllImport(Library, EntryPoint = "wl_proxy_marshal_array_flags")]
	private static extern nint MarshalFlags(nint proxy, uint opcode, nint iface, uint version, uint flags, nint args);

	[DllImport(Library, EntryPoint = "wl_proxy_add_listener")]
	internal static extern int AddListener(nint proxy, nint implementation, nint data);

	[DllImport(Library, EntryPoint = "wl_display_connect")]
	internal static extern nint DisplayConnect(nint name);

	[DllImport(Library, EntryPoint = "wl_display_disconnect")]
	internal static extern void DisplayDisconnect(nint display);

	[DllImport(Library, EntryPoint = "wl_display_roundtrip")]
	internal static extern int DisplayRoundtrip(nint display);

	[DllImport(Library, EntryPoint = "wl_display_flush")]
	internal static extern int DisplayFlush(nint display);

	[DllImport(Library, EntryPoint = "wl_display_get_fd")]
	internal static extern int DisplayGetFd(nint display);

	[DllImport(Library, EntryPoint = "wl_display_prepare_read")]
	internal static extern int DisplayPrepareRead(nint display);

	[DllImport(Library, EntryPoint = "wl_display_cancel_read")]
	internal static extern void DisplayCancelRead(nint display);

	[DllImport(Library, EntryPoint = "wl_display_read_events")]
	internal static extern int DisplayReadEvents(nint display);

	[DllImport(Library, EntryPoint = "wl_display_dispatch_pending")]
	internal static extern int DisplayDispatchPending(nint display);

	[DllImport("libc", EntryPoint = "poll")]
	internal static extern int Poll(ref PollFd fd, nuint count, int timeout);

	[StructLayout(LayoutKind.Sequential)]
	internal struct PollFd
	{
		internal int Fd;
		internal short Events;
		internal short Revents;
	}

	internal const short PollIn = 0x001;

	private static nint DefineMessages((string Name, string Signature, nint Types)[] messages)
	{
		if (messages.Length == 0) return 0;
		int size = MessageSize;
		nint pointer = Marshal.AllocHGlobal(size * messages.Length);
		for (int index = 0; index < messages.Length; index++)
		{
			Message message = new()
			{
				Name = Utf8(messages[index].Name),
				Signature = Utf8(messages[index].Signature),
				Types = messages[index].Types,
			};
			Marshal.StructureToPtr(message, pointer + index * size, false);
		}
		return pointer;
	}

	private static nint Utf8(string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
		nint pointer = Marshal.AllocHGlobal(bytes.Length);
		Marshal.Copy(bytes, 0, pointer, bytes.Length);
		return pointer;
	}
}
