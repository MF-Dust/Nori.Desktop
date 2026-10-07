using Avalonia.Controls;

namespace Nori.PluginRuntime.Tests;

public sealed partial class PluginRuntimeTests
{
	private sealed class PageContribution(string id = "page", string title = "页面") : IPluginPageContribution
	{
		public string Id => id;
		public string Title => title;
		public IPluginPage CreatePage() => throw new InvalidOperationException("注册不能创建控件");
	}

	[Theory]
	[InlineData(false, false, PluginErrorCodes.CapabilityMissing)]
	[InlineData(true, false, PluginErrorCodes.CapabilityNotGranted)]
	[InlineData(true, true, null)]
	public void 页面注册验证能力及撤销(bool declared, bool enabled, string? code)
	{
		PluginCapabilityRegistry capabilities = new(declared ? [PluginCapabilityIds.AvaloniaUi] : [],
			enabled ? [PluginCapabilityIds.AvaloniaUi] : [], enabled ? [new AvaloniaPageCapability()] : []);
		PluginContributionRegistry registry = new();
		registry.Begin(capabilities);
		if (code is not null)
		{
			Assert.Equal(code, Assert.Throws<PluginException>(() => registry.Register(new PageContribution())).Code);
			return;
		}
		int changed = 0;
		registry.Changed = () => { Assert.NotNull(registry.Snapshot()); changed++; };
		using IPluginRegistration registration = registry.Register(new PageContribution());
		Assert.Equal(PluginErrorCodes.DuplicateContribution, Assert.Throws<PluginException>(() => registry.Register(new PageContribution())).Code);
		registry.RevokeAll();
		Assert.Empty(registry.GetAll<IPluginPageContribution>());
		Assert.Equal(2, changed);
		Assert.Equal(PluginErrorCodes.CapabilityUnavailable, Assert.Throws<PluginException>(() => registry.Register(new PageContribution("late"))).Code);
	}

	[Theory]
	[InlineData("bad/id", "页面")]
	[InlineData("", "页面")]
	[InlineData("page", " ")]
	public void 页面元数据拒绝无效值(string id, string title)
	{
		PluginContributionRegistry registry = new();
		registry.Begin(new PluginCapabilityRegistry([PluginCapabilityIds.AvaloniaUi], [PluginCapabilityIds.AvaloniaUi], [new AvaloniaPageCapability()]));
		Assert.Equal(PluginErrorCodes.InvalidManifest, Assert.Throws<PluginException>(() => registry.Register(new PageContribution(id, title))).Code);
	}

	[Theory]
	[InlineData(true, false, true)]
	[InlineData(true, true, true)]
	[InlineData(false, false, false)]
	[InlineData(false, true, false)]
	public async Task 真实页面插件支持可选能力且共享宿主控件程序集(bool enabled, bool optional, bool active)
	{
		string root = CreateTemp();
		try
		{
			await Task.Run(async () =>
			{
				await using PluginManager manager = new(new PluginRuntimeOptions
				{
					PluginsDirectory = Path.Combine(root, "plugins"), DataDirectory = Path.Combine(root, "data"), EnableAvaloniaPages = enabled,
			});
			manager.Installer.Install(CreateTestPackage(root, "native.plugin", "1.0.0",
				capabilities: optional ? "[]" : "[\"ui.avalonia\"]", optionalCapabilities: optional ? "[\"ui.avalonia\"]" : "[]",
				entryType: "Nori.PluginRuntime.TestPlugin.NativePagePlugin", apiVersion: "2.1"));
			manager.Discover();
			await manager.StartAllAsync();
			Assert.Equal(active, manager.GetContributions<IPluginPageContribution>().Count > 0);
			if (active)
			{
				Assert.Equal(typeof(Control).Assembly, typeof(IPluginPage).GetProperty(nameof(IPluginPage.Control))!.PropertyType.Assembly);
				bool revoked = false;
				manager.ContributionsChanged += () => { if (manager.GetContributions<IPluginPageContribution>().Count == 0) revoked = true; };
				await manager.DeactivateAsync("native.plugin");
				Assert.True(revoked);
				Assert.Empty(manager.GetContributions<IPluginPageContribution>());
			}
			});
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task 页面引用阻止卸载时发现刷新不能绕过重启限制()
	{
		string root = CreateTemp();
		try
		{
			await Task.Run(async () =>
			{
				await using PluginManager manager = new(new PluginRuntimeOptions
				{
					PluginsDirectory = Path.Combine(root, "plugins"), DataDirectory = Path.Combine(root, "data"), EnableAvaloniaPages = true,
				});
				manager.Installer.Install(CreateTestPackage(root, "native.plugin", "1.0.0", capabilities: "[\"ui.avalonia\"]",
					entryType: "Nori.PluginRuntime.TestPlugin.NativePagePlugin", apiVersion: "2.1"));
				manager.Discover();
				await manager.StartAllAsync();
				IPluginPageContribution retained = manager.GetContributions<IPluginPageContribution>()[0];
				await manager.DisableAsync("native.plugin");
				Assert.Equal(PluginLifecycleState.PendingRestart, Assert.Single(manager.Discover()).State);
				PluginException error = await Assert.ThrowsAsync<PluginException>(() => manager.EnableAsync("native.plugin"));
				Assert.Equal(PluginErrorCodes.UnloadPendingRestart, error.Code);
				GC.KeepAlive(retained);
			});
		}
		finally { DeleteDirectory(root); }
	}

	[Theory]
	[InlineData("Avalonia.Controls.dll")]
	[InlineData("renamed.dll")]
	public void 插件包禁止私有Avalonia副本(string filename)
	{
		string root = CreateTemp();
		try
		{
			File.Copy(typeof(Control).Assembly.Location, Path.Combine(root, filename));
			Assert.Equal(PluginErrorCodes.ForbiddenReference, Assert.Throws<PluginException>(() => PluginLoadContext.EnsureReferencesAllowed(root)).Code);
		}
		finally { DeleteDirectory(root); }
	}
}
