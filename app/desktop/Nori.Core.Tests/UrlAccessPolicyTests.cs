using System.Net;
using Microsoft.Security.AntiSSRF;
using Nori.Core.Network;

namespace Nori.Core.Tests;

public class UrlAccessPolicyTests
{
	[Theory]
	[InlineData("https://example.com/page")]
	[InlineData("http://api.anysearch.com/v1/search")]
	public void 公网地址允许(string url) =>
		UrlAccessPolicy.EnsurePublicHttp(new Uri(url));

	[Theory]
	[InlineData("ftp://example.com/file", "仅支持 http/https")]
	[InlineData("file:///etc/passwd", "仅支持 http/https")]
	[InlineData("http://127.0.0.1:8080/api", "私网或保留地址")]
	[InlineData("http://localhost/x", "私网或保留地址")]
	[InlineData("http://169.254.1.1/metadata", "私网或保留地址")]
	[InlineData("http://192.168.1.10/router", "私网或保留地址")]
	[InlineData("http://10.0.0.5/internal", "私网或保留地址")]
	[InlineData("http://172.16.0.9/internal", "私网或保留地址")]
	[InlineData("http://[::1]/v6", "私网或保留地址")]
	[InlineData("http://224.0.0.1/group", "私网或保留地址")]
	public void 危险地址被拒绝(string url, string reason)
	{
		InvalidOperationException error = Assert.Throws<InvalidOperationException>(
			() => UrlAccessPolicy.EnsurePublicHttp(new Uri(url)));
		Assert.Contains(reason, error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void 允许私网时本地端点放行()
	{
		// 本地 LLM / GPT-SoVITS 端点走 allowPrivate
		UrlAccessPolicy.EnsureAllowed(new Uri("http://127.0.0.1:9880/tts"), allowPrivate: true);
		UrlAccessPolicy.EnsureAllowed(new Uri("http://localhost:11434/api"), allowPrivate: true);
	}

	[Fact]
	public void IPv4映射的IPv6回环按IPv4规则拒绝() =>
		Assert.Throws<InvalidOperationException>(() =>
			UrlAccessPolicy.EnsurePublicHttp(new Uri("http://[::ffff:127.0.0.1]/x")));

	[Theory]
	[InlineData("8.8.8.8")]
	[InlineData("1.1.1.1")]
	[InlineData("172.32.0.1")]
	[InlineData("192.0.1.1")]
	[InlineData("2001:200::1")]
	[InlineData("2001:4860:4860::8888")]
	[InlineData("2606:4700:4700::1111")]
	[InlineData("3ff0::1")]
	[InlineData("3fff:1000::1")]
	[InlineData("::ffff:8.8.8.8")]
	public void 推荐黑名单以外的IP地址允许(string address) =>
		Assert.False(UrlAccessPolicy.IsRestricted(IPAddress.Parse(address)));

	[Theory]
	[InlineData("0.0.0.0")]
	[InlineData("127.0.0.1")]
	[InlineData("::")]
	[InlineData("::1")]
	[InlineData("::ffff:127.0.0.1")]
	[InlineData("::ffff:192.168.1.1")]
	[InlineData("64:ff9b::808:808")]
	[InlineData("64:ff9b:1::1")]
	[InlineData("100::1")]
	[InlineData("100:0:0:1::1")]
	[InlineData("192.0.0.1")]
	[InlineData("168.63.129.16")]
	[InlineData("2001:1ff::1")]
	[InlineData("2001:db8::1")]
	[InlineData("2002::1")]
	[InlineData("2620:4f:8000::1")]
	[InlineData("3fff:fff::1")]
	[InlineData("5f00::1")]
	[InlineData("fc00::1")]
	[InlineData("fe80::1")]
	[InlineData("fec0::1")]
	[InlineData("ff02::1")]
	public void 推荐黑名单内的IP地址受限(string address) =>
		Assert.True(UrlAccessPolicy.IsRestricted(IPAddress.Parse(address)));

	[Fact]
	public void 推荐黑名单所有范围的首尾地址均受限()
	{
		foreach (string cidr in IPAddressRanges.recommendedLatest)
		{
			IPNetwork network = IPNetwork.Parse(cidr);
			byte[] endBytes = network.BaseAddress.GetAddressBytes();
			for (int bit = network.PrefixLength; bit < endBytes.Length * 8; bit++)
				endBytes[bit / 8] |= (byte)(1 << (7 - bit % 8));
			IPAddress last = new(endBytes);

			Assert.True(UrlAccessPolicy.IsRestricted(network.BaseAddress), $"{cidr} 首地址未拦截");
			Assert.True(UrlAccessPolicy.IsRestricted(last), $"{cidr} 尾地址未拦截");
			Assert.True(UrlAccessPolicy.IsRestricted(network.BaseAddress.MapToIPv6()), $"{cidr} 映射首地址未拦截");
			Assert.True(UrlAccessPolicy.IsRestricted(last.MapToIPv6()), $"{cidr} 映射尾地址未拦截");
		}
	}

	[Fact]
	public void DNS解析结果包含受限地址时被拒绝()
	{
		NetworkAddressPolicyException exception = Assert.Throws<NetworkAddressPolicyException>(() =>
			UrlAccessPolicy.EnsureResolvedPublicAddresses(
				"example.com", [IPAddress.Parse("8.8.8.8"), IPAddress.Loopback]));
		Assert.Contains("安全策略", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void DNS解析结果全部为公网地址时允许()
	{
		Exception? exception = Record.Exception(() =>
			UrlAccessPolicy.EnsureResolvedPublicAddresses(
				"example.com", [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1"), IPAddress.Parse("2001:4860:4860::8888")]));
		Assert.Null(exception);
	}

	[Fact]
	public void DNS安全异常翻译为稳定消息()
	{
		InvalidOperationException exception = UrlAccessPolicy.Translate(
			new HttpRequestException("连接失败", new NetworkAddressPolicyException("内部地址")),
			new Uri("https://example.com"));
		Assert.Equal("公网地址被安全策略拒绝: example.com", exception.Message);
	}
}
