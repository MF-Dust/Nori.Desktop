using Nori.Core.Data;

namespace Nori.Core.Tests;

/// <summary>包内存储路径和 containment 契约。</summary>
public sealed class AppPathsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nori-path-tests", Guid.NewGuid().ToString("N"));

	public AppPathsTests() => Directory.CreateDirectory(_root);

	[Fact]
	public void 所有业务路径位于包根data内()
	{
		AppStoragePaths paths = new(_root);

		Assert.Equal(Path.Combine(_root, "data"), paths.DataRoot);
		Assert.Equal(Path.Combine(paths.DataRoot, "core", "database", "nori.db"), paths.DatabasePath);
		Assert.Equal(Path.Combine(paths.DataRoot, "core", "security", "secret.key"), paths.SecretPath);
		Assert.Equal(Path.Combine(paths.DataRoot, "knowledge", "documents", "Memory.md"), paths.KnowledgePath);
		Assert.True(AppStoragePaths.IsContained(paths.DatabasePath, paths.DataRoot));
		Assert.True(AppStoragePaths.IsContained(paths.ResourcesInstalledDirectory, paths.DataRoot));
		Assert.False(AppStoragePaths.IsContained(Path.Combine(_root, "database", "nori.db"), paths.DataRoot));
	}

	[Fact]
	public void 创建固定目录且可写()
	{
		AppStoragePaths paths = new(_root);

		paths.EnsureCreated();

		Assert.True(Directory.Exists(paths.DatabaseDirectory));
		Assert.True(Directory.Exists(paths.PluginsStagingDirectory));
		Assert.True(Directory.Exists(paths.LogsDirectory));
	}

	public void Dispose()
	{
		try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
	}
}
