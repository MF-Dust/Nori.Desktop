using Nori.Desktop.Diagnostics;

namespace Nori.Desktop.Tests;

/// <summary>加载类失败 (NORI-1X / NORI-24) 的安全诊断标签测试。</summary>
public sealed class LoadFailureDiagnosticTests
{
	[Theory]
	[InlineData(@"C:\app\vendor\Blocked.dll", "Blocked.dll")]
	[InlineData("/opt/nori/vendor/Blocked.so", "Blocked.so")]
	public void FileLoad失败标签只暴露文件名与HRESULT(string path, string expectedAssembly)
	{
		Dictionary<string, string>? tags = CrashReporter.LoadFailureTags(
			new FileLoadException("Could not load file.", path)) as Dictionary<string, string>;

		Assert.NotNull(tags);
		Assert.Equal("file_load", tags!["exception_kind"]);
		Assert.Equal(expectedAssembly, tags["assembly"]);
		Assert.StartsWith("0x", tags["hresult"], StringComparison.Ordinal);
		Assert.DoesNotContain("vendor", tags["assembly"], StringComparison.Ordinal);
	}

	[Fact]
	public void TypeLoad失败标签含类型名()
	{
		Dictionary<string, string>? tags = CrashReporter.LoadFailureTags(
			TypeLoad("Some.Plugin.MissingType")) as Dictionary<string, string>;

		Assert.NotNull(tags);
		Assert.Equal("type_load", tags!["exception_kind"]);
		Assert.Equal("Some.Plugin.MissingType", tags["type_name"]);
	}

	[Fact]
	public void 内层加载失败也能被识别()
	{
		Dictionary<string, string>? tags = CrashReporter.LoadFailureTags(
			new InvalidOperationException("包装", new FileLoadException("blocked", "OnlyName.dll"))) as Dictionary<string, string>;

		Assert.NotNull(tags);
		Assert.Equal("OnlyName.dll", tags!["assembly"]);
	}

	[Fact]
	public void 普通异常不产生加载标签()
	{
		Assert.Null(CrashReporter.LoadFailureTags(new NullReferenceException()));
		Assert.Null(CrashReporter.LoadFailureTags(new InvalidOperationException("测试")));
	}

	/// <summary>用现有测试程序集中的不存在类型触发运行时真实的加载失败。</summary>
	private static TypeLoadException TypeLoad(string typeName) =>
		Assert.Throws<TypeLoadException>(() => Type.GetType(
			$"{typeName}, {typeof(LoadFailureDiagnosticTests).Assembly.FullName}", throwOnError: true));
}
