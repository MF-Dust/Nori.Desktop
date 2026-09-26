using Nori.Core.Resources;

namespace Nori.Core.Tests;

/// <summary>
/// 资源名称校验, 对应 Rust 版 validate_resource_name
/// </summary>
public class ResourceNameTests
{
	[Theory]
	[InlineData("arg-nori", true)]
	[InlineData("nori", true)]
	[InlineData("", false)]
	[InlineData(".", false)]
	[InlineData("..", false)]
	[InlineData("a/b", false)]
	[InlineData("a\\b", false)]
	[InlineData("C:", false)]
	public void 名称校验(string name, bool expected) =>
		Assert.Equal(expected, ResourceName.IsValid(name));

	[Fact]
	public void 控制字符被拒绝() => Assert.False(ResourceName.IsValid("a\u0001b"));
}
