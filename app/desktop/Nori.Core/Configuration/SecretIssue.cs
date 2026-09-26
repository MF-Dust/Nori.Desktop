namespace Nori.Core.Configuration;

/// <summary>敏感配置读取时发现的问题分类。</summary>
public enum SecretIssueCategory
{
	/// <summary>没有问题。</summary>
	None,

	/// <summary>读取到了旧的 nsec1 格式, 已可用但等待升级。</summary>
	LegacyNsec1,

	/// <summary>读取到了旧的 Windows DPAPI 格式, 已可用但等待升级。</summary>
	LegacyDpapi,

	/// <summary>读取到了历史明文值, 已可用但等待加密迁移。</summary>
	LegacyPlaintext,

	/// <summary>密钥库不可用, 不能读取或写入敏感配置。</summary>
	KeyStoreUnavailable,

	/// <summary>密文格式错误、密钥不匹配或完整性校验失败。</summary>
	CorruptCiphertext,

	/// <summary>旧格式在当前平台不受支持。</summary>
	LegacyUnsupported,
}

/// <summary>不包含敏感值的配置问题摘要。</summary>
public sealed record SecretIssue(string Key, SecretIssueCategory Category)
{
	/// <summary>给 UI 或日志使用的固定分类文案。</summary>
	public string Code => Category switch
	{
		SecretIssueCategory.LegacyNsec1 => "legacy_nsec1",
		SecretIssueCategory.LegacyDpapi => "legacy_dpapi",
		SecretIssueCategory.LegacyPlaintext => "legacy_plaintext",
		SecretIssueCategory.KeyStoreUnavailable => "keystore_unavailable",
		SecretIssueCategory.CorruptCiphertext => "corrupt_ciphertext",
		SecretIssueCategory.LegacyUnsupported => "legacy_unsupported",
		_ => "none",
	};

	/// <summary>是否必须由用户重新填写。</summary>
	public bool RequiresUserAction => Category is SecretIssueCategory.KeyStoreUnavailable
		or SecretIssueCategory.CorruptCiphertext
		or SecretIssueCategory.LegacyUnsupported;
}

/// <summary>敏感配置读取结果; Value 为空时该项不会被视为已配置。</summary>
public sealed record SecretReadResult(string? Value, SecretIssueCategory Issue)
{
	/// <summary>当前是否有可用的敏感值。</summary>
	public bool IsConfigured => !string.IsNullOrEmpty(Value)
		&& Issue is not SecretIssueCategory.KeyStoreUnavailable
		and not SecretIssueCategory.CorruptCiphertext
		and not SecretIssueCategory.LegacyUnsupported;
}
