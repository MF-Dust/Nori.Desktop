using Nori.Core.Chat;

namespace Nori.Core.Tests;

/// <summary>
/// 发给模型服务的 User-Agent。
///
/// 这一条守的是一个查起来很贵的故障：客户端库默认宣称自己是 <c>OpenAI/…</c>，
/// 而网关（实测是 Cloudflare 的托管规则）会按这个串把请求当成 AI 爬虫拦掉，回
/// 403 加一句 "Your request was blocked."。异常里只有状态码，看不出是谁拦的，
/// 所有「配置对不对」的排查方向都是错的 —— 同一把密钥用 curl 打就是 200。
/// 真实请求上的 User-Agent 由 TornadoChatClientTests 覆盖。
/// </summary>
public sealed class ChatClientUserAgentTests
{
	[Fact]
	public void 标识是Nori自己而不是SDK()
	{
		Assert.StartsWith("Nori.Desktop/", ChatClientFactory.UserAgent, StringComparison.Ordinal);
		Assert.DoesNotContain("OpenAI", ChatClientFactory.UserAgent, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 版本号里的 <c>+</c> 和构建元数据在 token 里非法。不裁的话 ParseAdd 会抛，
	/// 而且是抛在**每一次**请求上 —— 等于把发布版的对话功能整个打掉。
	/// </summary>
	[Theory]
	[InlineData("2.0.0", "2.0.0")]
	[InlineData("v2.0.0-Yuuka+3f8ab21", "v2.0.0-Yuuka3f8ab21")]
	[InlineData("Dev", "Dev")]
	[InlineData("2.0 (build 7)", "2.0build7")]
	[InlineData("+++", "Dev")]
	public void 版本号裁成合法token(string version, string expected)
	{
		string sanitized = ChatClientFactory.SanitizeVersion(version);
		Assert.Equal(expected, sanitized);

		// 真正的判据不是字符串相等，而是它进得了 User-Agent 头。
		HttpRequestMessage request = new();
		request.Headers.UserAgent.ParseAdd($"Nori.Desktop/{sanitized}");
		Assert.Single(request.Headers.UserAgent);
	}
}
