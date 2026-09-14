using System.Net;
using System.Net.Http.Headers;
using Nori.Core.Chat;

namespace Nori.Core.Tests;

/// <summary>
/// 发给模型服务的 User-Agent。
///
/// 这一条守的是一个查起来很贵的故障：provider SDK 默认宣称自己是 <c>OpenAI/…</c>，
/// 而网关（实测是 Cloudflare 的托管规则）会按这个串把请求当成 AI 爬虫拦掉，回
/// 403 加一句 "Your request was blocked."。异常里只有状态码，看不出是谁拦的，
/// 所有「配置对不对」的排查方向都是错的 —— 同一把密钥用 curl 打就是 200。
/// </summary>
public sealed class ChatClientUserAgentTests
{
	/// <summary>把收到的请求头记下来就回 200，用来看真正发出去的是什么。</summary>
	private sealed class CapturingHandler : HttpMessageHandler
	{
		public HttpRequestHeaders? Seen { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Seen = request.Headers;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
				RequestMessage = request,
			});
		}
	}

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
	[InlineData("", "Dev")]
	public void 版本号裁成合法token(string version, string expected)
	{
		string sanitized = ChatClientFactory.SanitizeVersion(version);
		Assert.Equal(expected, sanitized);

		// 真正的判据不是字符串相等，而是它进得了 User-Agent 头。
		HttpRequestMessage request = new();
		request.Headers.UserAgent.ParseAdd($"Nori.Desktop/{sanitized}");
		Assert.Single(request.Headers.UserAgent);
	}

	/// <summary>
	/// 真正走一次转发链路：provider SDK 写好的 User-Agent 必须被换掉，而不是
	/// 追加在后面 —— Cloudflare 那条规则匹配的是子串，追加一样会被拦。
	/// </summary>
	[Fact]
	public async Task 转发时覆盖掉SDK写的那一条()
	{
		CapturingHandler capturing = new();
		using HttpClient shared = new(capturing);
		using HttpClient provider = ChatClientFactory.CreateProviderHttpClient(shared);

		using HttpRequestMessage request = new(HttpMethod.Post, "https://example.invalid/v1/chat/completions")
		{
			Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
		};
		request.Headers.UserAgent.ParseAdd("OpenAI/2.5.0");

		using HttpResponseMessage response = await provider.SendAsync(request, CancellationToken.None);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string sent = string.Join(" ", capturing.Seen!.UserAgent.Select(part => part.ToString()));
		Assert.Equal(ChatClientFactory.UserAgent, sent);
		Assert.DoesNotContain("OpenAI", sent, StringComparison.OrdinalIgnoreCase);
	}
}
