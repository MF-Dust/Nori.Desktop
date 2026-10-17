using System.Reflection;
using Nori.Core;

namespace Nori.Core.Tests;

/// <summary>验证构建 informational version 在运行时完整保留。</summary>
public sealed class ProductVersionTests
{
	[Fact]
	public void Current与程序集informational_version一致并保留元数据()
	{
		string? assemblyVersion = typeof(ProductVersion).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

		Assert.False(string.IsNullOrWhiteSpace(assemblyVersion));
		Assert.Equal(assemblyVersion, ProductVersion.Current);
	}
}
