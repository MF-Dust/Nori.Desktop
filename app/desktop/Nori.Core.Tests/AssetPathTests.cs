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

	[Fact]
	public void 候选路径优先原始路径再逐层删目录()
	{
		IReadOnlyList<string> candidates = AssetPath.PathCandidates("live2d/arg-nori/arg-nori/tex.png");
		Assert.Equal("live2d/arg-nori/arg-nori/tex.png", candidates[0]);
		// 删掉第二层后应命中真实布局
		Assert.Contains("live2d/arg-nori/tex.png", candidates);
	}

	[Theory]
	[InlineData("a/b")]
	[InlineData("a")]
	public void 少于三段时不做候选展开(string path) =>
		Assert.Equal([path], AssetPath.PathCandidates(path));

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
	public void Resolve命中真实文件并挡住穿越()
	{
		string root = Path.Combine(Path.GetTempPath(), $"nori-asset-{Guid.NewGuid():N}");
		try
		{
			// 构造 live2d/arg-nori/arg-nori/tex.png (多包了一层), 请求走少一层的路径也要命中
			string nested = Path.Combine(root, "live2d", "arg-nori", "arg-nori");
			Directory.CreateDirectory(nested);
			string file = Path.Combine(nested, "tex.png");
			File.WriteAllText(file, "x");

			Assert.Equal(file, AssetPath.Resolve(root, "live2d/arg-nori/arg-nori/tex.png"));

			// 反过来: 请求少一层的路径, 候选展开后应命中同一个文件
			Directory.CreateDirectory(Path.Combine(root, "live2d", "flat"));
			string flat = Path.Combine(root, "live2d", "flat", "a.json");
			File.WriteAllText(flat, "{}");
			Assert.Equal(flat, AssetPath.Resolve(root, "live2d/flat/a.json"));

			// 穿越与不存在
			Assert.Null(AssetPath.Resolve(root, "../../secret"));
			Assert.Null(AssetPath.Resolve(root, "live2d/missing.png"));
			// 目录不是文件
			Assert.Null(AssetPath.Resolve(root, "live2d/arg-nori"));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	[Fact]
	public void Resolve拒绝祖先符号链接()
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

			Assert.Null(AssetPath.Resolve(root, "link/secret.txt"));
		}
		finally
		{
			try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
			try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
		}
	}
}
