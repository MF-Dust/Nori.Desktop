using System.Diagnostics;
using System.Security.Cryptography;
using Nori.Core.Data;

namespace Nori.Core.Security;

/// <summary>
/// 主密钥保管
///
/// 各平台的落点:
/// - Windows: DPAPI(CurrentUser) 保护的密钥文件 `<PackageRoot>/data/core/security/secret.key`
/// - macOS:   Keychain (security 命令); 失败回退 `<PackageRoot>/data/core/security/secret.key` (0600)
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
/// 默认实现: 按平台挑选保管方式
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

	private const string KeychainService = "cn.erhio.noriDesktopPet";
	private const string KeychainAccount = "config-master-key";

	private readonly string _keyPath;
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

	/// <inheritdoc />
	public bool IsFileFallback { get; private set; }

	/// <inheritdoc />
	public byte[] LoadOrCreate()
	{
		if (_cached is not null) return _cached;

		byte[]? existing = TryLoad();
		if (existing is not null)
		{
			if (existing.Length != KeySize)
			{
				throw new SecretKeyStoreException("平台主密钥长度无效, 为避免使已有密文全部失效而拒绝覆盖");
			}
			_cached = existing;
			return existing;
		}

		byte[] created = RandomNumberGenerator.GetBytes(KeySize);
		Save(created);
		_cached = created;
		return created;
	}

	private byte[]? TryLoad()
	{
		if (OperatingSystem.IsMacOS() && TryKeychainRead() is {Length: KeySize} fromKeychain) return fromKeychain;
		if (OperatingSystem.IsLinux() && TrySecretToolRead() is {Length: KeySize} fromSecretTool) return fromSecretTool;

		if (!File.Exists(_keyPath)) return null;
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

	private void Save(byte[] key)
	{
		if (OperatingSystem.IsMacOS() && TryKeychainWrite(key)) return;
		if (OperatingSystem.IsLinux() && TrySecretToolWrite(key)) return;

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
				using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					stream.Write(payload);
					stream.Flush(true);
				}
				if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
				File.Move(temporary, _keyPath, true);
			}
			finally
			{
				try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } // NOSONAR -- 临时资源清理失败不能覆盖原始异常
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

	// ---- macOS Keychain ----

	private static byte[]? TryKeychainRead()
	{
		string? output = RunTool(KeyStoreTool.MacOsSecurity,
			["find-generic-password", "-s", KeychainService, "-a", KeychainAccount, "-w"]);
		return DecodeHex(output);
	}

	private static bool TryKeychainWrite(byte[] key)
	{
		string hex = Convert.ToHexString(key);
		// -U: 已存在则更新
		return RunTool(KeyStoreTool.MacOsSecurity,
			["add-generic-password", "-s", KeychainService, "-a", KeychainAccount, "-w", hex, "-U"]) is not null;
	}

	// ---- Linux libsecret ----

	private static byte[]? TrySecretToolRead()
	{
		string? output = RunTool(KeyStoreTool.LinuxSecretTool,
			["lookup", "service", KeychainService, "account", KeychainAccount]);
		return DecodeHex(output);
	}

	private static bool TrySecretToolWrite(byte[] key)
	{
		string hex = Convert.ToHexString(key);
		return RunTool(KeyStoreTool.LinuxSecretTool,
			["store", "--label=Nori Desktop Pet", "service", KeychainService, "account", KeychainAccount],
			stdin: hex) is not null;
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
	/// 跑一个外部密钥库命令; 命令不存在或返回非 0 时给 null (调用方回退文件)
	/// </summary>
	private static string? RunTool(KeyStoreTool tool, string[] arguments, string? stdin = null)
	{
		try
		{
			ProcessStartInfo info = new()
			{
				FileName = tool switch
				{
					KeyStoreTool.MacOsSecurity => "security",
					KeyStoreTool.LinuxSecretTool => "secret-tool",
					_ => throw new ArgumentOutOfRangeException(nameof(tool)),
				},
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = stdin is not null,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			foreach (string argument in arguments) info.ArgumentList.Add(argument);

			// FileName 来自 KeyStoreTool 封闭枚举，参数通过 ArgumentList 传递。
			using Process? process = Process.Start(info); // nosemgrep
			if (process is null) return null;
			if (stdin is not null)
			{
				process.StandardInput.Write(stdin);
				process.StandardInput.Close();
			}
			string output = process.StandardOutput.ReadToEnd();
			process.WaitForExit(5000);
			return process.ExitCode == 0 ? output : null;
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
		{
			return null;
		}
	}
}
