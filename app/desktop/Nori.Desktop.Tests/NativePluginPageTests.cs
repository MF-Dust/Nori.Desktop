using System.IO.Compression;
using Nori.Desktop.Windows;
using Nori.PluginRuntime;
using Nori.PluginRuntime.TestPlugin;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private static PluginRuntimeHost CreatePageRuntime(string root)
	{
		string package = Path.Combine(root, "native.noripack");
		using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create))
		{
			using (StreamWriter writer = new(archive.CreateEntry("manifest.json").Open()))
				writer.Write("""
					{"schemaVersion":1,"id":"native.plugin","name":"Native","description":"页面测试","version":"1.0.0","authors":[{"name":"Nori"}],"apiVersion":"2.1","minHostVersion":"1.0.0","runtime":{"kind":"dotnet","assembly":"lib/Nori.PluginRuntime.TestPlugin.dll","entryType":"Nori.PluginRuntime.TestPlugin.NativePagePlugin"},"capabilities":["ui.avalonia"],"optionalCapabilities":[],"platforms":[],"dependencies":[]}
					""");
			archive.CreateEntryFromFile(typeof(NativePagePlugin).Assembly.Location, "lib/Nori.PluginRuntime.TestPlugin.dll");
		}
		new PluginPackageInstaller(Path.Combine(root, "plugins")).Install(package);
		PluginRuntimeHost runtime = new(new PluginRuntimeHostOptions
		{
			DataDirectory = Path.Combine(root, "data"), PluginsDirectory = Path.Combine(root, "plugins"), EnableAvaloniaPages = true,
		});
		runtime.Discover();
		return runtime;
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task NativePluginPagesReleaseBeforeRevocationOrShutdown(bool revoke)
	{
		string root = Directory.CreateTempSubdirectory("nori-page-release-").FullName;
		WeakReference? context = null;
		try
		{
			await WithSettingsUiAsync(async () =>
			{
				await using PluginRuntimeHost runtime = CreatePageRuntime(root);
				using PluginPageController controller = new(runtime, exception => throw new InvalidOperationException("测试页面释放不应失败", exception));
				await Task.Run(() => runtime.StartAllAsync());
				context = GetPageLoadContext(runtime);
				PluginPageWindow window = controller.Open("native.plugin", "home");
				if (revoke)
				{
					await Task.Run(() => runtime.InvokePluginActionAsync("native.plugin", "state", new System.Text.Json.Nodes.JsonObject { ["revoke"] = true }));
					Assert.Empty(controller.Pages);
				}
				else
				{
					int exited = 0;
					WindowManager manager = new(_ => exited++);
					typeof(WindowManager).GetField("_pluginPages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(manager, controller);
					manager.Shutdown();
					await WaitUntilAsync(() => exited == 1);
				}
				Assert.False(window.IsVisible);
				Assert.Null(window.Content);
			});
		}
		finally { await DeletePageTestDirectoryAsync(root); }
		Assert.NotNull(context);
		Assert.False(context.IsAlive);
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static WeakReference? GetPageLoadContext(PluginRuntimeHost runtime)
	{
		IPluginPageContribution? page = runtime.GetContributions<IPluginPageContribution>().FirstOrDefault();
		return page is null ? null : new WeakReference(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(page.GetType().Assembly));
	}

	private static async Task DeletePageTestDirectoryAsync(string root)
	{
		for (int attempt = 0; ; attempt++)
		{
			GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
			try { Directory.Delete(root, true); return; }
			catch (Exception exception) when (attempt < 9 && exception is IOException or UnauthorizedAccessException)
			{ await Task.Delay(50); }
		}
	}
}
