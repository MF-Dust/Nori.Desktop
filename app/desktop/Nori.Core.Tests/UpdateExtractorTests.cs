using System.IO.Compression;
using System.Text.Json;
using Nori.Core.Update;
using Xunit;

namespace Nori.Core.Tests;

/// <summary>部署槽解压、元数据比对与原子提交测试。</summary>
public sealed class UpdateExtractorTests : IDisposable
{
	private readonly string _tempRoot;
	private readonly string _packageRoot;
	private readonly string _stagingRoot;

	public UpdateExtractorTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), $"nori-update-test-{Guid.NewGuid():N}");
		_packageRoot = Path.Combine(_tempRoot, "root");
		_stagingRoot = Path.Combine(_packageRoot, "data", "updates", "staging", "test");
		Directory.CreateDirectory(_packageRoot);
		string current = Path.Combine(_packageRoot, "app-1.0.0-0");
		Directory.CreateDirectory(current);
		File.WriteAllText(Path.Combine(current, "deployment.json"), JsonSerializer.Serialize(new
		{
			schema_version = 1, product_version = "1.0.0-test", numeric_version = "1.0.0", revision = 0,
			rid = "win-x64", entrypoint = "Nori.Desktop.exe",
		}));
		File.WriteAllText(Path.Combine(current, "Nori.Desktop.exe"), "旧程序");
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
		}
		catch { }
	}

	[Fact]
	public void ExtractAndCommitSlot_ValidZip_LocksOldCurrentFirst_ThenCommitsNewSlot()
	{
		// 预置旧 .current 指针
		File.WriteAllText(Path.Combine(_packageRoot, ".current"), "app-1.0.0-0\n");

		// 创建测试 ZIP 与对应 UpdateManifest
		string zipPath = Path.Combine(_tempRoot, "update.zip");
		CreateTestZip(zipPath, "app-1.1.0-0", "1.1.0", 0, "win-x64", "Nori.Desktop.exe");

		UpdateManifest manifest = new(
			1,
			"1.1.0-test",
			"1.1.0",
			0,
			"win-x64",
			"v1.1.0-test",
			"update.zip",
			"https://github.com/MF-Dust/Nori-Desktop-Pet/releases/download/v1.1.0-test/update.zip",
			"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
			1024,
			"zip",
			"Nori.Desktop.exe",
			1,
			null,
			null);

		SlotCommitResult result = UpdateExtractor.ExtractAndCommitSlot(
			zipPath,
			_packageRoot,
			"win-x64",
			"app-1.0.0-0",
			manifest,
			_stagingRoot);

		Assert.Equal("app-1.1.0-0", result.SlotName);
		Assert.True(Directory.Exists(result.SlotPath));
		Assert.True(File.Exists(Path.Combine(result.SlotPath, "deployment.json")));
		Assert.True(File.Exists(Path.Combine(result.SlotPath, "Nori.Desktop.exe")));

		// 检查 .current 指针已安全指向新槽
		string current = File.ReadAllText(Path.Combine(_packageRoot, ".current")).Trim();
		Assert.Equal("app-1.1.0-0", current);

		// 检查 staging 已被清理
		Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "app-1.1.0-0")));
	}

	[Fact]
	public void ExtractAndCommitSlot_DuplicatePathsInZip_Throws()
	{
		string zipPath = Path.Combine(_tempRoot, "dup.zip");
		using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
		{
			archive.CreateEntry("app-1.1.0-0/same.txt");
			archive.CreateEntry("app-1.1.0-0/same.txt");
		}

		UpdateManifest manifest = CreateDummyManifest("1.1.0", 0);

		Assert.Throws<InvalidOperationException>(() =>
			UpdateExtractor.ExtractAndCommitSlot(
				zipPath,
				_packageRoot,
				"win-x64",
				"app-1.0.0-0",
				manifest,
				_stagingRoot));
	}

	[Fact]
	public void ExtractAndCommitSlot_DowngradeOrEqualVersion_ThrowsAndPreservesCurrent()
	{
		File.WriteAllText(Path.Combine(_packageRoot, ".current"), "app-1.1.0-0\n");

		string zipPath = Path.Combine(_tempRoot, "update-older.zip");
		CreateTestZip(zipPath, "app-1.0.0-0", "1.0.0", 0, "win-x64", "Nori.Desktop.exe");

		UpdateManifest manifest = CreateDummyManifest("1.0.0", 0);

		// 当前运行是 1.1.0-0，试图安装 1.0.0-0 必须被拦截
		var ex = Assert.Throws<InvalidOperationException>(() =>
			UpdateExtractor.ExtractAndCommitSlot(
				zipPath,
				_packageRoot,
				"win-x64",
				"app-1.1.0-0",
				manifest,
				_stagingRoot));

		Assert.Contains("拒绝安装平级或降级版本", ex.Message);
		Assert.Equal("app-1.1.0-0", File.ReadAllText(Path.Combine(_packageRoot, ".current")).Trim());
	}

	[Fact]
	public void ExtractAndCommitSlot_TargetSlotAlreadyExistsAndValid_ReusesWithoutDestroying()
	{
		File.WriteAllText(Path.Combine(_packageRoot, ".current"), "app-1.0.0-0\n");

		// 目标槽已存在
		string existingSlot = Path.Combine(_packageRoot, "app-1.2.0-0");
		Directory.CreateDirectory(existingSlot);
		File.WriteAllText(Path.Combine(existingSlot, "deployment.json"), JsonSerializer.Serialize(new
		{
			schema_version = 1,
			product_version = "1.2.0-test",
			numeric_version = "1.2.0",
			revision = 0,
			rid = "win-x64",
			entrypoint = "Nori.Desktop.exe",
		}));
		File.WriteAllText(Path.Combine(existingSlot, "Nori.Desktop.exe"), "fake binary payload");

		string zipPath = Path.Combine(_tempRoot, "update-reuse.zip");
		CreateTestZip(zipPath, "app-1.2.0-0", "1.2.0", 0, "win-x64", "Nori.Desktop.exe");
		UpdateManifest manifest = CreateDummyManifest("1.2.0", 0);

		SlotCommitResult result = UpdateExtractor.ExtractAndCommitSlot(
			zipPath,
			_packageRoot,
			"win-x64",
			"app-1.0.0-0",
			manifest,
			_stagingRoot);

		Assert.Equal("app-1.2.0-0", result.SlotName);
		Assert.Equal("app-1.2.0-0", File.ReadAllText(Path.Combine(_packageRoot, ".current")).Trim());
		// 不得产生 .destroy 目录
		Assert.False(Directory.Exists(Path.Combine(_packageRoot, "app-1.2.0-0.destroy")));
	}

	[Fact]
	public void ExtractAndCommitSlot_ManifestMismatch_ThrowsAndFailsClosed()
	{
		File.WriteAllText(Path.Combine(_packageRoot, ".current"), "app-1.0.0-0\n");

		// ZIP 内部是 1.1.0，但 UPDATE 清单期望 1.2.0
		string zipPath = Path.Combine(_tempRoot, "mismatch.zip");
		CreateTestZip(zipPath, "app-1.1.0-0", "1.1.0", 0, "win-x64", "Nori.Desktop.exe");

		UpdateManifest manifest = CreateDummyManifest("1.2.0", 0);

		var ex = Assert.Throws<InvalidOperationException>(() =>
			UpdateExtractor.ExtractAndCommitSlot(
				zipPath,
				_packageRoot,
				"win-x64",
				"app-1.0.0-0",
				manifest,
				_stagingRoot));

		Assert.Contains("与 UPDATE 清单元数据不一致", ex.Message);
		Assert.Equal("app-1.0.0-0", File.ReadAllText(Path.Combine(_packageRoot, ".current")).Trim());
	}

	[Fact]
	public void ExistingSlotWithModifiedPayloadIsNeverReusedOrOverwritten()
	{
		File.WriteAllText(Path.Combine(_packageRoot, ".current"), "app-1.0.0-0\n");
		string zip = Path.Combine(_tempRoot, "reuse.zip");
		CreateTestZip(zip, "app-1.2.0-0", "1.2.0", 0, "win-x64", "Nori.Desktop.exe");
		ZipFile.ExtractToDirectory(zip, _packageRoot);
		string executable = Path.Combine(_packageRoot, "app-1.2.0-0", "Nori.Desktop.exe");
		File.WriteAllText(executable, "损坏或被篡改的程序");
		Assert.Contains("内容", Assert.Throws<InvalidOperationException>(() => UpdateExtractor.ExtractAndCommitSlot(
			zip, _packageRoot, "win-x64", "app-1.0.0-0", CreateDummyManifest("1.2.0", 0), _stagingRoot)).Message);
		Assert.Equal("损坏或被篡改的程序", File.ReadAllText(executable));
		Assert.Equal("app-1.0.0-0", File.ReadAllText(Path.Combine(_packageRoot, ".current")).Trim());
	}

	[Fact]
	public void ProductVersionMismatchIsRejected()
	{
		string zip = Path.Combine(_tempRoot, "product.zip");
		CreateTestZip(zip, "app-1.2.0-0", "1.2.0", 0, "win-x64", "Nori.Desktop.exe");
		Assert.Contains("清单元数据不一致", Assert.Throws<InvalidOperationException>(() => UpdateExtractor.ExtractAndCommitSlot(
			zip, _packageRoot, "win-x64", "app-1.0.0-0", CreateDummyManifest("1.2.0", 0) with { ProductVersion = "伪造版本" }, _stagingRoot)).Message);
		Assert.False(Directory.Exists(Path.Combine(_packageRoot, "app-1.2.0-0")));
	}

	[Theory]
	[InlineData("../outside")]
	[InlineData("/absolute")]
	[InlineData("slot/file:stream")]
	[InlineData("slot/CON.txt")]
	[InlineData("slot/file.")]
	[InlineData("slot/file ")]
	public void UnsafeArchiveNamesAreRejected(string name)
	{
		string zip = Path.Combine(_tempRoot, "unsafe.zip");
		using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry(name);
		Assert.Throws<InvalidOperationException>(() => UpdateExtractor.ExtractAndCommitSlot(
			zip, _packageRoot, "win-x64", "app-1.0.0-0", CreateDummyManifest("1.2.0", 0), _stagingRoot));
	}

	[Theory]
	[InlineData(0xa1ff)]
	[InlineData(0x89ed)]
	public void ZipLinksAndSpecialPermissionsAreRejected(int mode)
	{
		string zip = Path.Combine(_tempRoot, "link.zip");
		using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry("link").ExternalAttributes = mode << 16;
		Assert.Contains("特殊", Assert.Throws<InvalidOperationException>(() => UpdateExtractor.ExtractAndCommitSlot(
			zip, _packageRoot, "win-x64", "app-1.0.0-0", CreateDummyManifest("1.2.0", 0), _stagingRoot)).Message);
	}

	[Fact]
	public void CancellationBeforeCommitPreservesPointerAndOldSlot()
	{
		File.WriteAllText(Path.Combine(_packageRoot, ".current"), "app-1.0.0-0\n");
		string zip = Path.Combine(_tempRoot, "cancel.zip");
		CreateTestZip(zip, "app-1.2.0-0", "1.2.0", 0, "win-x64", "Nori.Desktop.exe");
		Assert.ThrowsAny<OperationCanceledException>(() => UpdateExtractor.ExtractAndCommitSlot(zip, _packageRoot,
			"win-x64", "app-1.0.0-0", CreateDummyManifest("1.2.0", 0), _stagingRoot, new CancellationToken(true)));
		Assert.Equal("app-1.0.0-0", File.ReadAllText(Path.Combine(_packageRoot, ".current")).Trim());
		Assert.False(Directory.Exists(Path.Combine(_packageRoot, "app-1.2.0-0")));
	}

	[Fact]
	public void TarGzUsesManifestTypeEvenWhenTemporaryFilenameHasNoExtension()
	{
		string zip = Path.Combine(_tempRoot, "source.zip");
		CreateTestZip(zip, "app-1.2.0-0", "1.2.0", 0, "win-x64", "Nori.Desktop.exe");
		string source = Path.Combine(_tempRoot, "source");
		ZipFile.ExtractToDirectory(zip, source);
		string tar = Path.Combine(_tempRoot, "download.tmp-123");
		using (FileStream output = File.Create(tar))
		using (GZipStream gzip = new(output, CompressionMode.Compress))
			System.Formats.Tar.TarFile.CreateFromDirectory(source, gzip, false);
		SlotCommitResult result = UpdateExtractor.ExtractAndCommitSlot(tar, _packageRoot, "win-x64", "app-1.0.0-0",
			CreateDummyManifest("1.2.0", 0) with { ArchiveType = "tar.gz" }, _stagingRoot);
		Assert.Equal("app-1.2.0-0", result.SlotName);
	}

	[Theory]
	[InlineData(System.Formats.Tar.TarEntryType.SymbolicLink)]
	[InlineData(System.Formats.Tar.TarEntryType.HardLink)]
	public void TarLinksAreRejected(System.Formats.Tar.TarEntryType type)
	{
		string tar = Path.Combine(_tempRoot, "link.tar.gz");
		using (FileStream output = File.Create(tar))
		using (GZipStream gzip = new(output, CompressionMode.Compress))
		using (System.Formats.Tar.TarWriter writer = new(gzip))
			writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(type, "link") { LinkName = "outside" });
		Assert.Contains("特殊文件", Assert.Throws<InvalidOperationException>(() => UpdateExtractor.ExtractAndCommitSlot(tar,
			_packageRoot, "win-x64", "app-1.0.0-0", CreateDummyManifest("1.2.0", 0) with { ArchiveType = "tar.gz" }, _stagingRoot)).Message);
	}

	private static UpdateManifest CreateDummyManifest(string numeric, int revision) => new(
		1,
		$"{numeric}-test",
		numeric,
		revision,
		"win-x64",
		$"v{numeric}-test",
		"package.zip",
		$"https://github.com/MF-Dust/Nori-Desktop-Pet/releases/download/v{numeric}-test/package.zip",
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
		1024,
		"zip",
		"Nori.Desktop.exe",
		1,
		null,
		null);

	private static void CreateTestZip(
		string zipPath,
		string slotDirName,
		string numericVersion,
		int revision,
		string rid,
		string entrypoint)
	{
		using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
		var manifestObj = new
		{
			schema_version = 1,
			product_version = $"{numericVersion}-test",
			numeric_version = numericVersion,
			revision = revision,
			rid = rid,
			entrypoint = entrypoint,
		};
		ZipArchiveEntry manifestEntry = archive.CreateEntry($"{slotDirName}/deployment.json");
		using (Stream stream = manifestEntry.Open())
		{
			JsonSerializer.Serialize(stream, manifestObj);
		}

		ZipArchiveEntry entrypointEntry = archive.CreateEntry($"{slotDirName}/{entrypoint}");
		using StreamWriter writer = new(entrypointEntry.Open());
		writer.Write("fake binary payload");
	}
}
