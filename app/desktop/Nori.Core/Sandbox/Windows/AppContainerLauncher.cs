using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using static Nori.Core.Sandbox.Windows.AppContainerNativeApi;

namespace Nori.Core.Sandbox.Windows;

/// <summary>
/// Windows AppContainer 执行。
///
/// 边界由两样东西构成：进程令牌带容器 SID，而对象只有在 DACL 里授予了该 SID 时才可访问；
/// 网络另由 capability 控制，不授 <c>internetClient</c> 即无出站。微软把 AppContainer 列为
/// 安全边界，绕过它属于可服务的安全漏洞 —— 这与低完整性级别不同，后者只是纵深防御。
///
/// **授权是对文件系统 ACL 的持久修改**，不是进程级规则集。因此 <see cref="RevokeAccess"/>
/// 必须在工作目录变更或功能关闭时调用，否则 ACE 会残留在用户的目录上。
///
/// 实测过的边界（工具链只读 + 工作区读写两条 ACE，未授 internetClient）：工作区内读写枚举
/// 通过；写工作区外、读用户目录、读其他盘、往只读路径写、联网均被拒绝。**但凡授予了
/// `ALL_APPLICATION_PACKAGES` 的位置（`C:\Windows`、`C:\Program Files` 大部分）仍可读** ——
/// 要收紧需改用 LPAC，代价是系统 DLL 的加载需要 `ALL_RESTRICTED_APPLICATION_PACKAGES`。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppContainerLauncher : ISandboxLauncher
{
	private readonly string _containerName;
	private readonly Lock _gate = new();
	private string? _sidText;

	/// <summary>按容器名创建启动器。容器配置文件在首次执行时建立。</summary>
	public AppContainerLauncher(string containerName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
		// 容器名进入 `AppData\Local\Packages\<name>`，长度与字符集受限。
		_containerName = containerName.Length > 64 ? containerName[..64] : containerName;
	}

	/// <inheritdoc />
	public SandboxIsolation Isolation => SandboxIsolation.AppContainer;

	/// <inheritdoc />
	public string Describe() =>
		"AppContainer 隔离：命令只能读写你选定的工作文件夹，读取工具链目录，默认无法联网。";

	/// <summary>本机能否使用 AppContainer。</summary>
	public static bool IsSupported => OperatingSystem.IsWindows();

	/// <summary>提前建立容器配置文件，用于在选型阶段判断 AppContainer 是否真的可用。</summary>
	public void EnsureReady() => EnsureProfile();

	/// <summary>
	/// 本容器的 SID。
	///
	/// 只推导不创建。授权就是往目录 ACL 里加这个 SID 的 ACE，暴露出来才能核对某个目录上到底
	/// 还留没留授权 —— 这是唯一能从外部验证释放是否生效的判据。
	/// </summary>
	public string ContainerSid => DeriveSid();

	/// <inheritdoc />
	public Task<SandboxResult> RunAsync(
		string commandLine, SandboxPolicy policy, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(policy);
		ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

		string sid = EnsureProfile();
		GrantAccess(policy, sid);
		return Task.Run(() => Execute(commandLine, policy, sid, cancellationToken), cancellationToken);
	}

	/// <inheritdoc />
	/// <remarks>
	/// 走 <see cref="DeriveSid"/> 而非 <see cref="EnsureProfile"/>：释放不该顺手把容器建出来。
	/// 容器 SID 是容器名的确定性推导，配置文件不存在时同样能算出来，因此本方法对「从未授权过」
	/// 的情形安全，也可以重复调用。
	/// </remarks>
	public void Release(SandboxPolicy policy)
	{
		ArgumentNullException.ThrowIfNull(policy);
		SecurityIdentifier identity = new(DeriveSid());
		foreach (string path in Paths(policy))
		{
			RemoveRule(path, identity);
		}
	}

	/// <summary>删除容器配置文件，连同它的私有存储。撤销 ACE 是另一件事，见 <see cref="RevokeAccess"/>。</summary>
	public void DeleteProfile() => DeleteAppContainerProfile(_containerName);

	/// <summary>
	/// 展开 8.3 短名。
	///
	/// 容器内解析 `CLOUDN~1` 这类短名需要对父目录的列举权限，容器没有，表现为在祖先目录上
	/// 「拒绝访问」，与真正的授权缺失难以区分。
	/// </summary>
	public static string ExpandShortPath(string path)
	{
		ArgumentNullException.ThrowIfNull(path);
		if (!path.Contains('~', StringComparison.Ordinal)) return path;

		StringBuilder buffer = new(1024);
		int length = GetLongPathName(path, buffer, buffer.Capacity);
		return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
	}

	private static IEnumerable<string> Paths(SandboxPolicy policy) =>
		new[] { policy.WorkspaceRoot }.Concat(policy.ReadOnlyPaths).Where(path => path.Length > 0);

	/// <summary>确保容器配置文件存在，并返回它的 SID。会创建。</summary>
	private string EnsureProfile()
	{
		lock (_gate)
		{
			if (_sidText is not null) return _sidText;

			int created = CreateAppContainerProfile(
				_containerName, _containerName, "Nori 受限执行", IntPtr.Zero, 0, out IntPtr sid);
			if (created == ErrorAlreadyExists)
			{
				created = DeriveAppContainerSidFromAppContainerName(_containerName, out sid);
			}

			if (created != 0) throw new Win32Exception(created, $"建立 AppContainer 失败 0x{created:X8}");

			try
			{
				_sidText = SidToString(sid);
				return _sidText;
			}
			finally
			{
				FreeSid(sid);
			}
		}
	}

	/// <summary>
	/// 只推导容器 SID，不创建配置文件。
	///
	/// SID 是容器名的确定性推导，与配置文件是否存在无关。释放授权走这条路，避免「为了清理反而
	/// 建出一个容器」。
	/// </summary>
	private string DeriveSid()
	{
		lock (_gate)
		{
			if (_sidText is not null) return _sidText;

			int derived = DeriveAppContainerSidFromAppContainerName(_containerName, out IntPtr sid);
			if (derived != 0) throw new Win32Exception(derived, $"推导容器 SID 失败 0x{derived:X8}");

			try
			{
				return SidToString(sid);
			}
			finally
			{
				FreeSid(sid);
			}
		}
	}

	private static string SidToString(IntPtr sid)
	{
		if (!ConvertSidToStringSid(sid, out IntPtr text))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "读取容器 SID 失败");
		}

		try
		{
			return Marshal.PtrToStringUni(text)
				?? throw new InvalidOperationException("容器 SID 为空");
		}
		finally
		{
			LocalFree(text);
		}
	}

	private static void GrantAccess(SandboxPolicy policy, string sid)
	{
		SecurityIdentifier identity = new(sid);
		AddRule(policy.WorkspaceRoot, identity, FileSystemRights.Modify);
		foreach (string path in policy.ReadOnlyPaths)
		{
			if (path.Length > 0) AddRule(path, identity, FileSystemRights.ReadAndExecute);
		}
	}

	private static void AddRule(string path, SecurityIdentifier identity, FileSystemRights rights)
	{
		if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"路径不存在，无法授权: {path}");

		DirectoryInfo info = new(path);
		DirectorySecurity security = info.GetAccessControl(AccessControlSections.Access);
		security.AddAccessRule(new FileSystemAccessRule(
			identity,
			rights,
			InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
			PropagationFlags.None,
			AccessControlType.Allow));
		info.SetAccessControl(security);
	}

	private static void RemoveRule(string path, SecurityIdentifier identity)
	{
		if (!Directory.Exists(path)) return;

		DirectoryInfo info = new(path);
		DirectorySecurity security = info.GetAccessControl(AccessControlSections.Access);
		security.PurgeAccessRules(identity);
		info.SetAccessControl(security);
	}

	private SandboxResult Execute(
		string commandLine, SandboxPolicy policy, string sid, CancellationToken cancellationToken)
	{
		using AppContainerHandles handles = AppContainerHandles.Create(sid, policy.AllowNetwork, policy.EnvironmentOverrides);
		string workingDirectory = ExpandShortPath(policy.WorkspaceRoot);

		SecurityAttributes pipeAttributes = new()
		{
			Length = Marshal.SizeOf<SecurityAttributes>(),
			SecurityDescriptor = IntPtr.Zero,
			InheritHandle = true,
		};

		if (!CreatePipe(out IntPtr outRead, out IntPtr outWrite, ref pipeAttributes, 0))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "建立输出管道失败");
		}

		// 读端不可继承：子进程持有读端时管道不会 EOF，读取会一直阻塞。
		SetHandleInformation(outRead, HandleFlagInherit, 0);

		try
		{
			StartupInfoEx startup = default;
			startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
			startup.StartupInfo.Flags = StartfUseStdHandles;
			startup.StartupInfo.StdOutput = outWrite;
			startup.StartupInfo.StdError = outWrite;
			startup.AttributeList = handles.AttributeList;

			// CreateProcess 会就地改写命令行缓冲区，必须传可写副本。
			StringBuilder mutable = new(commandLine, commandLine.Length + 1);

			if (!CreateProcess(
				null, mutable, IntPtr.Zero, IntPtr.Zero, true,
				ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow,
				handles.Environment, workingDirectory, ref startup, out ProcessInformation process))
			{
				int error = Marshal.GetLastWin32Error();
				throw new Win32Exception(error, $"在 AppContainer 中启动进程失败: {new Win32Exception(error).Message}");
			}

			CloseHandle(outWrite);
			outWrite = IntPtr.Zero;

			return Collect(process, outRead, policy, cancellationToken);
		}
		finally
		{
			if (outWrite != IntPtr.Zero) CloseHandle(outWrite);
			CloseHandle(outRead);
		}
	}

	private static SandboxResult Collect(
		ProcessInformation process, IntPtr outRead, SandboxPolicy policy, CancellationToken cancellationToken)
	{
		try
		{
			// 先把管道读干再等退出：输出超过管道缓冲区时子进程会阻塞在写上，
			// 顺序反过来会双向等待。
			byte[] raw = ReadAll(outRead);

			uint waited = WaitForSingleObject(process.Process, (uint)Math.Max(0, policy.Timeout.TotalMilliseconds));
			bool timedOut = waited != 0;
			if (timedOut || cancellationToken.IsCancellationRequested)
			{
				TerminateProcess(process.Process, 1);
				WaitForSingleObject(process.Process, 5000);
			}

			cancellationToken.ThrowIfCancellationRequested();
			GetExitCodeProcess(process.Process, out uint exitCode);
			(string text, bool truncated) = SandboxOutput.Fit(
				SandboxOutput.Decode(raw), policy.MaxOutputCharacters);

			return new SandboxResult
			{
				ExitCode = timedOut ? -1 : (int)exitCode,
				Output = text,
				TimedOut = timedOut,
				Truncated = truncated,
			};
		}
		finally
		{
			CloseHandle(process.Thread);
			CloseHandle(process.Process);
		}
	}

	private static byte[] ReadAll(IntPtr handle)
	{
		using MemoryStream buffer = new();
		byte[] chunk = new byte[8192];
		while (ReadFile(handle, chunk, chunk.Length, out int read, IntPtr.Zero) && read > 0)
		{
			buffer.Write(chunk, 0, read);
		}

		return buffer.ToArray();
	}
}
