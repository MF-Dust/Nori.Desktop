using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Assets;
using Nori.Core.Logging;

namespace Nori.PluginRuntime;

/// <summary>插件运行时的统一宿主入口。</summary>
internal sealed class PluginRuntimeHost : IAsyncDisposable
{
	private readonly PluginWindowHost _windows;
	private readonly PluginManagementCommands _management;
	private readonly PluginManager _manager;
	private int _disposed;

	public PluginRuntimeHost(PluginRuntimeHostOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentException.ThrowIfNullOrWhiteSpace(options.DataDirectory);
		if (options.HostApiVersion.Major < 0 || options.HostApiVersion.Minor < 0)
			throw new ArgumentOutOfRangeException(nameof(options), "插件 API 版本无效");

		string dataDirectory = Path.GetFullPath(options.DataDirectory);
		// 可选项仅为核心测试/嵌入场景提供包内的确定性派生路径；绝不回退 cwd、AppData 或旧 Tauri 名称。
		string pluginsDirectory = Path.GetFullPath(options.PluginsDirectory ?? Path.Combine(dataDirectory, "plugins"));
		string pluginDataDirectory = Path.GetFullPath(options.PluginDataDirectory ?? Path.Combine(dataDirectory, "plugins", "data"));
		string webViewDataDirectory = Path.GetFullPath(options.WebViewDataDirectory ?? Path.Combine(dataDirectory, "plugins", "cache", "webview"));
		string packageInboxDirectory = Path.GetFullPath(options.PackageInboxDirectory ?? Path.Combine(dataDirectory, "plugins", "cache", "packages", "inbox"));
		string stagingDirectory = Path.GetFullPath(options.StagingDirectory ?? Path.Combine(dataDirectory, "plugins", "temp", "staging"));
		Directory.CreateDirectory(dataDirectory);

		_windows = new PluginWindowHost(options.Logger, webViewDataDirectory, options.AssetUriFactory);
		_manager = new PluginManager(new PluginRuntimeOptions
		{
			PluginsDirectory = pluginsDirectory,
			DataDirectory = pluginDataDirectory,
			PackageInboxDirectory = packageInboxDirectory,
			StagingDirectory = stagingDirectory,
			HostApiVersion = options.HostApiVersion,
			HostVersion = options.HostVersion,
			DevelopmentHost = options.DevelopmentHost,
			SafeMode = options.SafeMode,
			AssetUriFactory = options.AssetUriFactory,
			ClosePluginWindowsAsync = (pluginId, cancellationToken) => _windows.CloseAllWindowsForPluginAsync(pluginId, cancellationToken),
			CapabilityFactory = (descriptor, stoppingToken) =>
			[
				new PluginWebViewCapability(
					PluginDescriptorSummary.From(descriptor),
					(summary, windowOptions, cancellationToken) => _windows.CreateWindowAsync(summary, windowOptions, stoppingToken, cancellationToken)),
			],
			OnError = options.OnError,
			OnLog = options.OnLog,
		});
		AssetRoute = new PluginAssetRoute(_manager);
		_management = new PluginManagementCommands(_manager, options.MainWindowLabel, options.AssetUriFactory, options.PackagePicker);
	}

	public IAssetRoute AssetRoute { get; }

	public IReadOnlyCollection<PluginInfo> Discover() => _manager.Discover();

	public Task StartAllAsync(CancellationToken cancellationToken = default) => _manager.StartAllAsync(cancellationToken);

	/// <summary>活跃插件集合变化 (激活/停用完成) 时触发，供宿主刷新插件贡献派生状态 (如 AI 工具)。</summary>
	public event Action? ActivePluginsChanged
	{
		add => _manager.ActivePluginsChanged += value;
		remove => _manager.ActivePluginsChanged -= value;
	}

	/// <summary>枚举当前活跃插件提供的指定类型贡献快照。</summary>
	public IReadOnlyList<T> GetContributions<T>()
		where T : class, IPluginContribution =>
		_manager.GetContributions<T>();

	/// <summary>枚举活跃插件的聊天卡片部件 (约定 web/card.html)。</summary>
	public IReadOnlyList<PluginChatWidget> GetChatWidgets() => _manager.GetChatWidgets();

	/// <summary>枚举当前活跃插件提供的指定类型贡献及其来源插件。</summary>
	public IReadOnlyList<(PluginDescriptor Plugin, T Contribution)> GetContributionsWithSource<T>()
		where T : class, IPluginContribution =>
		_manager.GetContributionsWithSource<T>();

	/// <summary>
	/// 调用活跃插件的一个动作贡献 (宿主前端控制卡 / 宿主自动化入口)。
	/// </summary>
	public async Task<JsonNode?> InvokePluginActionAsync(
		string pluginId,
		string actionId,
		JsonNode? arguments,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(actionId))
			throw new PluginException(PluginErrorCodes.BridgeDenied, "plugin_action 参数无效");
		foreach ((PluginDescriptor plugin, IPluginActionContribution action) in _manager.GetContributionsWithSource<IPluginActionContribution>())
		{
			if (!string.Equals(plugin.Id, pluginId, StringComparison.Ordinal)
				|| !string.Equals(action.Id, actionId, StringComparison.Ordinal))
				continue;
			return await action.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
		}
		throw new PluginException(PluginErrorCodes.BridgeDenied, $"插件动作不存在或插件未激活: {pluginId}/{actionId}");
	}

	public Task<object?> InvokeManagementAsync(
		PluginManagementSource source,
		string command,
		JsonElement args,
		CancellationToken cancellationToken = default) =>
		_management.InvokeAsync(source, command, args, cancellationToken);

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		try { await _manager.DisposeAsync().ConfigureAwait(false); }
		finally { await _windows.DisposeAsync().ConfigureAwait(false); }
	}
}

/// <summary>插件运行时宿主配置。</summary>
internal sealed record PluginRuntimeHostOptions
{
	public required string DataDirectory { get; init; }
	public string? PluginsDirectory { get; init; }
	public string? PluginDataDirectory { get; init; }
	public string? WebViewDataDirectory { get; init; }
	public string? PackageInboxDirectory { get; init; }
	public string? StagingDirectory { get; init; }
	public PluginApiVersion HostApiVersion { get; init; } = new(2, 0);
	public PluginVersion HostVersion { get; init; } = new(1, 0, 0);
	public bool DevelopmentHost { get; init; }
	public bool SafeMode { get; init; }
	public string MainWindowLabel { get; init; } = "main";
	public FileLogger? Logger { get; init; }
	public Func<string, string, Uri>? AssetUriFactory { get; init; }
	public IPluginPackagePicker? PackagePicker { get; init; }
	public Action<PluginException>? OnError { get; init; }
	public Action<PluginDescriptor, string, Exception?>? OnLog { get; init; }
}
