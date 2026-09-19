using System.Diagnostics.CodeAnalysis;
using Nori.Core.Network;

namespace Nori.Core.Tests;

/// <summary>
/// AnySearch 端点/凭据策略: 存储密钥只跟随官方端点
/// </summary>
[SuppressMessage("Security", "S5332", Justification = "HTTP 地址是非 HTTPS 拒绝测试夹具，不会发起请求。")]
public class AnySearchRequestPolicyTests
{
	[Fact]
	public void 官方端点可回退使用存储密钥()
	{
		AnySearchRequest request = AnySearchRequestPolicy.Resolve(null, null, "sk-stored");
		Assert.Equal(AnySearchRequestPolicy.OfficialEndpoint, request.Endpoint.ToString());
		Assert.Equal("sk-stored", request.ApiKey);

		// 显式传入的官方端点同样允许回退
		request = AnySearchRequestPolicy.Resolve(AnySearchRequestPolicy.OfficialEndpoint, null, "sk-stored");
		Assert.Equal("sk-stored", request.ApiKey);
	}

	[Fact]
	public void 显式密钥优先于存储密钥()
	{
		AnySearchRequest request = AnySearchRequestPolicy.Resolve(null, "sk-explicit", "sk-stored");
		Assert.Equal("sk-explicit", request.ApiKey);
	}

	[Fact]
	public void 自定义端点必须显式携带密钥()
	{
		AnySearchRequest request = AnySearchRequestPolicy.Resolve("https://relay.example.com/v1/search", "sk-mine", "sk-stored");
		Assert.Equal("https://relay.example.com/v1/search", request.Endpoint.ToString());
		Assert.Equal("sk-mine", request.ApiKey);
	}

	[Fact]
	public void 自定义端点缺少密钥时拒绝而不是外发存储密钥()
	{
		InvalidOperationException error = Assert.Throws<InvalidOperationException>(
			() => AnySearchRequestPolicy.Resolve("https://relay.example.com/v1/search", null, "sk-stored"));
		Assert.Contains("API Key", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void 非HTTPS端点一律拒绝()
	{
		Assert.Throws<InvalidOperationException>(
			() => AnySearchRequestPolicy.Resolve("http://api.anysearch.com/v1/search", null, "sk-stored"));
	}

	[Fact]
	public void 非法端点拒绝()
	{
		Assert.Throws<InvalidOperationException>(
			() => AnySearchRequestPolicy.Resolve("not-a-uri", "sk-explicit", null));
	}
}
