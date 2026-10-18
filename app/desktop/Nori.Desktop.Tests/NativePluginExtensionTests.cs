using System.IO.Compression;
using System.Text.Json;
using Nori.Core.Chat;
using Nori.Desktop.Runtime;
using Nori.Desktop.Windows;
using Nori.PluginRuntime;
using Nori.PluginRuntime.TestPlugin;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	private const string ExtensionPluginId = "extension.plugin";

	private static PluginRuntimeHost CreateExtensionRuntime(string root, string entryType, string capability, bool chatProviders = false, bool applications = false)
	{
		string package = Path.Combine(root, $"extension-{Guid.NewGuid():N}.noripack");
		using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create))
		{
			using (StreamWriter writer = new(archive.CreateEntry("manifest.json").Open()))
				writer.Write($$"""
					{"schemaVersion":1,"id":"{{ExtensionPluginId}}","name":"Extension","description":"扩展测试","version":"1.0.0","authors":[{"name":"Nori"}],"apiVersion":"2.4","minHostVersion":"1.0.0","runtime":{"kind":"dotnet","assembly":"lib/Nori.PluginRuntime.TestPlugin.dll","entryType":"{{entryType}}"},"capabilities":["{{capability}}"],"optionalCapabilities":[],"platforms":[],"dependencies":[]}
					""");
			archive.CreateEntryFromFile(typeof(ChatProviderPlugin).Assembly.Location, "lib/Nori.PluginRuntime.TestPlugin.dll");
		}
		new PluginPackageInstaller(Path.Combine(root, "plugins")).Install(package);
		PluginRuntimeHost runtime = new(new PluginRuntimeHostOptions
		{
			DataDirectory = Path.Combine(root, "data"), PluginsDirectory = Path.Combine(root, "plugins"),
			EnableChatProviders = chatProviders, EnableApplicationInterop = applications,
		});
		runtime.Discover();
		return runtime;
	}

	private static async Task DisableExtensionAsync(PluginRuntimeHost runtime)
	{
		using JsonDocument args = JsonDocument.Parse($"{{\"id\":\"{ExtensionPluginId}\"}}");
		await Task.Run(() => runtime.InvokeManagementAsync(new PluginManagementSource("settings", true, IsTrustedNativeSettings: true), "plugin_disable", args.RootElement));
	}

	private static async Task DeleteExtensionDirectoryAsync(string root)
	{
		for (int attempt = 0; ; attempt++)
		{
			GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
			try { Directory.Delete(root, true); return; }
			catch (Exception exception) when (attempt < 9 && exception is IOException or UnauthorizedAccessException) { await Task.Delay(100); }
		}
	}

	[Fact]
	public async Task PluginChatProviderRegistersAndReleasesOnRevocation()
	{
		string root = Directory.CreateTempSubdirectory("nori-chat-provider-").FullName;
		try
		{
			// 未启用提供商能力的宿主只拒绝能力，不加载插件；独立目录避免版本冲突。
			string deniedRoot = Path.Combine(root, "denied");
			Directory.CreateDirectory(deniedRoot);
			await using (PluginRuntimeHost denied = CreateExtensionRuntime(deniedRoot, "Nori.PluginRuntime.TestPlugin.ChatProviderPlugin", "ai.provider"))
			{
				await Task.Run(() => denied.StartAllAsync());
				Assert.Empty(denied.GetContributions<IPluginChatProviderContribution>());
			}
			await Task.Run(() => GC.Collect());
			Directory.CreateDirectory(root);

			await using PluginRuntimeHost runtime = CreateExtensionRuntime(root, "Nori.PluginRuntime.TestPlugin.ChatProviderPlugin", "ai.provider",
				chatProviders: true);
			CustomChatProviderRegistry providers = new();
			List<Exception> errors = [];
			using PluginChatProviderController controller = new(runtime, providers, errors.Add);
			await Task.Run(() => runtime.StartAllAsync());

			Assert.True(providers.TryGet($"plugin:{ExtensionPluginId}:local", out CustomChatProviderDescriptor? descriptor));
			Assert.Equal("测试本地提供商", descriptor!.Name);
			Assert.False(descriptor.RequiresApiKey);
			Assert.Empty(errors);

			// 真实 ALC 插件返回的 IChatClient 必须与宿主共享 Microsoft.Extensions.AI 类型身份。
			string reply = await providers.CreateAdapter($"plugin:{ExtensionPluginId}:local")
				.CompleteAsync("", "", "model", "system", [new ChatMessageInput("user", "hi")]);
			Assert.Equal("插件回复", reply);

			// 模型列表去掉空白与控制字符并去重。
			Assert.Equal(["model-1", "model-2"], await providers.FetchModelsAsync($"plugin:{ExtensionPluginId}:local", "", "fast"));

			// 撤销时注册立即消失，在途的模型查询被取消而不是悬挂。
			Task<IReadOnlyList<string>> pending = providers.FetchModelsAsync($"plugin:{ExtensionPluginId}:local", "", "slow");
			await DisableExtensionAsync(runtime);
			Assert.False(providers.TryGet($"plugin:{ExtensionPluginId}:local", out _));
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
			Assert.Empty(errors);
		}
		finally
		{
			await DeleteExtensionDirectoryAsync(root);
		}
	}

	[Fact]
	public async Task PluginApplicationCapabilityRunsThroughRealPlugin()
	{
		string root = Directory.CreateTempSubdirectory("nori-application-").FullName;
		try
		{
			await using PluginRuntimeHost runtime = CreateExtensionRuntime(root, "Nori.PluginRuntime.TestPlugin.ApplicationPlugin", "applications",
				applications: true);
			await Task.Run(() => runtime.StartAllAsync());

			var result = await Task.Run(() => runtime.InvokePluginActionAsync(ExtensionPluginId, "applications", null));
			Assert.True(result!["count"]!.GetValue<int>() > 0);
			Assert.False(string.IsNullOrWhiteSpace(result["name"]!.GetValue<string>()));

			await DisableExtensionAsync(runtime);
			Assert.Empty(runtime.GetContributions<IPluginActionContribution>());
		}
		finally
		{
			await DeleteExtensionDirectoryAsync(root);
		}
	}
}
