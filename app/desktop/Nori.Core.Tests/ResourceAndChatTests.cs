using System.IO.Compression;
using Nori.Core.Chat;
using Nori.Core.Resources;

namespace Nori.Core.Tests;

/// <summary>
/// 动作标记解析, 对应 Rust 版 chat.rs 的 extract_motion_markers
/// </summary>
public class MotionMarkersTests
{
	[Fact]
	public void 剥离单个标记()
	{
		(string content, IReadOnlyList<string> motions) = MotionMarkers.Extract("你好呀。\n[nori_motion:smile]");
		Assert.Equal("你好呀。\n", content);
		Assert.Equal(["smile"], motions);
	}

	[Fact]
	public void 剥离多个标记并保留其余文本()
	{
		(string content, IReadOnlyList<string> motions) = MotionMarkers.Extract("A[nori_motion:a]B[nori_motion:b]C");
		Assert.Equal("ABC", content);
		Assert.Equal(["a", "b"], motions);
	}

	[Fact]
	public void 标记名去空白()
	{
		(_, IReadOnlyList<string> motions) = MotionMarkers.Extract("[nori_motion:  smile  ]");
		Assert.Equal(["smile"], motions);
	}

	[Fact]
	public void 空标记名被忽略()
	{
		(string content, IReadOnlyList<string> motions) = MotionMarkers.Extract("A[nori_motion:]B");
		Assert.Equal("AB", content);
		Assert.Empty(motions);
	}

	[Fact]
	public void 未闭合的标记原样保留()
	{
		(string content, IReadOnlyList<string> motions) = MotionMarkers.Extract("A[nori_motion:smile");
		Assert.Equal("A[nori_motion:smile", content);
		Assert.Empty(motions);
	}

	[Fact]
	public void 没有标记时原样返回()
	{
		(string content, IReadOnlyList<string> motions) = MotionMarkers.Extract("普通回复");
		Assert.Equal("普通回复", content);
		Assert.Empty(motions);
	}
}

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

public class ResourceImportTests
{
	[Fact]
	public void Import_从本地ZIP导入模型()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-import-{Guid.NewGuid():N}");
		string zipPath = Path.Combine(tempDir, "pack.zip");
		Directory.CreateDirectory(tempDir);
		try
		{
			using (FileStream stream = File.Create(zipPath))
			using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
			{
				using (StreamWriter writer = new(archive.CreateEntry("ARGNori_web/ARGNori.model3.json").Open()))
				{
					writer.Write("""{"Version":3,"FileReferences":{"Moc":"ARGNori.moc3","Textures":["tex/0.png"]}}""");
				}
				using (StreamWriter writer = new(archive.CreateEntry("ARGNori_web/ARGNori.moc3").Open()))
				{
					writer.Write("MOC3");
				}
				using (StreamWriter writer = new(archive.CreateEntry("ARGNori_web/tex/0.png").Open()))
				{
					writer.Write("img");
				}
			}

			ResourceManager manager = new(tempDir);
			IReadOnlyList<string> imported = manager.Import(ResourceType.Live2D, zipPath);

			Assert.Contains("arg-nori", imported);
			Assert.True(manager.IsInstalled(ResourceType.Live2D, "arg-nori"));
			Assert.True(File.Exists(Path.Combine(tempDir, "resources", "live2d", "arg-nori", "ARGNori.model3.json")));
			Assert.True(File.Exists(Path.Combine(tempDir, "resources", "live2d", "arg-nori", "tex", "0.png")));
		}
		finally
		{
			Directory.Delete(tempDir, true);
		}
	}

	[Fact]
	public void Import_覆盖导入后新内容替换旧模型()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-overwrite-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		try
		{
			ResourceManager manager = new(tempDir);

			string v1Zip = Path.Combine(tempDir, "v1.zip");
			WriteModelZip(v1Zip, "ARGNori_web", "ARGNori.model3.json", "{}", "old-texture");
			Assert.Contains("arg-nori", manager.Import(ResourceType.Live2D, v1Zip));
			string target = Path.Combine(tempDir, "resources", "live2d", "arg-nori");
			Assert.Equal("old-texture", File.ReadAllText(Path.Combine(target, "tex", "0.png")));

			string v2Zip = Path.Combine(tempDir, "v2.zip");
			WriteModelZip(v2Zip, "ARGNori_web", "ARGNori.model3.json", "{}", "new-texture");
			Assert.Contains("arg-nori", manager.Import(ResourceType.Live2D, v2Zip));

			Assert.Equal("new-texture", File.ReadAllText(Path.Combine(target, "tex", "0.png")));
			Assert.True(manager.IsInstalled(ResourceType.Live2D, "arg-nori"));
			Assert.Single(manager.List(ResourceType.Live2D), info => info.Name == "arg-nori");
		}
		finally
		{
			Directory.Delete(tempDir, true);
		}
	}

	[Fact]
	public void Import_目录导入覆盖旧资源()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-dir-import-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		try
		{
			ResourceManager manager = new(tempDir);

			string sourceV1 = Path.Combine(tempDir, "src-v1", "ARGNori_web");
			Directory.CreateDirectory(sourceV1);
			File.WriteAllText(Path.Combine(sourceV1, "ARGNori.model3.json"),
				"{\"FileReferences\":{\"Moc\":\"ARGNori.moc3\",\"Textures\":[\"tex/0.png\"]}}");
			File.WriteAllText(Path.Combine(sourceV1, "ARGNori.moc3"), "MOC3");
			Directory.CreateDirectory(Path.Combine(sourceV1, "tex"));
			File.WriteAllText(Path.Combine(sourceV1, "tex", "0.png"), "v1");
			manager.Import(ResourceType.Live2D, Path.Combine(tempDir, "src-v1"));

			string sourceV2 = Path.Combine(tempDir, "src-v2", "ARGNori_web");
			Directory.CreateDirectory(sourceV2);
			File.WriteAllText(Path.Combine(sourceV2, "ARGNori.model3.json"),
				"{\"FileReferences\":{\"Moc\":\"ARGNori.moc3\",\"Textures\":[\"tex/0.png\"]}}");
			File.WriteAllText(Path.Combine(sourceV2, "ARGNori.moc3"), "MOC3");
			Directory.CreateDirectory(Path.Combine(sourceV2, "tex"));
			File.WriteAllText(Path.Combine(sourceV2, "tex", "0.png"), "v2");
			manager.Import(ResourceType.Live2D, Path.Combine(tempDir, "src-v2"));

			Assert.Equal("v2", File.ReadAllText(Path.Combine(tempDir, "resources", "live2d", "arg-nori", "tex", "0.png")));
		}
		finally
		{
			Directory.Delete(tempDir, true);
		}
	}

	[Fact]
	public void Import_多候选中途失败时回滚全部并清理临时目录()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-rollback-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		try
		{
			ResourceManager manager = new(tempDir);

			// 预置一个可用的旧 arg-nori 模型
			string oldZip = Path.Combine(tempDir, "old.zip");
			WriteModelZip(oldZip, "ARGNori_web", "ARGNori.model3.json", "{}", "old-texture");
			manager.Import(ResourceType.Live2D, oldZip);
			string oldTexture = Path.Combine(tempDir, "resources", "live2d", "arg-nori", "tex", "0.png");
			Assert.Equal("old-texture", File.ReadAllText(oldTexture));

			// 用同名文件阻塞第二个候选的交换, 模拟交换中途失败
			string live2dRoot = Path.Combine(tempDir, "resources", "live2d");
			File.WriteAllText(Path.Combine(live2dRoot, "nori"), "blocker");

			string packZip = Path.Combine(tempDir, "pack.zip");
			using (FileStream stream = File.Create(packZip))
			using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
			{
				using (StreamWriter writer = new(archive.CreateEntry("ARGNori_web/ARGNori.model3.json").Open()))
				{
					writer.Write("{\"FileReferences\":{\"Moc\":\"ARGNori.moc3\",\"Textures\":[\"tex/0.png\"]}}");
				}
				using (StreamWriter writer = new(archive.CreateEntry("ARGNori_web/ARGNori.moc3").Open()))
				{
					writer.Write("MOC3");
				}
				using (StreamWriter writer = new(archive.CreateEntry("ARGNori_web/tex/0.png").Open()))
				{
					writer.Write("new-texture");
				}
				using (StreamWriter writer = new(archive.CreateEntry("Nori_pack/Nori.model3.json").Open()))
				{
					writer.Write("{\"FileReferences\":{\"Moc\":\"Nori.moc3\",\"Textures\":[]}}");
				}
				using (StreamWriter writer = new(archive.CreateEntry("Nori_pack/Nori.moc3").Open()))
				{
					writer.Write("MOC3");
				}
			}

			ResourceException error = Assert.Throws<ResourceException>(() => manager.Import(ResourceType.Live2D, packZip));
			Assert.Contains("导入资源失败", error.Message, StringComparison.Ordinal);

			// 旧模型完整保留, 新内容没有写入
			Assert.True(manager.IsInstalled(ResourceType.Live2D, "arg-nori"));
			Assert.Equal("old-texture", File.ReadAllText(oldTexture));
			Assert.False(manager.IsInstalled(ResourceType.Live2D, "nori"));

			// staging 与 backup 目录都被清理, 不出现在已安装列表中
			Assert.Empty(Directory.GetDirectories(tempDir, ".nori-*", SearchOption.AllDirectories));
			Assert.Single(manager.List(ResourceType.Live2D), info => info.Name == "arg-nori");
		}
		finally
		{
			Directory.Delete(tempDir, true);
		}
	}

	[Fact]
	public void Import_不支持的模型ID被整体拒绝()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-unsupported-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		try
		{
			string zipPath = Path.Combine(tempDir, "dup.zip");
			using (FileStream stream = File.Create(zipPath))
			using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
			{
				foreach (string name in new[] {"a/model/model.model3.json", "b/model/model.model3.json"})
				{
					using StreamWriter writer = new(archive.CreateEntry(name).Open());
					writer.Write("{}");
				}
			}

			ResourceManager manager = new(tempDir);
			ResourceException error = Assert.Throws<ResourceException>(() => manager.Import(ResourceType.Live2D, zipPath));
			Assert.Contains("不支持的 Live2D 模型 ID", error.Message, StringComparison.Ordinal);
			Assert.Empty(manager.List(ResourceType.Live2D));
			Assert.Empty(Directory.GetDirectories(tempDir, ".nori-*", SearchOption.AllDirectories));
		}
		finally
		{
			Directory.Delete(tempDir, true);
		}
	}

	[Fact]
	public void Import_model3越界引用被拒绝且不覆盖旧资源()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-invalid-reference-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		try
		{
			ResourceManager manager = new(tempDir);
			string zipPath = Path.Combine(tempDir, "invalid.zip");
			using (FileStream stream = File.Create(zipPath))
			using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
			{
				using StreamWriter writer = new(archive.CreateEntry("ARGNori/ARGNori.model3.json").Open());
				writer.Write("{\"FileReferences\":{\"Moc\":\"../outside.moc3\"}}");
			}

			ResourceException error = Assert.Throws<ResourceException>(() => manager.Import(ResourceType.Live2D, zipPath));
			Assert.Contains("路径穿越", error.Message, StringComparison.Ordinal);
			Assert.False(manager.IsInstalled(ResourceType.Live2D, "arg-nori"));
			Assert.Empty(Directory.GetDirectories(tempDir, ".nori-*", SearchOption.AllDirectories));
		}
		finally
		{
			Directory.Delete(tempDir, true);
		}
	}

	[Fact]
	public void Import_取消后不留下staging或资源()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-cancel-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		try
		{
			string zipPath = Path.Combine(tempDir, "cancel.zip");
			WriteModelZip(zipPath, "ARGNori_web", "ARGNori.model3.json", "{}", "texture");
			using CancellationTokenSource cancellation = new();
			cancellation.Cancel();
			ResourceManager manager = new(tempDir);
			Assert.Throws<OperationCanceledException>(() => manager.Import(ResourceType.Live2D, zipPath, cancellation.Token));
			Assert.Empty(manager.List(ResourceType.Live2D));
			Assert.Empty(Directory.GetDirectories(tempDir, ".nori-*", SearchOption.AllDirectories));
		}
		finally
		{
			Directory.Delete(tempDir, true);
		}
	}

	[Fact]
	public void Import_目录中的符号链接被拒绝()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), $"nori-symlink-{Guid.NewGuid():N}");
		string sourceDir = Path.Combine(tempDir, "source");
		string outsideDir = Path.Combine(tempDir, "outside");
		Directory.CreateDirectory(sourceDir);
		Directory.CreateDirectory(outsideDir);
		try
		{
			File.WriteAllText(Path.Combine(sourceDir, "ARGNori.model3.json"), "{}");
			File.WriteAllText(Path.Combine(outsideDir, "outside.txt"), "outside");
			try
			{
				Directory.CreateSymbolicLink(Path.Combine(sourceDir, "linked"), outsideDir);
			}
			catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
			{
				return;
			}

			ResourceManager manager = new(tempDir);
			ResourceException error = Assert.Throws<ResourceException>(() => manager.Import(ResourceType.Live2D, sourceDir));
			Assert.Contains("符号链接", error.Message, StringComparison.Ordinal);
			Assert.Empty(manager.List(ResourceType.Live2D));
		}
		finally
		{
			Directory.Delete(tempDir, true);
		}
	}

	/// <summary>构造一个单模型 ZIP: model3 + moc3 + tex/0.png.</summary>
	private static void WriteModelZip(string zipPath, string folder, string modelJson, string json, string texture)
	{
		using FileStream stream = File.Create(zipPath);
		using ZipArchive archive = new(stream, ZipArchiveMode.Create);
		string mocName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(modelJson)) + ".moc3";
		string modelContent = json == "{}"
			? $"{{\"FileReferences\":{{\"Moc\":\"{mocName}\",\"Textures\":[\"tex/0.png\"]}}}}"
			: json;
		using (StreamWriter writer = new(archive.CreateEntry($"{folder}/{modelJson}").Open()))
		{
			writer.Write(modelContent);
		}
		using (StreamWriter writer = new(archive.CreateEntry($"{folder}/{mocName}").Open()))
		{
			writer.Write("MOC3");
		}
		using (StreamWriter writer = new(archive.CreateEntry($"{folder}/tex/0.png").Open()))
		{
			writer.Write(texture);
		}
	}
}

public class ChatServiceTests : IDisposable
{
	private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return Task.FromResult(handler(request));
		}
	}

	private readonly string _path = Path.Combine(Path.GetTempPath(), $"nori-chat-test-{Guid.NewGuid():N}.db");
	private readonly Nori.Core.Data.NoriDatabase _database;
	private readonly Nori.Core.Configuration.ConfigStore _config;

	public ChatServiceTests()
	{
		_database = Nori.Core.Data.NoriDatabase.Open(_path);
		_config = new Nori.Core.Configuration.ConfigStore(_database);
		_config.InitDefaults("0.1.0");
	}

	public void Dispose()
	{
		_database.Dispose();
		try
		{
			File.Delete(_path);
		}
		catch (IOException)
		{
		}
		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task ChatService_StreamAsync流式回调与动作提取()
	{
		using MockHttpMessageHandler handler = new(req =>
		{
			string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"主人好呀！\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"[nori_motion:smile]\"}}]}\n\ndata: [DONE]\n\n";
			return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
			{
				Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
			};
		});

		using HttpClient client = new(handler);
		ChatService chat = new(client, _database, _config);

		List<string> chunks = [];
		List<string> motions = [];

		string final = await chat.StreamAsync(
			"openai",
			"https://api.openai.com/v1",
			"key",
			"gpt-4o",
			[new ChatMessageInput {Role = "user", Content = "hello"}],
			chunk => chunks.Add(chunk),
			motion => motions.Add(motion));

		Assert.Equal("主人好呀！", final);
		Assert.Equal(["smile"], motions);
		Assert.Equal(2, chat.GetHistory().Count);
	}

	[Fact]
	public async Task ChatService_普通调用不持久化仍返回动作结果()
	{
		using MockHttpMessageHandler handler = new(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
		{
			Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"reply[nori_motion:smile]\"}}]}",
				System.Text.Encoding.UTF8, "application/json")
		});
		using HttpClient client = new(handler);
		ChatService chat = new(client, _database, _config);
		List<string> motions = [];

		string reply = await chat.CompleteAsync("openai", "https://api.openai.com/v1", "key", "gpt-4o",
			[new ChatMessageInput("user", "hello")], motions.Add, persist: false);

		Assert.Equal("reply", reply);
		Assert.Equal(["smile"], motions);
		Assert.Empty(chat.GetHistory());
	}

	[Fact]
	public async Task ChatService_普通和流式调用使用相同输入校验()
	{
		using HttpClient client = new();
		ChatService chat = new(client, _database, _config);
		ChatMessageInput[] messages = [new("user", "hello")];
		ChatException complete = await Assert.ThrowsAsync<ChatException>(() =>
			chat.CompleteAsync("openai", "", "key", "gpt-4o", messages, _ => { }));
		ChatException stream = await Assert.ThrowsAsync<ChatException>(() =>
			chat.StreamAsync("openai", "", "key", "gpt-4o", messages, _ => { }, _ => { }));
		Assert.Equal(complete.Message, stream.Message);
		Assert.Empty(chat.GetHistory());
	}

	[Fact]
	public void ChatService_SaveAndClearHistory()
	{
		using HttpClient client = new();
		ChatService chat = new(client, _database, _config);

		chat.SaveMessage("user", "你好呀");
		chat.SaveMessage("assistant", "主人好！");

		IReadOnlyList<ChatMessage> history = chat.GetHistory();
		Assert.Equal(2, history.Count);
		Assert.Equal("user", history[0].Role);
		Assert.Equal("你好呀", history[0].Content);
		Assert.Equal("assistant", history[1].Role);
		Assert.Equal("主人好！", history[1].Content);

		chat.ClearHistory();
		Assert.Empty(chat.GetHistory());
	}

	[Fact]
	public void ChatService_GetHistoryPagedReturnsLatestPageInOrder()
	{
		using HttpClient client = new();
		ChatService chat = new(client, _database, _config);

		for (int i = 1; i <= 5; i++) chat.SaveMessage("user", $"msg{i}");

		// 首页: 取最新的 2 条, 返回时间正序
		IReadOnlyList<ChatMessage> page = chat.GetHistory(2, 0);
		Assert.Equal(2, page.Count);
		Assert.Equal("msg4", page[0].Content);
		Assert.Equal("msg5", page[1].Content);

		// 翻页: 以本页最早一条的 id 为游标继续向前取
		IReadOnlyList<ChatMessage> older = chat.GetHistory(2, page[0].Id);
		Assert.Equal(2, older.Count);
		Assert.Equal("msg2", older[0].Content);
		Assert.Equal("msg3", older[1].Content);

		// 剩余不足一页时返回余量, 取完之后返回空页
		IReadOnlyList<ChatMessage> last = chat.GetHistory(2, older[0].Id);
		Assert.Single(last);
		Assert.Equal("msg1", last[0].Content);
		Assert.Empty(chat.GetHistory(2, last[0].Id));
	}
}
