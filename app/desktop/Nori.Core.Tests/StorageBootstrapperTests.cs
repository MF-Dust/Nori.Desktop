using Nori.Core.Data;

namespace Nori.Core.Tests;

public sealed class StorageBootstrapperTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nori-storage-tests", Guid.NewGuid().ToString("N"));

	public StorageBootstrapperTests() => Directory.CreateDirectory(_root);

	[Fact]
	public void 首次启动只创建当前版本包内布局()
	{
		AppStoragePaths paths = new(Path.Combine(_root, "package"));

		StorageBootstrapper.Bootstrap(paths, "v1.2.3-test+abcdef0", "win-x64");

		Assert.True(File.Exists(paths.MarkerPath));
		Assert.True(Directory.Exists(paths.DatabaseDirectory));
		Assert.True(Directory.Exists(paths.KnowledgeDirectory));
		Assert.True(Directory.Exists(paths.PluginsStagingDirectory));
		Assert.False(Directory.Exists(Path.Combine(paths.DataRoot, "legacy")));
	}

	[Fact]
	public void 当前版本数据可以重复启动()
	{
		AppStoragePaths paths = new(Path.Combine(_root, "package-reopen"));
		StorageBootstrapper.Bootstrap(paths, "Dev", "win-x64");
		File.WriteAllText(Path.Combine(paths.DataRoot, "kept.txt"), "keep");

		StorageBootstrapper.Bootstrap(paths, "Dev", "win-x64");

		Assert.Equal("keep", File.ReadAllText(Path.Combine(paths.DataRoot, "kept.txt")));
	}

	[Fact]
	public void 非空无marker拒绝启动()
	{
		AppStoragePaths paths = new(Path.Combine(_root, "package-no-marker"));
		Directory.CreateDirectory(paths.DataRoot);
		File.WriteAllText(Path.Combine(paths.DataRoot, "unexpected"), "x");

		Assert.Throws<InvalidOperationException>(() => StorageBootstrapper.Bootstrap(paths, "Dev", "win-x64"));
	}

	[Fact]
	public void 旧迁移marker状态拒绝启动()
	{
		AppStoragePaths paths = new(Path.Combine(_root, "package-invalid-marker"));
		StorageBootstrapper.Bootstrap(paths, "Dev", "win-x64");
		string marker = File.ReadAllText(paths.MarkerPath).Replace("\"status\":\"ready\"", "\"status\":\"cleanup_pending\"", StringComparison.Ordinal);
		File.WriteAllText(paths.MarkerPath, marker);

		Assert.Throws<InvalidOperationException>(() => StorageBootstrapper.Bootstrap(paths, "Dev", "win-x64"));
	}

	[Fact]
	public void 初始化失败时不留下staging目录()
	{
		string packageRoot = Path.Combine(_root, "package-invalid-version");
		AppStoragePaths paths = new(packageRoot);

		Assert.Throws<InvalidOperationException>(() => StorageBootstrapper.Bootstrap(paths, "not-a-version", "win-x64"));
		Assert.False(Directory.Exists(packageRoot) && Directory.EnumerateDirectories(packageRoot, "data.staging-*", SearchOption.TopDirectoryOnly).Any());
	}

	public void Dispose()
	{
		try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
	}
}
