using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Nori.Desktop.Automation.Windows;

/// <summary>窗口枚举、安全桌面、UIPI 与 DPI 的 Win32 封装。</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32WindowNativeApi : IWindowsWindowNativeApi
{
	private const int TokenIntegrityLevel = 25;
	private const uint TokenQuery = 8;
	private const uint ProcessQueryLimitedInformation = 0x1000;
	private const uint DesktopReadObjects = 1;
	[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool EnumWindowsProc(nint handle, nint data);
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S3898", Justification = "原生 ABI 结构体仅用于互操作，不参与相等比较。")]
	[StructLayout(LayoutKind.Sequential)] private struct TokenLabel { public nint Sid; }

	[DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc callback, nint data);
	[DllImport("user32.dll", EntryPoint = "IsWindow")] private static extern bool NativeIsWindow(nint handle);
	[DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool NativeIsWindowVisible(nint handle);
	[DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int maxCount);
	[DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
	[DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint NativeGetForegroundWindow();
	[DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint handle, out WindowsNativeRect rect);
	[DllImport("user32.dll", EntryPoint = "GetDpiForWindow")] private static extern uint NativeGetDpiForWindow(nint handle);
	[DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
	[DllImport("user32.dll", SetLastError = true)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder name, uint length, out uint needed);
	[DllImport("user32.dll")] private static extern bool CloseDesktop(nint handle);
	[DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
	[DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
	[DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
	[DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
	[DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int kind, byte[] buffer, int length, out int returned);
	[DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
	[DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
	[DllImport("user32.dll", EntryPoint = "SetThreadDpiAwarenessContext", SetLastError = true)] private static extern nint NativeSetThreadDpiAwarenessContext(nint context);

	public bool TryEnumerateTopLevelWindows(Func<nint, bool> callback)
	{
		EnumWindowsProc proc = (handle, _) => callback(handle);
		return EnumWindows(proc, 0);
	}
	public bool IsWindow(nint handle) => NativeIsWindow(handle);
	public bool IsWindowVisible(nint handle) => NativeIsWindowVisible(handle);
	public nint GetRootWindow(nint handle) => GetAncestor(handle, 2);
	public string GetWindowTitle(nint handle) { StringBuilder text = new(512); GetWindowText(handle, text, text.Capacity); return text.ToString(); }
	public int GetProcessId(nint handle) { GetWindowThreadProcessId(handle, out uint id); return id > int.MaxValue ? 0 : (int)id; }
	public nint GetForegroundWindow() => NativeGetForegroundWindow();
	public bool TryGetWindowRect(nint handle, out WindowsNativeRect rect) => GetWindowRect(handle, out rect);
	public uint GetDpiForWindow(nint handle) => NativeGetDpiForWindow(handle);
	public nint SetThreadDpiAwarenessContext(nint context) => NativeSetThreadDpiAwarenessContext(context);

	public bool IsSecureDesktop()
	{
		nint desktop = OpenInputDesktop(0, false, DesktopReadObjects);
		if (desktop == 0) return true;
		try
		{
			StringBuilder name = new(128);
			if (!GetUserObjectInformation(desktop, 2, name, (uint)name.Capacity, out _)) return true;
			return name.ToString().Equals("Winlogon", StringComparison.OrdinalIgnoreCase) || name.ToString().Equals("Screen-saver", StringComparison.OrdinalIgnoreCase);
		}
		finally { CloseDesktop(desktop); }
	}

	public bool IsUipiAllowed(nint handle)
	{
		GetWindowThreadProcessId(handle, out uint processId);
		if (processId == 0) return false;
		nint process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
		if (process == 0) return false;
		try
		{
			if (!TryGetIntegrity(process, out int target) || !TryGetIntegrity(GetCurrentProcess(), out int current)) return false;
			return target <= current;
		}
		finally { CloseHandle(process); }
	}

	private static bool TryGetIntegrity(nint process, out int level)
	{
		level = 0;
		if (!OpenProcessToken(process, TokenQuery, out nint token)) return false;
		try
		{
			byte[] buffer = new byte[256];
			if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, buffer.Length, out _)) return false;
			GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
			try
			{
				TokenLabel label = Marshal.PtrToStructure<TokenLabel>(pin.AddrOfPinnedObject());
				if (label.Sid == 0) return false;
				nint count = GetSidSubAuthorityCount(label.Sid);
				if (count == 0) return false;
				nint authority = GetSidSubAuthority(label.Sid, (uint)(Marshal.ReadByte(count) - 1));
				level = Marshal.ReadInt32(authority);
				return true;
			}
			finally { pin.Free(); }
		}
		finally { CloseHandle(token); }
	}
}
