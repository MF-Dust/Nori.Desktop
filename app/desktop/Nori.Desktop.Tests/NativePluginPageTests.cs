using System.IO.Compression;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Nori.Desktop.Settings;
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
	[InlineData(720, 480, "zh-CN")]
	[InlineData(1920, 1080, "zh-CN")]
	[InlineData(720, 480, "en-US")]
	[InlineData(1920, 1080, "en-US")]
	public async Task NativePluginPageLifecycleAndLayout(int width, int height, string language)
	{
		string root = Path.Combine(Path.GetTempPath(), "nori-page-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			await WithSettingsUiAsync(async () =>
			{
				SettingsLocalization.SetLanguage(language);
				try
				{
					await using PluginRuntimeHost runtime = CreatePageRuntime(root);
					List<Exception> errors = [];
					using PluginPageController controller = new(runtime, errors.Add);
					await Task.Run(() => runtime.StartAllAsync());
					Assert.Equal(3, controller.Pages.Count);
					PluginPageWindow first = controller.Open("native.plugin", "home");
					Assert.Same(first, controller.Open("native.plugin", "home"));
					first.Width = width;
					first.Height = height;
					await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
					first.UpdateLayout();
					Assert.Equal(40, first.GetLogicalDescendants().OfType<NativeWindowChrome>().Single().Bounds.Height);
					Assert.Equal(width, first.ClientSize.Width);
					Assert.Equal(height, first.ClientSize.Height);
					CaptureUi(first, $"plugin-page-{width}x{height}-{language}");
					using (BridgeCommandsTests fixture = new())
					{
						fixture._config.Set(Nori.Core.Configuration.ConfigStore.KeyLanguage, new Nori.Core.Configuration.ConfigValue.Text(language));
						fixture._services.PluginRuntime = runtime;
						WindowManager manager = new(_ => { });
						fixture._services.Windows = manager;
						typeof(WindowManager).GetField("_pluginPages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(manager, controller);
						SettingsWindow settings = new(fixture._services) { Width = width, Height = height };
						try
						{
							settings.Show();
							SettingsViewModel vm = Assert.IsType<SettingsViewModel>(settings.DataContext);
							await vm.RefreshSnapshotAsync();
							vm.Navigate("plugins");
							await WaitUntilAsync(() => settings.GetLogicalDescendants().OfType<Button>().Any(button => button.Content?.ToString()?.Contains("原生页面", StringComparison.Ordinal) == true));
							settings.UpdateLayout();
							CaptureUi(settings, $"plugin-settings-{width}x{height}-{language}");
							Button open = settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Content?.ToString()?.Contains("释放错误", StringComparison.Ordinal) == true);
							open.BringIntoView();
							await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
							settings.UpdateLayout();
							CaptureUi(settings, $"plugin-settings-pages-{width}x{height}-{language}");
							settings.Hide();
							Assert.True(first.IsVisible);
							first.Close();
							Assert.Null(first.Content);
							var state = await runtime.InvokePluginActionAsync("native.plugin", "state", null);
							Assert.Equal(1, state!["created"]!.GetValue<int>());
							Assert.Equal(1, state["disposed"]!.GetValue<int>());
							PluginPageWindow second = controller.Open("native.plugin", "home");
							Assert.NotSame(first, second);
							Assert.Throws<InvalidOperationException>(() => controller.Open("native.plugin", "broken"));
							PluginPageWindow badDispose = controller.Open("native.plugin", "dispose-error");
							badDispose.Close();
							Assert.Null(badDispose.Content);
							Assert.Single(errors);
							errors.Clear();
							// 后台停用等待 UI 清理屏障；插件 Deactivate 会检查所有已创建页面已释放。
							using JsonDocument args = JsonDocument.Parse("{\"id\":\"native.plugin\"}");
							await Task.Run(() => runtime.InvokeManagementAsync(new PluginManagementSource("settings", true, IsTrustedNativeSettings: true), "plugin_disable", args.RootElement));
							Assert.Null(second.Content);
							Assert.False(second.IsVisible);
							Assert.Empty(controller.Pages);
							Assert.Throws<InvalidOperationException>(() => controller.Open("native.plugin", "home"));
						}
						finally { settings.AllowClose = true; settings.Close(); fixture._services.PluginRuntime = null; }
					}
				}
				finally
				{
					SettingsLocalization.SetLanguage("zh-CN");
				}
			});
		}
		finally { await DeletePageTestDirectoryAsync(root); }
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

	[Fact]
	public Task NativePluginPageRejectsMountedOrTopLevelControl() => WithSettingsUiAsync(() =>
	{
		List<Exception> errors = [];
		using InvalidPage windowPage = new(new Window());
		Assert.Throws<InvalidOperationException>(() => new PluginPageWindow("无效页面", windowPage, errors.Add));
		TextBlock mounted = new();
		StackPanel parent = new() { Children = { mounted } };
		using InvalidPage mountedPage = new(mounted);
		Assert.Throws<InvalidOperationException>(() => new PluginPageWindow("无效页面", mountedPage, errors.Add));
		Assert.Same(parent, mounted.Parent);
		return Task.CompletedTask;
	});

	private sealed class InvalidPage(Control control) : IPluginPage
	{
		public Control Control => control;
		public void Dispose() => Dispatcher.UIThread.VerifyAccess();
	}
}
