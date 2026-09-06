using Nori.Core.Update;
using Xunit;

namespace Nori.Core.Tests;

/// <summary>更新清单解析与版本比对单元测试。</summary>
public sealed class UpdateManifestTests
{
	[Fact]
	public void FromJson_ValidManifest_ParsesCorrectly()
	{
		string json = """
		{
			"schema_version": 1,
			"product_version": "1.2.3-codename",
			"numeric_version": "1.2.3",
			"revision": 0,
			"rid": "win-x64",
			"release_tag": "v1.2.3-codename",
			"package_name": "nori-1.2.3-win-x64.zip",
			"download_url": "https://github.com/MF-Dust/Nori-Desktop-Pet/releases/download/v1.2.3-codename/nori-1.2.3-win-x64.zip",
			"sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
			"size_bytes": 1048576,
			"entrypoint": "Nori.Desktop.exe",
			"launcher_protocol": 1,
			"archive_type": "zip",
			"published_at": "2025-05-18T12:00:00Z",
			"release_notes": "初始发布"
		}
		""";

		UpdateManifest manifest = UpdateManifest.FromJson(json);

		Assert.Equal(1, manifest.SchemaVersion);
		Assert.Equal("1.2.3-codename", manifest.ProductVersion);
		Assert.Equal("1.2.3", manifest.NumericVersion);
		Assert.Equal(0, manifest.Revision);
		Assert.Equal("win-x64", manifest.Rid);
		Assert.Equal("v1.2.3-codename", manifest.ReleaseTag);
		Assert.Equal("nori-1.2.3-win-x64.zip", manifest.PackageName);
		Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", manifest.Sha256);
		Assert.Equal(1048576, manifest.SizeBytes);
		Assert.Equal("Nori.Desktop.exe", manifest.Entrypoint);
		Assert.Equal(1, manifest.LauncherProtocol);
		Assert.Equal("zip", manifest.ArchiveType);
		Assert.Equal("初始发布", manifest.ReleaseNotes);
		Assert.NotNull(manifest.PublishedAt);
	}

	[Theory]
	[InlineData("http://github.com/file.zip")] // 非 HTTPS
	[InlineData("ftp://github.com/file.zip")]
	[InlineData("not-a-url")]
	public void FromJson_NonHttpsUrl_Throws(string invalidUrl)
	{
		string json = $$"""
		{
			"schema_version": 1,
			"product_version": "1.0.0",
			"numeric_version": "1.0.0",
			"revision": 0,
			"rid": "win-x64",
			"release_tag": "v1.0.0",
			"package_name": "nori.zip",
			"download_url": "{{invalidUrl}}",
			"sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
			"size_bytes": 100,
			"entrypoint": "Nori.Desktop.exe"
		}
		""";

		Assert.Throws<InvalidOperationException>(() => UpdateManifest.FromJson(json));
	}

	[Theory]
	[InlineData("https://evil.com/nori.zip")]
	[InlineData("https://attacker.githubusercontent.com/nori.zip")]
	[InlineData("https://raw.githubusercontent.com/nori.zip")]
	public void FromJson_DisallowedHost_Throws(string url)
	{
		string json = $$"""
		{
			"schema_version": 1,
			"product_version": "1.0.0",
			"numeric_version": "1.0.0",
			"revision": 0,
			"rid": "win-x64",
			"release_tag": "v1.0.0",
			"package_name": "nori.zip",
			"download_url": "{{url}}",
			"sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
			"size_bytes": 100,
			"entrypoint": "Nori.Desktop.exe"
		}
		""";

		var ex = Assert.Throws<InvalidOperationException>(() => UpdateManifest.FromJson(json));
		Assert.Contains("白名单", ex.Message);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(513L * 1024 * 1024)] // 超过 512 MiB
	public void FromJson_InvalidSizeBytes_Throws(long size)
	{
		string json = $$"""
		{
			"schema_version": 1,
			"product_version": "1.0.0",
			"numeric_version": "1.0.0",
			"revision": 0,
			"rid": "win-x64",
			"release_tag": "v1.0.0",
			"package_name": "nori.zip",
			"download_url": "https://github.com/MF-Dust/Nori-Desktop-Pet/releases/download/v1.0.0/nori.zip",
			"sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
			"size_bytes": {{size}},
			"entrypoint": "Nori.Desktop.exe"
		}
		""";

		Assert.Throws<InvalidOperationException>(() => UpdateManifest.FromJson(json));
	}

	[Theory]
	[InlineData("1.0.0", 0, "1.0.1", 0, true)]
	[InlineData("1.0.0", 0, "1.1.0", 0, true)]
	[InlineData("1.0.0", 0, "2.0.0", 0, true)]
	[InlineData("1.0.0", 0, "1.0.0", 1, true)]
	[InlineData("1.0.0", 1, "1.0.0", 0, false)] // 降级 revision
	[InlineData("1.0.1", 0, "1.0.0", 0, false)] // 降级 numeric
	[InlineData("1.0.0", 0, "1.0.0", 0, false)] // 平级
	public void IsStrictlyNewer_RejectsDowngradeAndEqual(
		string currentNumeric,
		int currentRevision,
		string targetNumeric,
		int targetRevision,
		bool expected)
	{
		bool actual = UpdateManifest.IsStrictlyNewer(currentNumeric, currentRevision, targetNumeric, targetRevision);
		Assert.Equal(expected, actual);
	}
}
