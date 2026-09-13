using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Nori.Core.Sandbox.Windows;

/// <summary>
/// AppContainer 相关的 Win32 调用。
///
/// 没有可用的 BCL 封装：<see cref="System.Diagnostics.Process"/> 不暴露扩展启动信息，
/// 而把进程放进 AppContainer 必须经由 <c>PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES</c>，
/// 因此整条启动链路只能手写。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AppContainerNativeApi
{
	/// <summary>把进程放进指定 AppContainer 的属性标识。</summary>
	internal const int ProcThreadAttributeSecurityCapabilities = 0x00020009;

	internal const uint ExtendedStartupInfoPresent = 0x00080000;
	internal const uint CreateUnicodeEnvironment = 0x00000400;
	internal const uint CreateNoWindow = 0x08000000;
	internal const uint StartfUseStdHandles = 0x00000100;
	internal const uint HandleFlagInherit = 0x00000001;
	internal const uint SeGroupEnabled = 0x00000004;
	internal const uint Infinite = 0xFFFFFFFF;

	/// <summary>配置文件已存在时 <c>CreateAppContainerProfile</c> 的返回值。</summary>
	internal const int ErrorAlreadyExists = unchecked((int)0x800700B7);

	/// <summary>出站互联网访问的众所周知能力 SID（<c>internetClient</c>）。</summary>
	internal const string InternetClientCapability = "S-1-15-3-1";

	[DllImport("userenv.dll", CharSet = CharSet.Unicode)]
	internal static extern int CreateAppContainerProfile(
		string name, string displayName, string description, IntPtr capabilities, int capabilityCount, out IntPtr sid);

	[DllImport("userenv.dll", CharSet = CharSet.Unicode)]
	internal static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);

	[DllImport("userenv.dll", CharSet = CharSet.Unicode)]
	internal static extern int DeleteAppContainerProfile(string name);

	// FreeSid 由 advapi32 导出，尽管上面两个取 SID 的函数在 userenv。
	[DllImport("advapi32.dll")]
	internal static extern IntPtr FreeSid(IntPtr sid);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool ConvertStringSidToSid(string text, out IntPtr sid);

	[DllImport("kernel32.dll", SetLastError = true)]
	internal static extern IntPtr LocalFree(IntPtr memory);

	/// <summary>
	/// 展开 8.3 短名。
	///
	/// 容器内解析 `CLOUDN~1` 这类短名需要对父目录的列举权限，而容器没有，表现为在祖先
	/// 目录上「拒绝访问」。传入前必须展开成长路径。
	/// </summary>
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern int GetLongPathName(string shortPath, StringBuilder longPath, int bufferLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool UpdateProcThreadAttribute(
		IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

	[DllImport("kernel32.dll")]
	internal static extern void DeleteProcThreadAttributeList(IntPtr list);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool CreateProcess(
		string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
		[MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment,
		string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, int size);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool ReadFile(IntPtr handle, byte[] buffer, int count, out int read, IntPtr overlapped);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool CloseHandle(IntPtr handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool TerminateProcess(IntPtr handle, uint exitCode);

	[StructLayout(LayoutKind.Sequential)]
	internal struct SecurityCapabilities
	{
		public IntPtr AppContainerSid;
		public IntPtr Capabilities;
		public uint CapabilityCount;
		public uint Reserved;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct SidAndAttributes
	{
		public IntPtr Sid;
		public uint Attributes;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct SecurityAttributes
	{
		public int Length;
		public IntPtr SecurityDescriptor;
		[MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	internal struct StartupInfo
	{
		public int Size;
		public IntPtr Reserved;
		public IntPtr Desktop;
		public IntPtr Title;
		public int X;
		public int Y;
		public int XSize;
		public int YSize;
		public int XCountChars;
		public int YCountChars;
		public int FillAttribute;
		public uint Flags;
		public short ShowWindow;
		public short Reserved2Size;
		public IntPtr Reserved2;
		public IntPtr StdInput;
		public IntPtr StdOutput;
		public IntPtr StdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct StartupInfoEx
	{
		public StartupInfo StartupInfo;
		public IntPtr AttributeList;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct ProcessInformation
	{
		public IntPtr Process;
		public IntPtr Thread;
		public int ProcessId;
		public int ThreadId;
	}
}
