using System.IO.Compression;
using Nori.Core.Resources;

namespace Nori.Core.Tests;

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
