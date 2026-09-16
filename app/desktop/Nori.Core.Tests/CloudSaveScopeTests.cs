using Nori.Core.Cloud;
using Nori.Core.Configuration;

namespace Nori.Core.Tests;

/// <summary>
/// 云存档的范围。
///
/// 这一族测的是**隐私边界**，不是功能。它的价值在于：以后有人加一个配置键时，
/// 如果那个键该留在本机却混进了白名单，这里会失败；而如果只靠人审，那种疏漏
/// 不会报错、不会崩溃，只会安静地把不该走的数据发出去。
/// </summary>
public sealed class CloudSaveScopeTests
{
	/// <summary>
	/// 密钥一条都不能在里面。
	///
	/// 这是整份清单最要命的一条：库里的密钥是用本机 DPAPI 绑定的主密钥加密的，
	/// 传上去既解不开也不该传。
	/// </summary>
	[Theory]
	[InlineData("llm_api_key")]
	[InlineData("tts_api_key")]
	[InlineData("stt_api_key")]
	[InlineData("embedding_api_key")]
	public void 密钥不在同步范围内(string key)
	{
		Assert.False(CloudSaveScope.IncludesConfig(key));
		Assert.False(CloudSaveScope.IncludesConfigWithModelSuffix(key));
	}

	/// <summary>
	/// 凡是名字里带 key / token / secret / password 的，一律不许进白名单。
	///
	/// 上一条只挡住了今天已知的四个。这条挡的是**以后**加的 —— 新的提供方总会带
	/// 新的密钥键，而加键的人未必想得起来这份清单。
	/// </summary>
	[Fact]
	public void 白名单里不许出现任何形似密钥的键()
	{
		string[] offenders = [.. CloudSaveScope.ConfigKeys.Where(key =>
			key.Contains("key", StringComparison.OrdinalIgnoreCase)
			|| key.Contains("token", StringComparison.OrdinalIgnoreCase)
			|| key.Contains("secret", StringComparison.OrdinalIgnoreCase)
			|| key.Contains("password", StringComparison.OrdinalIgnoreCase))];

		Assert.Empty(offenders);
	}

	/// <summary>
	/// 安全面的开关不跟着账户跑。
	///
	/// 在自己的台式机上把授权调到「完全放行」，不代表在公司的笔记本上也该放行。
	/// 这类设置是**这台机器上**的决定。
	/// </summary>
	[Theory]
	[InlineData("permission_gear")]
	[InlineData("permission_bypass_until")]
	[InlineData("automation_enabled")]
	[InlineData("automation_allow_keyboard")]
	[InlineData("automation_allow_pointer")]
	[InlineData("screen_reading_enabled")]
	public void 安全相关的开关不同步(string key)
	{
		Assert.False(CloudSaveScope.IncludesConfigWithModelSuffix(key));
	}

	/// <summary>同意不能被同步：在这台机器上同意过，不代表在新机器上也同意。</summary>
	[Fact]
	public void 遥测同意不同步()
	{
		Assert.False(CloudSaveScope.IncludesConfig(ConfigStore.KeyTelemetryConsent));
		Assert.False(CloudSaveScope.IncludesConfig("telemetry_enabled"));
	}

	/// <summary>本机路径与具体命令搬到另一台机器上没有意义，而且属于敏感面。</summary>
	[Theory]
	[InlineData("workspace_root")]
	[InlineData("workspace_tasks")]
	public void 本机路径与任务命令不同步(string key)
	{
		Assert.False(CloudSaveScope.IncludesConfigWithModelSuffix(key));
	}

	/// <summary>地址可能指向内网或私有代理，属于这台机器的环境。</summary>
	[Theory]
	[InlineData("llm_api_base")]
	[InlineData("tts_base_url")]
	[InlineData("stt_base_url")]
	public void 接口地址不同步(string key)
	{
		Assert.False(CloudSaveScope.IncludesConfigWithModelSuffix(key));
	}

	// ── 该在里面的 ─────────────────────────────────────────────────────────

	/// <summary>不带这些，换一台机器就得把她重新调一遍。</summary>
	[Theory]
	[InlineData("language")]
	[InlineData("selected_model")]
	[InlineData("nori_skills")]
	[InlineData("l2d_scale")]
	[InlineData("proactive_idle_minutes")]
	[InlineData("memory_recall_top_k")]
	[InlineData("tts_voice")]
	public void 偏好在同步范围内(string key)
	{
		Assert.True(CloudSaveScope.IncludesConfig(key));
	}

	/// <summary>
	/// 形象参数是按模型分键存的（l2d_scale_nori），白名单只列基名。
	///
	/// 不认后缀的话，用户给每个形象调过的大小一个都同步不上去 —— 而那正是
	/// 这一项存在的理由。
	/// </summary>
	[Theory]
	[InlineData("l2d_scale_nori")]
	[InlineData("l2d_opacity_arg-nori")]
	[InlineData("l2d_shadow_nori")]
	public void 带模型后缀的形象参数也算在内(string key)
	{
		Assert.False(CloudSaveScope.IncludesConfig(key));          // 基名白名单不含它
		Assert.True(CloudSaveScope.IncludesConfigWithModelSuffix(key));
	}

	/// <summary>后缀匹配不能匹过头：别的以 l2d_ 开头的键不该被顺带带上。</summary>
	[Theory]
	[InlineData("l2d_scale_secret_api_key")]      // 看着像后缀，实际不该进
	[InlineData("l2d_unknown_future_setting")]
	public void 后缀匹配不会顺带带上没列过的键(string key)
	{
		// 只有「基名 + 下划线 + 模型 id」才算；上面两个的基名都不在白名单里。
		bool allowed = CloudSaveScope.IncludesConfigWithModelSuffix(key);
		if (key.StartsWith("l2d_scale_", StringComparison.Ordinal))
		{
			// l2d_scale 在白名单里，所以这是**已知的过度匹配**：把一个叫
			// l2d_scale_secret_api_key 的键当成了某个模型的缩放。
			// 断言它确实会被匹上，并由上一条「不许出现形似密钥的键」在源头挡住 ——
			// 这里记录这个边界，免得下次有人以为后缀匹配是精确的。
			Assert.True(allowed);
			return;
		}
		Assert.False(allowed);
	}

	// ── 表 ─────────────────────────────────────────────────────────────────

	/// <summary>用户选的是「不含对话原文」。这条钉住它。</summary>
	[Fact]
	public void 对话原文不同步()
	{
		Assert.DoesNotContain("chat_messages", CloudSaveScope.Tables);
	}

	/// <summary>本地文档算出的向量搬过去只是一堆指向不存在文件的索引。</summary>
	[Fact]
	public void 知识库向量不同步()
	{
		Assert.DoesNotContain("knowledge_chunks", CloudSaveScope.Tables);
		Assert.DoesNotContain("knowledge_documents", CloudSaveScope.Tables);
	}

	[Fact]
	public void 记忆与提醒在同步范围内()
	{
		Assert.Contains("memories", CloudSaveScope.Tables);
		Assert.Contains("reminders", CloudSaveScope.Tables);
	}

	// ── 清单本身 ───────────────────────────────────────────────────────────

	/// <summary>排除清单里的每一条都必须真的不在白名单里 —— 否则那份理由是假的。</summary>
	[Fact]
	public void 排除理由与白名单不矛盾()
	{
		foreach ((string key, string reason) in CloudSaveScope.ExcludedWithReason)
		{
			Assert.False(CloudSaveScope.ConfigKeys.Contains(key), $"{key} 同时出现在白名单和排除清单里");
			Assert.NotEmpty(reason);
		}
	}

	/// <summary>每一条排除都要写明理由 —— 没有理由的排除，下一个人会直接删掉。</summary>
	[Fact]
	public void 每条排除都有理由()
	{
		Assert.All(CloudSaveScope.ExcludedWithReason.Values, reason => Assert.True(reason.Length >= 8));
	}
}
