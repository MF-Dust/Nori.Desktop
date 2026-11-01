using Nori.Core.Security;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

/// <summary>
/// 可移植静态加密: AES-GCM 往返、错误密钥拒绝、旧 DPAPI 格式识别
/// </summary>
public class SecretProtectorTests
{
	private static byte[] Key(byte fill) => Enumerable.Repeat(fill, SecretKeyStore.KeySize).ToArray();

	[Fact]
	public void 往返加解密()
	{
		byte[] key = Key(0x11);
		string cipher = SecretProtector.Protect(key, "sk-hello-世界");

		Assert.StartsWith(SecretProtector.Prefix, cipher, StringComparison.Ordinal);
		Assert.DoesNotContain("sk-hello", cipher, StringComparison.Ordinal);
		Assert.True(SecretProtector.TryUnprotect(key, cipher, out string plain));
		Assert.Equal("sk-hello-世界", plain);
	}

	[Fact]
	public void 相同明文两次加密结果不同()
	{
		byte[] key = Key(0x22);
		Assert.NotEqual(SecretProtector.Protect(key, "same"), SecretProtector.Protect(key, "same"));
	}

	[Fact]
	public void nsec2使用配置键作为AAD()
	{
		byte[] key = Key(0x2A);
		string cipher = SecretProtector.ProtectV2(key, "llm_api_key", "secret");

		Assert.StartsWith(SecretProtector.Nsec2Prefix, cipher, StringComparison.Ordinal);
		Assert.True(SecretProtector.TryUnprotectV2(key, "llm_api_key", cipher, out string plain));
		Assert.Equal("secret", plain);
		Assert.False(SecretProtector.TryUnprotectV2(key, "tts_api_key", cipher, out _));
	}

	[Fact]
	public void nsec1仍可读取()
	{
		string cipher = LegacySecretFormats.ProtectNsec1(Key(0x2B), "legacy");

		Assert.StartsWith(SecretProtector.LegacyNsec1Prefix, cipher, StringComparison.Ordinal);
		Assert.True(SecretProtector.TryUnprotect(Key(0x2B), "any_key", cipher, out string plain));
		Assert.Equal("legacy", plain);
	}

	[Fact]
	public void 错误密钥拒绝解密()
	{
		string cipher = SecretProtector.Protect(Key(0x33), "secret");
		Assert.False(SecretProtector.TryUnprotect(Key(0x44), cipher, out string plain));
		Assert.Equal("", plain);
	}

	[Fact]
	public void 密文被篡改后拒绝解密()
	{
		byte[] key = Key(0x55);
		string cipher = SecretProtector.Protect(key, "secret");
		// 翻掉 base64 正文里的一个字符
		char[] chars = cipher.ToCharArray();
		int index = SecretProtector.Prefix.Length + 4;
		chars[index] = chars[index] == 'A' ? 'B' : 'A';

		Assert.False(SecretProtector.TryUnprotect(key, new string(chars), out _));
	}

	[Fact]
	public void 空串不加密()
	{
		Assert.Equal("", SecretProtector.Protect(Key(0x66), ""));
	}

	[Fact]
	public void 格式识别区分新旧与明文()
	{
		Assert.True(SecretProtector.IsProtected(SecretProtector.Protect(Key(0x77), "x")));
		Assert.True(SecretProtector.IsLegacyDpapi("enc:dpapi:AQAAAA=="));
		Assert.False(SecretProtector.IsProtected("plain-text"));
		Assert.False(SecretProtector.IsLegacyDpapi("plain-text"));
	}

	[Fact]
	public void 非法输入不抛异常()
	{
		byte[] key = Key(0x88);
		foreach (string bad in new[] {"nsec1:", "nsec1:not-base64!!", "nsec1:QQ==", "plain"})
		{
			Assert.False(SecretProtector.TryUnprotect(key, bad, out _));
		}
	}
}

/// <summary>
/// 主密钥保管: 生成、持久化与复用
/// </summary>
public class SecretKeyStoreTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nori-key-{Guid.NewGuid():N}");

	public SecretKeyStoreTests() => Directory.CreateDirectory(_dir);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_dir, true);
		}
		catch (IOException)
		{
		}
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void 生成的密钥长度正确且同实例稳定()
	{
		SecretKeyStore store = new(_dir, () => new(SecretKeyStore.PlatformKeyStatus.NotFound), _ => SecretKeyStore.PlatformKeyWriteStatus.Failed);
		byte[] first = store.LoadOrCreate();
		byte[] second = store.LoadOrCreate();

		Assert.Equal(SecretKeyStore.KeySize, first.Length);
		Assert.Equal(first, second);
		Assert.NotEqual(new byte[SecretKeyStore.KeySize], first);
		Assert.True(File.Exists(Path.Combine(_dir, "secret.key")));
	}

	[Fact]
	public void 已有本地密钥不会被恢复的系统密钥环替换()
	{
		byte[] local = new SecretKeyStore(_dir, () => new(SecretKeyStore.PlatformKeyStatus.NotFound), _ => SecretKeyStore.PlatformKeyWriteStatus.Failed).LoadOrCreate();
		string cipher = SecretProtector.ProtectV2(local, "llm_api_key", "existing-secret");
		int platformReads = 0;
		SecretKeyStore reopened = new(_dir, () => { platformReads++; return new(SecretKeyStore.PlatformKeyStatus.Found, new byte[SecretKeyStore.KeySize]); },
			_ => throw new InvalidOperationException("不得覆盖主密钥"));

		byte[] loaded = reopened.LoadOrCreate();

		Assert.Equal(local, loaded);
		Assert.Equal(0, platformReads);
		Assert.Equal(!OperatingSystem.IsWindows(), reopened.IsFileFallback);
		Assert.True(SecretProtector.TryUnprotectV2(loaded, "llm_api_key", cipher, out string plain));
		Assert.Equal("existing-secret", plain);
	}

	[Fact]
	public void 无本地文件时仍复用系统密钥环()
	{
		byte[] platform = Enumerable.Repeat((byte)0x42, SecretKeyStore.KeySize).ToArray();
		SecretKeyStore store = new(_dir, () => new(SecretKeyStore.PlatformKeyStatus.Found, platform), _ => throw new InvalidOperationException("不得重建系统密钥"));

		Assert.Equal(platform, store.LoadOrCreate());
		Assert.False(store.IsFileFallback);
		Assert.False(File.Exists(Path.Combine(_dir, "secret.key")));
	}

	[Fact]
	public void 系统密钥环暂不可用时拒绝换钥且冷却后可恢复原密钥()
	{
		byte[] original = Enumerable.Repeat((byte)0x42, SecretKeyStore.KeySize).ToArray();
		long now = 0;
		int reads = 0;
		int writes = 0;
		SecretKeyStore store = new(_dir, () =>
		{
			reads++;
			return reads == 1
				? new(SecretKeyStore.PlatformKeyStatus.Unavailable)
				: new(SecretKeyStore.PlatformKeyStatus.Found, original);
		}, _ =>
		{
			writes++;
			return SecretKeyStore.PlatformKeyWriteStatus.Failed;
		}, () => now);

		SecretKeyStoreException unavailable = Assert.Throws<SecretKeyStoreException>(() => store.LoadOrCreate());
		Assert.False(File.Exists(Path.Combine(_dir, "secret.key")));
		Assert.Equal(0, writes);
		now = 29_999;
		Assert.Same(unavailable, Assert.Throws<SecretKeyStoreException>(() => store.LoadOrCreate()));
		Assert.Equal(1, reads);

		now = 30_000;
		Assert.Equal(original, store.LoadOrCreate());
		Assert.Equal(original, store.LoadOrCreate());
		Assert.Equal(2, reads);
		Assert.Equal(0, writes);
		Assert.False(File.Exists(Path.Combine(_dir, "secret.key")));
	}

	[Fact]
	public void 系统密钥环写入暂不可用时不得回退文件且会冷却()
	{
		long now = 0;
		int reads = 0;
		int writes = 0;
		SecretKeyStore store = new(_dir, () =>
		{
			reads++;
			return new(SecretKeyStore.PlatformKeyStatus.NotFound);
		}, _ =>
		{
			writes++;
			return SecretKeyStore.PlatformKeyWriteStatus.Unavailable;
		}, () => now);

		Assert.Throws<SecretKeyStoreException>(() => store.LoadOrCreate());
		now = 29_999;
		Assert.Throws<SecretKeyStoreException>(() => store.LoadOrCreate());
		Assert.Equal(1, reads);
		Assert.Equal(1, writes);
		Assert.False(File.Exists(Path.Combine(_dir, "secret.key")));
	}

	[Fact]
	public void 损坏的本地密钥不得回退到其他系统密钥或被覆盖()
	{
		string path = Path.Combine(_dir, "secret.key");
		File.WriteAllBytes(path, [1, 2, 3]);
		if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		SecretKeyStore store = new(_dir, () => new(SecretKeyStore.PlatformKeyStatus.Found, new byte[SecretKeyStore.KeySize]), _ => SecretKeyStore.PlatformKeyWriteStatus.Stored);

		Assert.Throws<SecretKeyStoreException>(() => store.LoadOrCreate());
		Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
	}

	[Fact]
	public void Unix回退文件权限仅允许当前用户读写()
	{
		if (OperatingSystem.IsWindows()) return;
		SecretKeyStore store = new(_dir, () => new(SecretKeyStore.PlatformKeyStatus.NotFound), _ => SecretKeyStore.PlatformKeyWriteStatus.Failed);
		store.LoadOrCreate();
		string path = Path.Combine(_dir, "secret.key");

		Assert.True(store.IsFileFallback);
		Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
		File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
		SecretKeyStore reopened = new(_dir, () => new(SecretKeyStore.PlatformKeyStatus.Found, new byte[SecretKeyStore.KeySize]), _ => SecretKeyStore.PlatformKeyWriteStatus.Stored);
		Assert.Throws<SecretKeyStoreException>(() => reopened.LoadOrCreate());
	}

	[Fact]
	public void Windows下密钥落盘并可被新实例读回()
	{
		// macOS/Linux 会优先走系统密钥库 (CI 容器里通常没有), 这里只锁定 Windows 的文件路径行为
		if (!OperatingSystem.IsWindows()) return;

		byte[] created = new SecretKeyStore(_dir).LoadOrCreate();
		Assert.True(File.Exists(Path.Combine(_dir, "secret.key")));
		Assert.Equal(created, new SecretKeyStore(_dir).LoadOrCreate());
	}

	[Fact]
	public async Task 不同实例并发首次读取仍返回同一主密钥()
	{

		using ManualResetEventSlim gate = new(false);
		Task<byte[]>[] tasks = Enumerable.Range(0, 16)
			.Select(_ => Task.Run(() =>
			{
				gate.Wait();
				return new SecretKeyStore(_dir, () => new(SecretKeyStore.PlatformKeyStatus.NotFound), _ => SecretKeyStore.PlatformKeyWriteStatus.Failed).LoadOrCreate();
			}))
			.ToArray();

		gate.Set();
		byte[][] keys = await Task.WhenAll(tasks);
		Assert.All(keys, key => Assert.Equal(keys[0], key));
	}

	[Fact]
	public async Task 同实例并发首次读取只生成一个主密钥()
	{
		SecretKeyStore store = new(_dir, () => new(SecretKeyStore.PlatformKeyStatus.NotFound), _ => SecretKeyStore.PlatformKeyWriteStatus.Failed);
		using ManualResetEventSlim gate = new(false);
		Task<byte[]>[] tasks = Enumerable.Range(0, 16)
			.Select(_ => Task.Run(() =>
			{
				gate.Wait();
				return store.LoadOrCreate();
			}))
			.ToArray();

		gate.Set();
		byte[][] keys = await Task.WhenAll(tasks);
		Assert.All(keys, key => Assert.Equal(keys[0], key));
	}
}
