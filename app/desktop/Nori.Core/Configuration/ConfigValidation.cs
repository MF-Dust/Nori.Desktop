using System.Text.RegularExpressions;

namespace Nori.Core.Configuration;

/// <summary>
/// 配置边界校验。
///
/// 所有来自桥接、迁移或外部配置文件的枚举值与标识符都在进入存储层前经过这里,
/// 避免把格式错误当成一个可用的默认值。
/// </summary>
public static partial class ConfigValidation
{
	/// <summary>允许的遥测同意状态。</summary>
	public static bool TryParseTelemetryConsent(string? value, out TelemetryConsent consent)
	{
		consent = value?.Trim().ToLowerInvariant() switch
		{
			"unset" => TelemetryConsent.Unset,
			"granted" => TelemetryConsent.Granted,
			"denied" => TelemetryConsent.Denied,
			_ => (TelemetryConsent)(-1),
		};
		return consent is TelemetryConsent.Unset or TelemetryConsent.Granted or TelemetryConsent.Denied;
	}

	/// <summary>把遥测同意状态写成稳定的小写值。</summary>
	public static string TelemetryConsentStorage(TelemetryConsent consent) => consent switch
	{
		TelemetryConsent.Unset => "unset",
		TelemetryConsent.Granted => "granted",
		TelemetryConsent.Denied => "denied",
		_ => throw new ArgumentOutOfRangeException(nameof(consent)),
	};

	/// <summary>校验 MCP 服务器 ID, 该 ID 会参与敏感配置键名。</summary>
	public static bool IsValidMcpServerId(string? value) =>
		!string.IsNullOrWhiteSpace(value)
		&& value.Length <= 96
		&& McpServerIdRegex().IsMatch(value);

	/// <summary>校验 stdio 环境变量名。</summary>
	public static bool IsValidEnvironmentName(string? value) =>
		!string.IsNullOrWhiteSpace(value)
		&& value.Length <= 256
		&& EnvironmentNameRegex().IsMatch(value);

	/// <summary>校验一个配置键是否为敏感字段。</summary>
	public static bool IsSensitiveKey(string key) => ConfigStore.IsSensitiveKey(key);

	[GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
	private static partial Regex McpServerIdRegex();

	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
	private static partial Regex EnvironmentNameRegex();
}

/// <summary>遥测同意状态; 未确认时任何远程 SDK 都必须保持关闭。</summary>
public enum TelemetryConsent
{
	/// <summary>尚未完成一次明确确认。</summary>
	Unset,

	/// <summary>用户明确允许。</summary>
	Granted,

	/// <summary>用户明确拒绝。</summary>
	Denied,
}
