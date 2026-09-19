using System.Diagnostics.CodeAnalysis;
using Nori.Core.Telemetry;

namespace Nori.Core.Tests;

/// <summary>遥测操作名与凭据脱敏纯函数测试。</summary>
[SuppressMessage("Security", "S2068", Justification = "伪凭据用于验证遥测脱敏。")]
public sealed class TelemetrySanitizerTests
{
	[Fact]
	public void 操作名只保留稳定字符并限制长度()
	{
		string normalized = TelemetrySanitizer.NormalizeOperation("bridge.chat_start/用户消息");

		Assert.Equal("bridge.chat_start", normalized);
		Assert.True(TelemetrySanitizer.NormalizeOperation(new string('x', 200)).Length <= 80);
		Assert.Equal("operation", TelemetrySanitizer.NormalizeOperation("中文操作"));
	}

	[Fact]
	public void 普通诊断文本会移除密钥凭据和路径()
	{
		// 脱敏测试必须包含会被清除的伪凭据与用户信息。
		string raw = "api_key=sk-secret https://user:password@example.com/v1?token=token-secret /home/user/nori.db";
		string scrubbed = TelemetrySanitizer.ScrubText(raw);

		Assert.DoesNotContain("sk-secret", scrubbed, StringComparison.Ordinal);
		Assert.DoesNotContain("password@example", scrubbed, StringComparison.Ordinal);
		Assert.DoesNotContain("token-secret", scrubbed, StringComparison.Ordinal);
		Assert.DoesNotContain("/home/user/nori.db", scrubbed, StringComparison.Ordinal);
		Assert.Contains("[redacted]", scrubbed, StringComparison.Ordinal);
		Assert.Contains("[path]", scrubbed, StringComparison.Ordinal);
	}

	[Fact]
	public void 异常正文只保留异常类型不保留聊天内容()
	{
		InvalidOperationException exception = new("用户的聊天内容和提示词");

		string value = TelemetrySanitizer.SanitizeExceptionValue(exception);

		Assert.DoesNotContain("聊天内容", value, StringComparison.Ordinal);
		Assert.Equal(typeof(InvalidOperationException).FullName, value);
	}

	[Fact]
	public void 标签值归一化为小写安全字符并限制长度()
	{
		Assert.Equal("http_status", TelemetrySanitizer.NormalizeTag("HTTP_Status"));
		Assert.Equal("plugin.not-found", TelemetrySanitizer.NormalizeTag("Plugin.Not-Found"));
		Assert.Equal(string.Empty, TelemetrySanitizer.NormalizeTag("用户"));
		Assert.Equal(string.Empty, TelemetrySanitizer.NormalizeTag("  "));
		Assert.Equal(string.Empty, TelemetrySanitizer.NormalizeTag(null));
		Assert.True(TelemetrySanitizer.NormalizeTag(new string('x', 200)).Length <= 64);
	}

	[Fact]
	public void 标签白名单外的键被丢弃()
	{
		Dictionary<string, string> tags = new()
		{
			["failure_kind"] = "timeout",
			["operation"] = "bridge.test",
			["user_content"] = "聊天正文",
			["api_key"] = "sk-secret",
			["PLUGIN_ID"] = "cloud-music",
			["hresult"] = "0x800711C7",
			["assembly"] = "Blocked.dll",
			["type_name"] = "Some.Plugin.MissingType",
		};

		IReadOnlyDictionary<string, string> safe = TelemetrySanitizer.NormalizeTags(tags);

		Assert.Equal("timeout", safe["failure_kind"]);
		Assert.Equal("bridge.test", safe["operation"]);
		Assert.Equal("cloud-music", safe["plugin_id"]);
		Assert.Equal("0x800711c7", safe["hresult"]);
		Assert.Equal("blocked.dll", safe["assembly"]);
		Assert.Equal("some.plugin.missingtype", safe["type_name"]);
		Assert.False(safe.ContainsKey("user_content"));
		Assert.False(safe.ContainsKey("api_key"));
	}

	[Fact]
	public void 空标签集合返回空结果()
	{
		Assert.Empty(TelemetrySanitizer.NormalizeTags(null));
		Assert.Empty(TelemetrySanitizer.NormalizeTags(new Dictionary<string, string>()));
		Assert.Empty(TelemetrySanitizer.NormalizeTags(new Dictionary<string, string> { ["provider"] = "" }));
	}
}
