using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Nori.Core.Sandbox.Linux;

internal static class LandlockAbi
{
	// Linux x86_64 与 arm64 的 Landlock 系统调用号相同。
	internal const int CreateRuleset = 444;
	internal const int AddRule = 445;
	internal const int RestrictSelf = 446;
	private const uint CreateRulesetVersion = 1;
	private const int OperationNotPermitted = 1;
	private const int NotImplemented = 38;
	private const int NotSupported = 95;

	internal const ulong ReadAndExecuteFile = (1UL << 0) | (1UL << 2);
	internal const ulong ReadAndExecuteDirectory = ReadAndExecuteFile | (1UL << 3);
	private const ulong FileSystemV1 = (1UL << 13) - 1;
	private const ulong Refer = 1UL << 13;
	private const ulong Truncate = 1UL << 14;
	private const ulong BindAndConnectTcp = (1UL << 0) | (1UL << 1);

	internal static (ulong FileSystem, ulong Network) BuildAccessMasks(int abi, bool allowNetwork) => (
		FileSystemV1 | (abi >= 2 ? Refer : 0) | (abi >= 3 ? Truncate : 0),
		abi >= 4 && !allowNetwork ? BindAndConnectTcp : 0);

	internal static int Probe()
	{
		if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
			return 0;

		nint version = Syscall(CreateRuleset, 0, 0, CreateRulesetVersion);
		return version >= 0 ? (int)version : UnavailableOrThrow(Marshal.GetLastPInvokeError());
	}

	internal static int UnavailableOrThrow(int error) => error switch
	{
		OperationNotPermitted or NotImplemented or NotSupported => 0,
		_ => throw new Win32Exception(error, "无法探测 Linux Landlock ABI。"),
	};

	[DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
	internal static extern nint Syscall(nint number, nint arg1, nuint arg2, uint arg3);
}
