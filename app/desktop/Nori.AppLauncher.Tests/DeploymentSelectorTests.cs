using System.Text.Json;
using Nori.AppLauncher;

namespace Nori.AppLauncher.Tests;

public sealed class DeploymentSelectorTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nori-launcher-tests", Guid.NewGuid().ToString("N"));

	public DeploymentSelectorTests() => Directory.CreateDirectory(_root);

	[Fact]
	public void 当前槽优先于更高版本()
	{
		CreateSlot("app-1.0.0-2", "1.0.0", 2, "win-x64");
		CreateSlot("app-2.0.0-1", "2.0.0", 1, "win-x64");
		File.WriteAllText(Path.Combine(_root, ".current"), "app-1.0.0-2\n");

		DeploymentSelection selected = DeploymentSelector.Select(_root, "win-x64");

		Assert.Equal("app-1.0.0-2", Path.GetFileName(selected.DeploymentRoot));
	}

	[Fact]
	public void 版本修订号和目录名排序稳定()
	{
		CreateSlot("app-1.0.0-1", "1.0.0", 1, "win-x64");
		CreateSlot("app-1.0.0-3", "1.0.0", 3, "linux-x64");
		CreateSlot("app-1.0.0-2", "1.0.0", 2, "win-x64");

		DeploymentSelection selected = DeploymentSelector.Select(_root, "win-x64");

		Assert.Equal("app-1.0.0-2", Path.GetFileName(selected.DeploymentRoot));
	}

	[Fact]
	public void 拒绝入口越界和不匹配Rid()
	{
		CreateSlot("app-1.0.0-1", "1.0.0", 1, "linux-x64", "../outside");
		CreateSlot("app-2.0.0-1", "2.0.0", 1, "linux-x64");

		Assert.Throws<InvalidOperationException>(() => DeploymentSelector.Select(_root, "win-x64"));
	}

	[Fact]
	public void 忽略partial和destroy槽()
	{
		CreateSlot("app-9.0.0-1.partial", "9.0.0", 1, "win-x64");
		CreateSlot("app-8.0.0-1.destroy", "8.0.0", 1, "win-x64");

		Assert.Throws<InvalidOperationException>(() => DeploymentSelector.Select(_root, "win-x64"));
	}

	[Fact]
	public void 损坏current和控制字符manifest只跳过坏槽()
	{
		CreateSlot("app-1.0.0-1", "1.0.0", 1, "win-x64");
		string badSlot = Path.Combine(_root, "app-2.0.0-1");
		Directory.CreateDirectory(badSlot);
		File.WriteAllText(Path.Combine(badSlot, "deployment.json"), JsonSerializer.Serialize(new
		{
			schema_version = 1, product_version = "v2.0.0-test", numeric_version = "2.0.0", revision = 1, rid = "win-x64", entrypoint = "bad\u0001.exe",
		}));
		File.WriteAllText(Path.Combine(_root, ".current"), "../app-2.0.0-1");

		DeploymentSelection selected = DeploymentSelector.Select(_root, "win-x64");

		Assert.Equal("app-1.0.0-1", Path.GetFileName(selected.DeploymentRoot));
	}

	private void CreateSlot(string name, string version, int revision, string rid, string entrypoint = "Nori.Desktop.exe")
	{
		string slot = Path.Combine(_root, name);
		Directory.CreateDirectory(slot);
		if (!name.EndsWith(".partial", StringComparison.Ordinal) && !name.EndsWith(".destroy", StringComparison.Ordinal)) File.WriteAllText(Path.Combine(slot, entrypoint), "test");
		File.WriteAllText(Path.Combine(slot, "deployment.json"), JsonSerializer.Serialize(new
		{
			schema_version = 1,
			product_version = "v" + version + "-test+abcdef0",
			numeric_version = version,
			revision,
			rid,
			entrypoint,
		}));
	}

	public void Dispose()
	{
		try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } // NOSONAR: 测试夹具销毁阶段只能尽力清理，不能让清理异常覆盖测试结果。
	}
}
