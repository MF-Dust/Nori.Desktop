using Nori.Core.Assets;

namespace Nori.Core.Tests;

/// <summary>
/// 资源路径安全逻辑, 对应 Rust 版 asset.rs.
/// 这些规则是防路径穿越的最后一道防线, 放宽任意一条都会变成任意文件读取.
/// </summary>
public class AssetPathTests
{
	[Theory]
	[InlineData("live2d/arg-nori/model.json", true)]
	[InlineData("a", true)]
	// Unix 绝对路径
	[InlineData("/etc/passwd", false)]
	// UNC
	[InlineData("\\\\server\\share", false)]
	// Windows 盘符
	[InlineData("C:/Windows/win.ini", false)]
	[InlineData("C:\\Windows\\win.ini", false)]
	[InlineData("C:", false)]
	// 路径穿越
	[InlineData("../secret", false)]
	[InlineData("live2d/../../secret", false)]
	[InlineData("live2d/./model.json", false)]
	// 反斜杠形式的穿越同样要拦
	[InlineData("live2d\\..\\..\\secret", false)]
	[InlineData("", false)]
	public void 安全相对路径判定(string path, bool expected) =>
		Assert.Equal(expected, AssetPath.IsSafeRelativePath(path));

	[Theory]
	[InlineData("/live2d/a.moc3", "/live2d/a.moc3")]
	[InlineData("/live2d/%E6%A8%A1%E5%9E%8B.json", "/live2d/模型.json")]
	[InlineData("a%2Fb", "a/b")]
	public void 百分号解码(string input, string expected) =>
		Assert.Equal(expected, AssetPath.PercentDecode(input));

	[Theory]
	// % 后面不是合法 HEX
	[InlineData("a%ZZ")]
	[InlineData("a%")]
	[InlineData("a%4")]
	public void 非法百分号编码返回null(string input) =>
		Assert.Null(AssetPath.PercentDecode(input));

	[Theory]
	[InlineData("a.model3.json", "application/json; charset=utf-8")]
	[InlineData("a.moc3", "application/octet-stream")]
	[InlineData("a.PNG", "image/png")]
	[InlineData("index.html", "text/html; charset=utf-8")]
	[InlineData("app.js", "text/javascript; charset=utf-8")]
	[InlineData("unknown.xyz", "application/octet-stream")]
	public void MIME映射(string path, string expected) =>
		Assert.Equal(expected, AssetPath.MimeFor(path));

	[Fact]
	public void ResolveExact命中真实文件并挡住穿越()
	{
		string root = Path.Combine(Path.GetTempPath(), $"nori-asset-{Guid.NewGuid():N}");
		try
		{
			string nested = Path.Combine(root, "live2d", "arg-nori", "arg-nori");
			Directory.CreateDirectory(nested);
			string file = Path.Combine(nested, "tex.png");
			File.WriteAllText(file, "x");

			Assert.Equal(file, AssetPath.ResolveExact(root, "live2d/arg-nori/arg-nori/tex.png"));

			Directory.CreateDirectory(Path.Combine(root, "live2d", "flat"));
			string flat = Path.Combine(root, "live2d", "flat", "a.json");
			File.WriteAllText(flat, "{}");
			Assert.Equal(flat, AssetPath.ResolveExact(root, "live2d/flat/a.json"));

			Assert.Null(AssetPath.ResolveExact(root, "../../secret"));
			Assert.Null(AssetPath.ResolveExact(root, "live2d/missing.png"));
			Assert.Null(AssetPath.ResolveExact(root, "live2d/arg-nori"));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	[Fact]
	public void ResolveExact拒绝祖先符号链接()
	{
		string root = Path.Combine(Path.GetTempPath(), $"nori-asset-link-{Guid.NewGuid():N}");
		string outside = Path.Combine(root, "outside");
		string link = Path.Combine(root, "link");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "secret.txt"), "TOP SECRET");
		try
		{
			try
			{
				Directory.CreateSymbolicLink(link, outside);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
			{
				// 某些 Windows 环境没有创建符号链接权限，平台安全测试由 ResourcePathSafetyTests 覆盖。
				return;
			}

			Assert.Null(AssetPath.ResolveExact(root, "link/secret.txt"));
		}
		finally
		{
			try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
			try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
		}
	}
}
