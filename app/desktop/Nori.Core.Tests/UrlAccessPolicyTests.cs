using System.Diagnostics.CodeAnalysis;
using System.Net;
using Nori.Core.Network;

namespace Nori.Core.Tests;

[SuppressMessage("Security", "S5332", Justification = "HTTP 地址用于验证网络地址策略边界，调用方另行执行 HTTPS 契约。")]
public class UrlAccessPolicyTests
{
	[Theory]
	[InlineData("https://example.com/page")]
	[InlineData("http://api.anysearch.com/v1/search")]
	[InlineData("http://127.0.0.1:8080/api")]
	[InlineData("http://localhost/x")]
	[InlineData("http://169.254.1.1/metadata")]
	[InlineData("http://192.168.1.10/router")]
	public void HTTP地址不再按网络地址策略拒绝(string url) =>
		UrlAccessPolicy.EnsurePublicHttp(new Uri(url));

	[Theory]
	[InlineData("ftp://example.com/file")]
	[InlineData("file:///etc/passwd")]
	public void 非HTTP地址仍然拒绝(string url)
	{
		InvalidOperationException error = Assert.Throws<InvalidOperationException>(
			() => UrlAccessPolicy.EnsurePublicHttp(new Uri(url)));
		Assert.Contains("仅支持 http/https", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void 允许私网参数保持兼容()
	{
		UrlAccessPolicy.EnsureAllowed(new Uri("http://127.0.0.1:9880/tts"), allowPrivate: true);
		UrlAccessPolicy.EnsureAllowed(new Uri("http://localhost:11434/api"), allowPrivate: false);
	}

	[Fact]
	public async Task 官方SDK响应包装拒绝超限正文()
	{
		using HttpContent original = new StringContent(new string('x', 32));
		using HttpContent capped = UrlAccessPolicy.WrapResponseContent(original, 8);

		await Assert.ThrowsAsync<InvalidOperationException>(() => capped.ReadAsStringAsync());
	}

	[Fact]
	public void 网络异常翻译为稳定消息()
	{
		InvalidOperationException exception = UrlAccessPolicy.Translate(
			new HttpRequestException("连接失败"),
			new Uri("https://example.com"));
		Assert.Equal("访问公网地址失败: example.com", exception.Message);
	}
}
