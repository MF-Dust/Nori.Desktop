using System.Reflection;
using Nori.PluginRuntime;

namespace Nori.PluginRuntime.Tests;

/// <summary>插件失败脱敏诊断装配测试 (NORI-24 TypeLoadException / 相关加载失败)。</summary>
public sealed class PluginDiagnosticsTests
{
	[Fact]
	public void TypeLoad失败附带类型名与宿主信息()
	{
		PluginException exception = new(
			PluginErrorCodes.ActivationFailed,
			"插件激活失败: CloudMusic",
			TypeLoad("Could not load type 'CloudMusic.MissingService'."));

		PluginDiagnostics.Attach(exception, "cloud-music", "1.4.0", "2.0", "v1.4.0+abcdef0");

		Assert.Equal("System.TypeLoadException", exception.DiagnosticExceptionType);
		Assert.Contains("CloudMusic.MissingService", exception.DiagnosticTypeLoadTypeName, StringComparison.Ordinal);
		Assert.Equal("cloud-music", exception.DiagnosticPluginId);
		Assert.Equal("1.4.0", exception.DiagnosticPluginVersion);
		Assert.Equal("2.0", exception.DiagnosticHostApiVersion);
		Assert.Equal("v1.4.0+abcdef0", exception.DiagnosticHostVersion);
		Assert.Null(exception.DiagnosticAssemblyName);
	}

	[Theory]
	[InlineData(@"C:\plugins\demo\vendor\Locked.dll", "Locked.dll", "C:")]
	[InlineData("/home/user/.nori/plugins/demo/vendor/Locked.so", "Locked.so", "/home")]
	public void FileLoad失败只附带程序集文件名不带完整路径(string filePath, string expectedName, string forbiddenFragment)
	{
		PluginException exception = new(
			PluginErrorCodes.ActivationFailed,
			"插件激活失败: Demo",
			new FileLoadException("Could not load file.", filePath));

		PluginDiagnostics.Attach(exception, "demo", "0.1.0", "2.0", "Dev");

		Assert.Equal("System.IO.FileLoadException", exception.DiagnosticExceptionType);
		Assert.Equal(expectedName, exception.DiagnosticAssemblyName);
		Assert.DoesNotContain("vendor", exception.DiagnosticAssemblyName, StringComparison.Ordinal);
		Assert.DoesNotContain(forbiddenFragment, exception.DiagnosticAssemblyName, StringComparison.Ordinal);
	}

	[Fact]
	public void 无根因异常时仅记录宿主与插件信息()
	{
		PluginException exception = new(PluginErrorCodes.PackagePathDenied, "插件包路径被拒绝: ../escape");

		PluginDiagnostics.Attach(exception, "demo", "0.1.0", "2.0", "Dev");

		Assert.Equal("Nori.PluginRuntime.PluginException", exception.DiagnosticExceptionType);
		Assert.Equal("demo", exception.DiagnosticPluginId);
		Assert.Null(exception.DiagnosticTypeLoadTypeName);
		Assert.Null(exception.DiagnosticAssemblyName);
	}

	[Fact]
	public void 嵌套包装链取根因异常类型()
	{
		PluginException exception = new(
			PluginErrorCodes.ActivationFailed,
			"插件激活失败: Demo",
			new InvalidOperationException("包装", TypeLoad("Could not load type 'Demo.Deep.Type'.")));

		PluginDiagnostics.Attach(exception, "demo", null, "2.0", "Dev");

		Assert.Equal("System.TypeLoadException", exception.DiagnosticExceptionType);
		Assert.Contains("Demo.Deep.Type", exception.DiagnosticTypeLoadTypeName, StringComparison.Ordinal);
	}

	[Fact]
	public void TypeName被截断到200字符()
	{
		PluginException exception = new(
			PluginErrorCodes.ActivationFailed, "插件激活失败: Demo",
			TypeLoad(new string('t', 500)));

		PluginDiagnostics.Attach(exception, "demo", null, "2.0", "Dev");

		Assert.NotNull(exception.DiagnosticTypeLoadTypeName);
		Assert.True(exception.DiagnosticTypeLoadTypeName!.Length <= 200);
	}

	/// <summary>真实类型加载失败时 TypeName 由运行时填充; 测试里用反射模拟同样的内部字段。</summary>
	private static TypeLoadException TypeLoad(string typeName)
	{
		TypeLoadException exception = new($"Could not load type '{typeName}'.");
		FieldInfo? field = typeof(TypeLoadException)
			.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
			.FirstOrDefault(candidate => candidate.FieldType == typeof(string) &&
				(candidate.Name.Contains("className", StringComparison.OrdinalIgnoreCase) ||
				 candidate.Name.Contains("typeName", StringComparison.OrdinalIgnoreCase)));
		Assert.NotNull(field);
		field!.SetValue(exception, typeName);
		return exception;
	}
}
