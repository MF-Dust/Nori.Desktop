using System.Security.Cryptography;
using System.Text;

namespace Nori.Core.Security;

/// <summary>
/// 可移植的本机静态加密。
///
/// 当前配置格式为 <c>nsec2:&lt;base64(nonce(12) | ciphertext | tag(16))&gt;</c>,
/// 配置键作为 AES-GCM 的 AAD, 因此同一段密文不能被复制到另一个配置键下使用。
/// 旧的 <c>nsec1:</c> 格式仍然只读兼容, 读取后由 ConfigStore 惰性迁移。
///
/// 安全边界: 本机静态加密只防 nori.db 被单独拷走, 不防同一用户下已经能读取
/// 应用进程内存或密钥库的其他进程。
/// </summary>
public static class SecretProtector
{
	/// <summary>当前格式前缀。</summary>
	public const string Prefix = "nsec2:";

	/// <summary>旧 AES-GCM 格式前缀 (只读兼容, 无 AAD)。</summary>
	public const string LegacyNsec1Prefix = "nsec1:";

	/// <summary>旧 DPAPI 格式前缀 (只读兼容)。</summary>
	public const string LegacyDpapiPrefix = "enc:dpapi:";

	private const int NonceSize = 12;
	private const int TagSize = 16;

	/// <summary>是否为 nsec1 或 nsec2 密文。</summary>
	public static bool IsProtected(string stored) => IsNsec1(stored) || IsNsec2(stored);

	/// <summary>是否为当前 nsec2 密文。</summary>
	public static bool IsNsec2(string? stored) => stored?.StartsWith(Prefix, StringComparison.Ordinal) == true;

	/// <summary>是否为旧 nsec1 密文。</summary>
	public static bool IsNsec1(string? stored) => stored?.StartsWith(LegacyNsec1Prefix, StringComparison.Ordinal) == true;

	/// <summary>是否为旧 DPAPI 格式。</summary>
	public static bool IsLegacyDpapi(string? stored) => stored?.StartsWith(LegacyDpapiPrefix, StringComparison.Ordinal) == true;

	/// <summary>
	/// 使用当前格式加密不带配置键的独立值。
	/// 配置存储应调用带 configKey 的重载, 以便把键绑定为 AAD。
	/// </summary>
	public static string Protect(ReadOnlySpan<byte> key, string plainText) => ProtectV2(key, string.Empty, plainText);

	/// <summary>使用配置键作为 AAD 的当前格式加密。</summary>
	public static string Protect(ReadOnlySpan<byte> key, string configKey, string plainText) => ProtectV2(key, configKey, plainText);

	/// <summary>使用配置键作为 AAD 的当前格式加密。</summary>
	public static string ProtectV2(ReadOnlySpan<byte> key, string configKey, string plainText)
	{
		if (plainText.Length == 0) return plainText;
		byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
		byte[] plain = Encoding.UTF8.GetBytes(plainText);
		byte[] cipher = new byte[plain.Length];
		byte[] tag = new byte[TagSize];
		byte[] aad = Encoding.UTF8.GetBytes(configKey ?? string.Empty);

		using AesGcm aes = new(key, TagSize);
		aes.Encrypt(nonce, plain, cipher, tag, aad);
		return Prefix + EncodePayload(nonce, cipher, tag);
	}

	/// <summary>
	/// 解密兼容入口。nsec2 使用空 AAD, 配置存储应使用带 configKey 的重载。
	/// </summary>
	public static bool TryUnprotect(ReadOnlySpan<byte> key, string stored, out string plainText)
	{
		if (IsNsec2(stored)) return TryUnprotectV2(key, string.Empty, stored, out plainText);
		return TryUnprotectV1(key, stored, out plainText);
	}

	/// <summary>按配置键 AAD 解密 nsec2, 同时兼容无 AAD 的 nsec1。</summary>
	public static bool TryUnprotect(ReadOnlySpan<byte> key, string configKey, string stored, out string plainText)
	{
		if (IsNsec2(stored)) return TryUnprotectV2(key, configKey, stored, out plainText);
		return TryUnprotectV1(key, stored, out plainText);
	}

	/// <summary>按配置键 AAD 解密当前 nsec2 格式。</summary>
	public static bool TryUnprotectV2(ReadOnlySpan<byte> key, string configKey, string stored, out string plainText)
	{
		return TryDecrypt(key, Encoding.UTF8.GetBytes(configKey ?? string.Empty), stored, Prefix, out plainText);
	}

	/// <summary>解密旧 nsec1 格式。</summary>
	public static bool TryUnprotectV1(ReadOnlySpan<byte> key, string stored, out string plainText)
	{
		return TryDecrypt(key, ReadOnlySpan<byte>.Empty, stored, LegacyNsec1Prefix, out plainText);
	}

	private static bool TryDecrypt(
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> aad,
		string stored,
		string prefix,
		out string plainText)
	{
		plainText = string.Empty;
		if (!stored.StartsWith(prefix, StringComparison.Ordinal)) return false;
		try
		{
			byte[] payload = Convert.FromBase64String(stored[prefix.Length..]);
			if (payload.Length < NonceSize + TagSize) return false;

			int cipherLength = payload.Length - NonceSize - TagSize;
			ReadOnlySpan<byte> nonce = payload.AsSpan(0, NonceSize);
			ReadOnlySpan<byte> cipher = payload.AsSpan(NonceSize, cipherLength);
			ReadOnlySpan<byte> tag = payload.AsSpan(NonceSize + cipherLength, TagSize);
			byte[] plain = new byte[cipherLength];

			using AesGcm aes = new(key, TagSize);
			aes.Decrypt(nonce, cipher, tag, plain, aad);
			plainText = Encoding.UTF8.GetString(plain);
			return true;
		}
		catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
		{
			return false;
		}
	}

	private static string EncodePayload(byte[] nonce, byte[] cipher, byte[] tag)
	{
		byte[] payload = new byte[NonceSize + cipher.Length + TagSize];
		nonce.CopyTo(payload.AsSpan(0, NonceSize));
		cipher.CopyTo(payload.AsSpan(NonceSize, cipher.Length));
		tag.CopyTo(payload.AsSpan(NonceSize + cipher.Length, TagSize));
		return Convert.ToBase64String(payload);
	}
}
