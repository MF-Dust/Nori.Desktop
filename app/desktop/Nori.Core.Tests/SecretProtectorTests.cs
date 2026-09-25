using Nori.Core.Security;

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
		string cipher = ProtectLegacyNsec1(Key(0x2B), "legacy");

		Assert.StartsWith(SecretProtector.LegacyNsec1Prefix, cipher, StringComparison.Ordinal);
		Assert.True(SecretProtector.TryUnprotect(Key(0x2B), "any_key", cipher, out string plain));
		Assert.Equal("legacy", plain);
	}

	/// <summary>测试本地的 nsec1 造数: base64(nonce|cipher|tag), 无 AAD, 与已发布格式一致。</summary>
	private static string ProtectLegacyNsec1(byte[] key, string plainText)
	{
		const int nonceSize = 12;
		const int tagSize = 16;
		byte[] nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(nonceSize);
		byte[] plain = System.Text.Encoding.UTF8.GetBytes(plainText);
		byte[] cipher = new byte[plain.Length];
		byte[] tag = new byte[tagSize];
		using System.Security.Cryptography.AesGcm aes = new(key, tagSize);
		aes.Encrypt(nonce, plain, cipher, tag);
		byte[] payload = new byte[nonceSize + cipher.Length + tagSize];
		nonce.CopyTo(payload.AsSpan(0, nonceSize));
		cipher.CopyTo(payload.AsSpan(nonceSize, cipher.Length));
		tag.CopyTo(payload.AsSpan(nonceSize + cipher.Length, tagSize));
		return SecretProtector.LegacyNsec1Prefix + Convert.ToBase64String(payload);
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
		SecretKeyStore store = new(_dir);
		byte[] first = store.LoadOrCreate();
		byte[] second = store.LoadOrCreate();

		Assert.Equal(SecretKeyStore.KeySize, first.Length);
		Assert.Equal(first, second);
		Assert.NotEqual(new byte[SecretKeyStore.KeySize], first);
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
		if (!OperatingSystem.IsWindows()) return;

		using ManualResetEventSlim gate = new(false);
		Task<byte[]>[] tasks = Enumerable.Range(0, 16)
			.Select(_ => Task.Run(() =>
			{
				gate.Wait();
				return new SecretKeyStore(_dir).LoadOrCreate();
			}))
			.ToArray();

		gate.Set();
		byte[][] keys = await Task.WhenAll(tasks);
		Assert.All(keys, key => Assert.Equal(keys[0], key));
	}

	[Fact]
	public async Task 同实例并发首次读取只生成一个主密钥()
	{
		SecretKeyStore store = new(_dir);
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
