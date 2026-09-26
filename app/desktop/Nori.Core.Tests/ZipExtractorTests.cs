using System.IO.Compression;
using Nori.Core.Resources;

namespace Nori.Core.Tests;

/// <summary>
/// ZIP 解压安全规则, 对应 Rust 版 downloader.rs 的 sanitize_zip_path / extract_zip
/// </summary>
public class ZipExtractorTests
{
	[Theory]
	[InlineData("a/b/c.png", "a/b/c.png")]
	// 反斜杠归一化
	[InlineData("a\\b\\c.png", "a/b/c.png")]
	// 冗余的 . 与空段被折叠
	[InlineData("a/./b//c.png", "a/b/c.png")]
	public void 合法路径归一化(string raw, string expected) =>
		Assert.Equal(expected, ZipExtractor.SanitizePath(raw));

	[Theory]
	[InlineData("/etc/passwd", "绝对路径")]
	[InlineData("//server/share/x", "UNC")]
	[InlineData("C:/Windows/win.ini", "Windows 绝对路径")]
	[InlineData("a/../../etc/passwd", "路径穿越")]
	public void 非法路径被拒绝(string raw, string reason)
	{
		ResourceException error = Assert.Throws<ResourceException>(() => ZipExtractor.SanitizePath(raw));
		Assert.Contains(reason, error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void 控制字符被拒绝() =>
		Assert.Throws<ResourceException>(() => ZipExtractor.SanitizePath("a/\u0001b.png"));

	[Theory]
	// 所有条目共享同一个顶层目录 → 可以剥
	[InlineData(new[] {"arg-nori/a.json", "arg-nori/tex/b.png"}, "arg-nori")]
	// 顶层不唯一 → 不能剥
	[InlineData(new[] {"arg-nori/a.json", "other/b.png"}, null)]
	// 顶层有文件 → 不能剥
	[InlineData(new[] {"a.json", "arg-nori/b.png"}, null)]
	[InlineData(new[] {"a.json"}, null)]
	public void 识别唯一顶层目录(string[] paths, string? expected) =>
		Assert.Equal(expected, ZipExtractor.FindCommonTopDirectory(paths));

	[Fact]
	public void 解压时剥掉多余顶层目录()
	{
		string root = Path.Combine(Path.GetTempPath(), $"nori-zip-{Guid.NewGuid():N}");
		string zipPath = Path.Combine(root, "pack.zip");
		string target = Path.Combine(root, "out");
		Directory.CreateDirectory(root);
		try
		{
			// 模拟"多包了一层同名目录"的资源包
			using (FileStream stream = File.Create(zipPath))
			using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
			{
				archive.CreateEntry("arg-nori/");
				using (StreamWriter writer = new(archive.CreateEntry("arg-nori/ARGNori.model3.json").Open()))
				{
					writer.Write("""{"Version":3}""");
				}
				using (StreamWriter writer = new(archive.CreateEntry("arg-nori/tex/t.png").Open()))
				{
					writer.Write("x");
				}
			}

			ZipExtractor.Extract(zipPath, target);

			// 顶层目录被剥掉, 文件直接落在目标目录下 —— 前端按 live2d/<id>/<file> 就能取到
			Assert.True(File.Exists(Path.Combine(target, "ARGNori.model3.json")));
			Assert.True(File.Exists(Path.Combine(target, "tex", "t.png")));
			Assert.False(Directory.Exists(Path.Combine(target, "arg-nori")));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Fact]
	public void 顶层不唯一时保持原结构()
	{
		string root = Path.Combine(Path.GetTempPath(), $"nori-zip-{Guid.NewGuid():N}");
		string zipPath = Path.Combine(root, "pack.zip");
		string target = Path.Combine(root, "out");
		Directory.CreateDirectory(root);
		try
		{
			using (FileStream stream = File.Create(zipPath))
			using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
			{
				using (StreamWriter writer = new(archive.CreateEntry("ARGNori.model3.json").Open()))
				{
					writer.Write("{}");
				}
				using (StreamWriter writer = new(archive.CreateEntry("tex/t.png").Open()))
				{
					writer.Write("x");
				}
			}

			ZipExtractor.Extract(zipPath, target);
			Assert.True(File.Exists(Path.Combine(target, "ARGNori.model3.json")));
			Assert.True(File.Exists(Path.Combine(target, "tex", "t.png")));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Fact]
	public void ZIP条目数量超过上限时拒绝()
	{
		string root = Path.Combine(Path.GetTempPath(), $"nori-zip-limit-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		try
		{
			string zipPath = Path.Combine(root, "pack.zip");
			WriteZipEntries(zipPath, ("a.txt", "a"), ("b.txt", "b"));
			ResourceException error = Assert.Throws<ResourceException>(() => ZipExtractor.Extract(
				zipPath,
				Path.Combine(root, "out"),
				new ZipExtractionLimits {MaxEntryCount = 1}));
			Assert.Contains("条目数量", error.Message, StringComparison.Ordinal);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Fact]
	public void ZIP单文件与总展开大小超过上限时拒绝()
	{
		string root = Path.Combine(Path.GetTempPath(), $"nori-zip-size-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		try
		{
			string singleZip = Path.Combine(root, "single.zip");
			WriteZipEntries(singleZip, ("large.txt", "123456789"));
			ResourceException singleError = Assert.Throws<ResourceException>(() => ZipExtractor.Extract(
				singleZip,
				Path.Combine(root, "single-out"),
				new ZipExtractionLimits
				{
					MaxSingleFileBytes = 4,
					MaxTotalUncompressedBytes = 100,
					MaxCompressionRatio = 10_000,
				}));
			Assert.Contains("单个文件", singleError.Message, StringComparison.Ordinal);

			string totalZip = Path.Combine(root, "total.zip");
			WriteZipEntries(totalZip, ("a.txt", "1234"), ("b.txt", "5678"));
			ResourceException totalError = Assert.Throws<ResourceException>(() => ZipExtractor.Extract(
				totalZip,
				Path.Combine(root, "total-out"),
				new ZipExtractionLimits
				{
					MaxSingleFileBytes = 100,
					MaxTotalUncompressedBytes = 5,
					MaxCompressionRatio = 10_000,
				}));
			Assert.Contains("总展开大小", totalError.Message, StringComparison.Ordinal);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Fact]
	public void ZIP符号链接条目被拒绝()
	{
		string root = Path.Combine(Path.GetTempPath(), $"nori-zip-link-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		try
		{
			string zipPath = Path.Combine(root, "link.zip");
			using (FileStream stream = File.Create(zipPath))
			using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
			{
				ZipArchiveEntry entry = archive.CreateEntry("linked.txt");
				entry.ExternalAttributes = unchecked((int)(0xA000u << 16));
				using StreamWriter writer = new(entry.Open());
				writer.Write("target");
			}

			ResourceException error = Assert.Throws<ResourceException>(() => ZipExtractor.Extract(zipPath, Path.Combine(root, "out")));
			Assert.Contains("符号链接", error.Message, StringComparison.Ordinal);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Fact]
	public void ZIP目标父目录符号链接被拒绝()
	{
		string root = Path.Combine(Path.GetTempPath(), $"nori-zip-target-link-{Guid.NewGuid():N}");
		string outside = Path.Combine(root, "outside");
		string targetParent = Path.Combine(root, "linked");
		Directory.CreateDirectory(outside);
		try
		{
			try
			{
				Directory.CreateSymbolicLink(targetParent, outside);
			}
			catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
			{
				return;
			}

			string zipPath = Path.Combine(root, "pack.zip");
			WriteZipEntries(zipPath, ("file.txt", "content"));
			ResourceException error = Assert.Throws<ResourceException>(() =>
				ZipExtractor.Extract(zipPath, Path.Combine(targetParent, "out")));
			Assert.Contains("符号链接", error.Message, StringComparison.Ordinal);
			Assert.False(File.Exists(Path.Combine(outside, "out", "file.txt")));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Fact]
	public void ZIP异常压缩比超过上限时拒绝()
	{
		string root = Path.Combine(Path.GetTempPath(), $"nori-zip-ratio-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		try
		{
			string zipPath = Path.Combine(root, "ratio.zip");
			WriteZipEntries(zipPath, ("repetitive.txt", new string('a', 10_000)));
			ResourceException error = Assert.Throws<ResourceException>(() => ZipExtractor.Extract(
				zipPath,
				Path.Combine(root, "out"),
				new ZipExtractionLimits
				{
					MaxSingleFileBytes = 20_000,
					MaxTotalUncompressedBytes = 20_000,
					MaxCompressionRatio = 2,
				}));
			Assert.Contains("压缩比", error.Message, StringComparison.Ordinal);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	private static void WriteZipEntries(string zipPath, params (string Name, string Content)[] entries)
	{
		using FileStream stream = File.Create(zipPath);
		using ZipArchive archive = new(stream, ZipArchiveMode.Create);
		foreach ((string name, string content) in entries)
		{
			using StreamWriter writer = new(archive.CreateEntry(name).Open());
			writer.Write(content);
		}
	}
}
