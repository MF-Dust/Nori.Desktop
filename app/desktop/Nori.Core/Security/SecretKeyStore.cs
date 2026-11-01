using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Nori.Core.Data;

namespace Nori.Core.Security;

/// <summary>
/// 主密钥保管
///
/// 各平台的落点:
/// - Windows: DPAPI(CurrentUser) 保护的密钥文件 `<PackageRoot>/data/core/security/secret.key`
/// - macOS:   Keychain (Security 框架读写，旧条目仍可用 security 命令读取); 失败回退 `<PackageRoot>/data/core/security/secret.key` (0600)
/// - Linux:   libsecret (secret-tool, 若可用); 否则 `<PackageRoot>/data/core/security/secret.key` (0600)
///
/// 回退到裸文件时会写日志 —— 这是「能用但更弱」的状态, 不能静默。
/// </summary>
public interface ISecretKeyStore
{
	/// <summary>取出主密钥 (不存在则生成并保存); 失败必须抛出, 不得返回可导致明文落库的回退值。</summary>
	byte[] LoadOrCreate();

	/// <summary>当前是否退化为纯文件保护 (无系统密钥库)</summary>
	bool IsFileFallback { get; }
}

/// <summary>平台密钥库不可用或密钥文件损坏。</summary>
public sealed class SecretKeyStoreException(string message, Exception? innerException = null)
	: InvalidOperationException(message, innerException)
{
}

/// <summary>
/// 默认实现: 按平台挑选保管方式；暂时不可用时拒绝换钥，调用方立即失败，由后台按需单飞探测恢复。
/// 连续不可用的冷却从 30 秒指数退避至 5 分钟，成功后重置。
/// </summary>
public sealed class SecretKeyStore : ISecretKeyStore
{
	private enum KeyStoreTool
	{
		MacOsSecurity,
		LinuxSecretTool,
	}

	/// <summary>主密钥长度 (AES-256)</summary>
	public const int KeySize = 32;

	internal enum PlatformKeyStatus { Found, NotFound, Unavailable }
	internal readonly record struct PlatformKeyResult(PlatformKeyStatus Status, byte[]? Key = null);
	internal enum PlatformKeyWriteStatus { Stored, Failed, Unavailable }
	internal enum ToolRunStatus { Exited, TimedOut, StartFailed }
	internal readonly record struct ToolRun(ToolRunStatus Status, int ExitCode = 0, string? Output = null);

	private sealed class PlatformUnavailableSignal : Exception { }

	private const long UnavailableCooldownMilliseconds = 30_000;
	private const long MaximumUnavailableCooldownMilliseconds = 300_000;
	private const int ToolTimeoutMilliseconds = 5_000;
	private const string KeychainService = "cn.erhio.noriDesktopPet";
	private const string KeychainAccount = "config-master-key";

	private readonly string _keyPath;
	private readonly Func<PlatformKeyResult> _readPlatformKey = ReadPlatformKey;
	private readonly Func<byte[], PlatformKeyWriteStatus> _writePlatformKey = WritePlatformKey;
	private readonly Func<long> _getTimestampMilliseconds = () => Environment.TickCount64;
	private readonly Action<Action> _startBackground = work => _ = Task.Run(work);
	private SecretKeyStoreException? _unavailableException;
	private long _unavailableAt;
	private int _consecutiveUnavailable;
	private bool _probeRunning;
	private readonly Lock _gate = new();
	private readonly Lock _loadGate = new();
	private byte[]? _cached;

	/// <summary>生产构造必须使用宿主已经解析并校验过的 secret.key 路径。</summary>
	public SecretKeyStore(AppStoragePaths paths)
	{
		ArgumentNullException.ThrowIfNull(paths);
		_keyPath = paths.SecretPath;
	}

	/// <summary>兼容纯核心测试的当前目录构造；宿主生产装配不得使用。</summary>
	public SecretKeyStore() : this(Environment.CurrentDirectory) { }

	/// <summary>测试用显式目录构造；不会推断或访问系统数据目录。</summary>
	public SecretKeyStore(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		_keyPath = Directory.Exists(directory) || string.IsNullOrEmpty(Path.GetExtension(directory))
			? Path.Combine(directory, "secret.key")
			: Path.GetFullPath(directory);
	}

	/// <summary>测试替换外部密钥库，避免读写开发机的真实主密钥。</summary>
	internal SecretKeyStore(string directory, Func<PlatformKeyResult> readPlatformKey,
		Func<byte[], PlatformKeyWriteStatus> writePlatformKey, Func<long>? getTimestampMilliseconds = null,
		Action<Action>? startBackground = null) : this(directory)
	{
		_readPlatformKey = readPlatformKey;
		_writePlatformKey = writePlatformKey;
		if (getTimestampMilliseconds is not null) _getTimestampMilliseconds = getTimestampMilliseconds;
		if (startBackground is not null) _startBackground = startBackground;
	}

	/// <inheritdoc />
	public bool IsFileFallback { get; private set; }

	/// <inheritdoc />
	public byte[] LoadOrCreate()
	{
		lock (_gate)
		{
			byte[]? cached = GetCachedOrThrowUnavailable();
			if (cached is not null) return cached;
		}

		// 实际加载单独串行化，不可用期间的调用方只访问上面的短状态锁。
		lock (_loadGate)
		{
			lock (_gate)
			{
				byte[]? cached = GetCachedOrThrowUnavailable();
				if (cached is not null) return cached;
			}
			try
			{
				byte[] key = LoadKey();
				lock (_gate)
				{
					_cached = key;
					ClearUnavailable();
				}
				return key;
			}
			catch (PlatformUnavailableSignal)
			{
				lock (_gate) throw RecordUnavailable();
			}
		}
	}

	// 仅在持有 _gate 时调用；冷却结束的调用仍抛出原异常，不等待后台探测。
	private byte[]? GetCachedOrThrowUnavailable()
	{
		if (_cached is not null) return _cached;
		if (_unavailableException is null) return null;
		SecretKeyStoreException exception = _unavailableException;
		long cooldown = Math.Min(MaximumUnavailableCooldownMilliseconds,
			UnavailableCooldownMilliseconds << (_consecutiveUnavailable - 1));
		if (!_probeRunning && _getTimestampMilliseconds() - _unavailableAt >= cooldown)
		{
			_probeRunning = true;
			_startBackground(Probe);
		}
		throw exception;
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "后台探测不能向线程池泄漏异常；非暂时不可用错误交由下一次前台加载暴露。")]
	private void Probe()
	{
		try
		{
			lock (_loadGate)
			{
				byte[] key = LoadKey();
				lock (_gate)
				{
					_cached = key;
					ClearUnavailable();
				}
			}
		}
		catch (PlatformUnavailableSignal)
		{
			lock (_gate) RecordUnavailable();
		}
		catch
		{
			lock (_gate) ClearUnavailable();
		}
		finally
		{
			lock (_gate) _probeRunning = false;
		}
	}

	private byte[] LoadKey()
	{
		using FileStream processLock = AcquireProcessLock();
		byte[]? existing = TryLoad();
		if (existing is not null)
		{
			if (existing.Length != KeySize)
				throw new SecretKeyStoreException("平台主密钥长度无效, 为避免使已有密文全部失效而拒绝覆盖");
			return existing;
		}

		byte[] created = RandomNumberGenerator.GetBytes(KeySize);
		Save(created);
		return created;
	}

	// 以下状态变更均由持有 _gate 的前台加载或后台探测负责。
	private SecretKeyStoreException RecordUnavailable()
	{
		// 第五次失败已达到冷却上限，饱和计数避免移位溢出。
		_consecutiveUnavailable = Math.Min(_consecutiveUnavailable + 1, 5);
		_unavailableAt = _getTimestampMilliseconds();
		_unavailableException ??= new SecretKeyStoreException("平台密钥库暂时不可用（可能已锁定、取消了解锁或无响应）, 为避免更换主密钥已拒绝读写敏感配置; 请解锁系统密钥环, 稍后会自动重试");
		return _unavailableException;
	}

	private void ClearUnavailable()
	{
		_unavailableException = null;
		_consecutiveUnavailable = 0;
		_unavailableAt = 0;
	}

	private FileStream AcquireProcessLock()
	{
		string lockPath = _keyPath + ".lock";
		Directory.CreateDirectory(Path.GetDirectoryName(_keyPath) ?? ".");
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
		while (true)
		{
			try
			{
				FileStream stream = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
				try
				{
					if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
					return stream;
				}
				catch
				{
					stream.Dispose();
					throw;
				}
			}
			catch (IOException) when (DateTime.UtcNow < deadline)
			{
				Thread.Sleep(50);
			}
			catch (IOException exception)
			{
				throw new SecretKeyStoreException("等待平台主密钥锁超时, 为避免并发更换密钥而拒绝启动敏感配置", exception);
			}
		}
	}

	private byte[]? TryLoad()
	{
		// 文件回退一旦承载了已有密文，就不能因系统密钥环恢复而切换到另一把密钥。
		if (!File.Exists(_keyPath))
		{
			PlatformKeyResult result = _readPlatformKey();
			if (result.Status == PlatformKeyStatus.Unavailable) throw new PlatformUnavailableSignal();
			return result.Status == PlatformKeyStatus.Found ? result.Key : null;
		}
		try
		{
			if (!OperatingSystem.IsWindows())
			{
				UnixFileMode mode = File.GetUnixFileMode(_keyPath);
				UnixFileMode allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite;
				if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
					| UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0
					|| (mode & allowed) != allowed)
					throw new SecretKeyStoreException("平台主密钥文件权限不安全, 请收紧为 0600");
			}
			byte[] stored = File.ReadAllBytes(_keyPath);
			if (OperatingSystem.IsWindows())
			{
				return System.Security.Cryptography.ProtectedData.Unprotect(
					stored, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
			}
			IsFileFallback = true;
			return stored;
		}
		catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
		{
			throw new SecretKeyStoreException("无法读取平台主密钥, 为避免静默更换密钥而拒绝启动敏感配置", exception);
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "密钥保存失败时的回滚清理不能覆盖原始异常。")]
	private void Save(byte[] key)
	{
		PlatformKeyWriteStatus result = _writePlatformKey(key);
		if (result == PlatformKeyWriteStatus.Unavailable) throw new PlatformUnavailableSignal();
		if (result == PlatformKeyWriteStatus.Stored) return;

		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(_keyPath) ?? ".");
			byte[] payload = OperatingSystem.IsWindows()
				? System.Security.Cryptography.ProtectedData.Protect(
					key, null, System.Security.Cryptography.DataProtectionScope.CurrentUser)
				: key;
			string temporary = _keyPath + $".tmp-{Guid.NewGuid():N}";
			try
			{
				FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
				// 创建时即限制访问，不能等裸密钥写完后再收紧权限。
				if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
				using (FileStream stream = new(temporary, options))
				{
					stream.Write(payload);
					stream.Flush(true);
				}
				if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
				File.Move(temporary, _keyPath, true);
			}
			finally
			{
				try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
			}
		}
		catch (SecretKeyStoreException)
		{
			throw;
		}
		catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
		{
			throw new SecretKeyStoreException("无法保存平台主密钥, 敏感配置将保持未写入", exception);
		}
		if (!OperatingSystem.IsWindows()) IsFileFallback = true;
	}

	private static PlatformKeyResult ReadPlatformKey()
	{
		if (OperatingSystem.IsMacOS())
		{
			byte[]? key = TryFrameworkKeychainRead();
			return key is not null ? new(PlatformKeyStatus.Found, key) : TryKeychainRead();
		}
		return OperatingSystem.IsLinux() ? TrySecretToolRead() : new(PlatformKeyStatus.NotFound);
	}

	private static PlatformKeyWriteStatus WritePlatformKey(byte[] key)
	{
		if (OperatingSystem.IsMacOS())
			return TryKeychainWrite(key) ? PlatformKeyWriteStatus.Stored : PlatformKeyWriteStatus.Failed;
		return OperatingSystem.IsLinux() ? TrySecretToolWrite(key) : PlatformKeyWriteStatus.Failed;
	}

	// ---- macOS 钥匙串 (Keychain) ----

	private static byte[]? TryFrameworkKeychainRead()
	{
		if (!OperatingSystem.IsMacOS()) return null;
		byte[]? payload = MacKeychainStore.TryReadGenericPassword(KeychainService, KeychainAccount);
		if (payload is null || payload.Length == 0) return null;
		return DecodeHex(Encoding.ASCII.GetString(payload));
	}

	private static PlatformKeyResult TryKeychainRead()
	{
		ToolRun result = RunTool(KeyStoreTool.MacOsSecurity,
			["find-generic-password", "-s", KeychainService, "-a", KeychainAccount, "-w"]);
		return DecodeToolKey(result);
	}

	private static bool TryKeychainWrite(byte[] key)
	{
		if (!OperatingSystem.IsMacOS()) return false;
		// 与 find-generic-password -w 读出的文本一致，但不再把密钥放进进程参数。
		byte[] payload = Encoding.ASCII.GetBytes(Convert.ToHexString(key));
		return MacKeychainStore.TryWriteGenericPassword(KeychainService, KeychainAccount, payload);
	}

	// ---- Linux 密钥环 (libsecret) ----

	private static PlatformKeyResult TrySecretToolRead()
	{
		ToolRun result = RunTool(KeyStoreTool.LinuxSecretTool,
			["lookup", "service", KeychainService, "account", KeychainAccount]);
		return ClassifySecretToolRead(result, () => RunTool(KeyStoreTool.LinuxSecretTool,
			["search", "service", KeychainService, "account", KeychainAccount]));
	}

	internal static PlatformKeyResult ClassifySecretToolRead(ToolRun lookup, Func<ToolRun> search)
	{
		if (lookup.Status != ToolRunStatus.Exited || lookup.ExitCode == 0) return DecodeToolKey(lookup);

		// 不解锁地确认条目是否存在；输出可能含秘密，只判断空白，不记录或保存内容。
		ToolRun result = search();
		if (result.Status != ToolRunStatus.Exited) return new(PlatformKeyStatus.Unavailable);
		return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output)
			? new(PlatformKeyStatus.Unavailable)
			: new(PlatformKeyStatus.NotFound);
	}

	private static PlatformKeyWriteStatus TrySecretToolWrite(byte[] key)
	{
		string hex = Convert.ToHexString(key);
		ToolRun result = RunTool(KeyStoreTool.LinuxSecretTool,
			["store", "--label=Nori Desktop Pet", "service", KeychainService, "account", KeychainAccount],
			stdin: hex);
		if (result.Status == ToolRunStatus.TimedOut) return PlatformKeyWriteStatus.Unavailable;
		return result.Status == ToolRunStatus.Exited && result.ExitCode == 0
			? PlatformKeyWriteStatus.Stored : PlatformKeyWriteStatus.Failed;
	}

	private static PlatformKeyResult DecodeToolKey(ToolRun result)
	{
		if (result.Status == ToolRunStatus.TimedOut) return new(PlatformKeyStatus.Unavailable);
		byte[]? key = result.Status == ToolRunStatus.Exited && result.ExitCode == 0 ? DecodeHex(result.Output) : null;
		return key is null ? new(PlatformKeyStatus.NotFound) : new(PlatformKeyStatus.Found, key);
	}

	private static byte[]? DecodeHex(string? text)
	{
		if (text is null) return null;
		string trimmed = text.Trim();
		if (trimmed.Length != KeySize * 2) return null;
		try
		{
			return Convert.FromHexString(trimmed);
		}
		catch (FormatException)
		{
			return null;
		}
	}

	/// <summary>
	/// 固定路径执行外部密钥库命令，输入、双流读取和退出共用 5 秒预算；超时终止进程树。
	/// 丢弃标准错误，不记录秘密；返回启动、超时或退出状态，由平台逻辑分类是否允许回退。
	/// </summary>
	private static ToolRun RunTool(KeyStoreTool tool, string[] arguments, string? stdin = null)
	{
		Stopwatch budget = Stopwatch.StartNew();
		try
		{
			ProcessStartInfo info = new()
			{
				// 不从 PATH 查找，避免应用启动环境中的恶意可执行文件读取或替换主密钥。
				FileName = tool switch
				{
					KeyStoreTool.MacOsSecurity => "/usr/bin/security",
					KeyStoreTool.LinuxSecretTool => "/usr/bin/secret-tool",
					_ => throw new ArgumentOutOfRangeException(nameof(tool)),
				},
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = stdin is not null,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			foreach (string argument in arguments) info.ArgumentList.Add(argument);

			using Process? process = Process.Start(info); // nosemgrep
			if (process is null) return new(ToolRunStatus.StartFailed);

			Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
			Task errorTask = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
			Task inputTask = stdin is null ? Task.CompletedTask : WriteToolInputAsync(process, stdin);
			Task ioTask = Task.WhenAll(outputTask, errorTask, inputTask);
			// 超时后流任务仍可能失败，提前注册观察，避免未观察异常。
			_ = ioTask.ContinueWith(
				static task => _ = task.Exception,
				CancellationToken.None,
				TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
			if (!ioTask.Wait(RemainingToolMilliseconds(budget))
				|| !process.WaitForExit(RemainingToolMilliseconds(budget)))
			{
				TryTerminate(process);
				return new(ToolRunStatus.TimedOut);
			}
			return new(ToolRunStatus.Exited, process.ExitCode, outputTask.GetAwaiter().GetResult());
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or AggregateException)
		{
			return new(ToolRunStatus.StartFailed);
		}
	}

	private static int RemainingToolMilliseconds(Stopwatch budget)
		=> (int)Math.Clamp(ToolTimeoutMilliseconds - budget.ElapsedMilliseconds, 0, ToolTimeoutMilliseconds);

	private static async Task WriteToolInputAsync(Process process, string stdin)
	{
		await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
		process.StandardInput.Close();
	}

	private static void TryTerminate(Process process)
	{
		try
		{
			if (!process.HasExited) process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
		{
		}
	}
}
