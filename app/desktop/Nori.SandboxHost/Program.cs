using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Nori.Core.Sandbox.Linux;

namespace Nori.SandboxHost;

internal static class Program
{
	private const int OpenPath = 0x200000;
	private const int OpenCloseOnExec = 0x80000;
	// Linux arm64 的 O_DIRECTORY 与 x86_64 不同。
	private static readonly int OpenDirectory = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;
	private const int NoSuchFile = 2;
	private const int NotDirectory = 20;
	private const int SetNoNewPrivileges = 38;
	private const int PathBeneath = 1;
	// Linux x86_64 与 arm64 的 close_range 系统调用号相同。
	private const int CloseRange = 436;
	private const uint CloseRangeCloseOnExec = 1 << 2;

	private static readonly string[] SystemReadOnlyPaths =
	[
		"/usr", "/lib", "/lib64", "/bin", "/sbin", "/etc/ld.so.cache", "/etc/ld.so.conf",
		"/etc/ld.so.conf.d", "/etc/alternatives", "/dev/null", "/dev/zero", "/dev/urandom",
	];

	private static int Main(string[] args)
	{
		try
		{
			Check(SetProcessGroup(0, 0), "无法为沙箱任务建立进程组。");
			Check(DuplicateDescriptor(1, 2), "无法合并沙箱任务的输出。");
			using Stream input = Console.OpenStandardInput();
			SandboxHostRequest request = JsonSerializer.Deserialize<SandboxHostRequest>(input)
				?? throw new InvalidOperationException("缺少沙箱任务配置。");

			nint[] argv = new nint[args.Length + 2];
			argv[0] = Marshal.StringToCoTaskMemUTF8(request.FileName);
			for (int i = 0; i < args.Length; i++) argv[i + 1] = Marshal.StringToCoTaskMemUTF8(args[i]);

			// 规则只加到当前线程；必须在同一线程直接 exec，不能切换到托管线程池。
			ApplyLandlock(request);
			// 任务环境不能用于启动辅助进程；进入隔离后才把它交给 exec。
			foreach ((string key, string value) in request.EnvironmentOverrides)
				Check(SetEnvironment(key, value, 1), $"无法设置沙箱任务的环境变量：{key}");
			Check(LandlockAbi.Syscall(CloseRange, 3, uint.MaxValue, CloseRangeCloseOnExec), "无法关闭任务不需要继承的文件描述符。");
			Check(Execute(argv[0], argv), $"无法执行沙箱任务：{request.FileName}");
			return 0;
		}
		catch (Win32Exception exception)
		{
			Console.Error.WriteLine($"{exception.Message}（errno={exception.NativeErrorCode}）");
			return 125;
		}
	}

	private static unsafe void ApplyLandlock(SandboxHostRequest request)
	{
		(ulong fileSystem, ulong network) = LandlockAbi.BuildAccessMasks(request.Abi, request.AllowNetwork);
		ulong* attributes = stackalloc ulong[] { fileSystem, network };
		nuint size = request.Abi >= 4 ? 16u : 8u;
		using SafeFileHandle ruleset = new(Check(LandlockAbi.Syscall(LandlockAbi.CreateRuleset, (nint)attributes, size, 0),
			"无法创建 Landlock 规则集。"), ownsHandle: true);
		using SafeFileHandle workspace = new(Check(Open(request.WorkspaceRoot, OpenPath | OpenCloseOnExec | OpenDirectory),
			$"无法打开沙箱工作目录：{request.WorkspaceRoot}"), ownsHandle: true);
		Check(ChangeDirectory((int)workspace.DangerousGetHandle()), $"无法进入沙箱工作目录：{request.WorkspaceRoot}");
		AddPathRule(ruleset, workspace, fileSystem, request.WorkspaceRoot);
		foreach (string path in SystemReadOnlyPaths.Concat(request.ReadOnlyPaths)) AddReadOnlyPath(ruleset, path);

		Check(ProcessControl(SetNoNewPrivileges, 1, 0, 0, 0), "无法禁止沙箱任务获得新权限。");
		Check(LandlockAbi.Syscall(LandlockAbi.RestrictSelf, ruleset.DangerousGetHandle(), 0, 0), "无法施加 Landlock 隔离。");
	}

	private static void AddReadOnlyPath(SafeFileHandle ruleset, string path)
	{
		ulong access = LandlockAbi.ReadAndExecuteDirectory;
		int fd = Open(path, OpenPath | OpenCloseOnExec | OpenDirectory);
		if (fd < 0 && Marshal.GetLastPInvokeError() == NotDirectory)
		{
			access = LandlockAbi.ReadAndExecuteFile;
			fd = Open(path, OpenPath | OpenCloseOnExec);
		}
		if (fd < 0 && Marshal.GetLastPInvokeError() is NoSuchFile or NotDirectory) return;

		using SafeFileHandle handle = new(Check(fd, $"无法打开 Landlock 只读路径：{path}"), ownsHandle: true);
		AddPathRule(ruleset, handle, access, path);
	}

	private static unsafe void AddPathRule(SafeFileHandle ruleset, SafeFileHandle handle, ulong access, string path)
	{
		PathRule attribute = new() { AllowedAccess = access, ParentFd = (int)handle.DangerousGetHandle() };
		Check(AddRule(LandlockAbi.AddRule, (int)ruleset.DangerousGetHandle(), PathBeneath, &attribute, 0),
			$"无法添加 Landlock 路径规则：{path}");
	}

	private static nint Check(nint result, string message) => result < 0
		? throw new Win32Exception(Marshal.GetLastPInvokeError(), message)
		: result;

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	private struct PathRule
	{
		public ulong AllowedAccess;
		public int ParentFd;
	}

	[DllImport("libc", EntryPoint = "setpgid", SetLastError = true)]
	private static extern int SetProcessGroup(int pid, int pgid);

	[DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
	private static extern int DuplicateDescriptor(int oldFd, int newFd);

	[DllImport("libc", EntryPoint = "open", SetLastError = true)]
	private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

	[DllImport("libc", EntryPoint = "fchdir", SetLastError = true)]
	private static extern int ChangeDirectory(int fd);

	[DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
	private static extern int ProcessControl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

	[DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
	private static extern unsafe nint AddRule(nint number, int ruleset, int type, PathRule* attribute, uint flags);

	[DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
	private static extern int SetEnvironment([MarshalAs(UnmanagedType.LPUTF8Str)] string key,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);

	[DllImport("libc", EntryPoint = "execvp", SetLastError = true)]
	private static extern int Execute(nint file, nint[] argv);
}
