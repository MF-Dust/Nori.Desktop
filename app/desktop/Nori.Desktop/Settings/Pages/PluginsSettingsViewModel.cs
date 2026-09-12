using System.Text.Json;

namespace Nori.Desktop.Settings.Pages;

/// <summary>插件能力授权状态。</summary>
public sealed record PluginCapabilityItem(string Id, bool Declared, bool Granted, bool Available);

/// <summary>插件运行信息。</summary>
public sealed record PluginItem(
	string Id,
	string Name,
	string Description,
	string Version,
	string Author,
	string? Homepage,
	string? Repository,
	string? License,
	string State,
	bool Enabled,
	IReadOnlyList<string> Capabilities,
	IReadOnlyList<string> OptionalCapabilities,
	IReadOnlyList<PluginCapabilityItem> CapabilityStatuses,
	string? ErrorCode,
	string? ErrorMessage,
	bool RequiresRestart,
	string? IconUrl);

/// <summary>插件卸载结果。</summary>
public sealed record PluginUninstallItem(bool Success, bool RequiresRestart, PluginItem? Plugin);

/// <summary>插件设置页的状态和宿主命令编排。</summary>
public sealed class PluginsSettingsViewModel : SettingsPageViewModelBase
{
	/// <summary>插件风险确认读取命令。</summary>
	public const string TrustReadCommand = "settings_get_plugin_trust";

	/// <summary>插件风险确认写入命令。</summary>
	public const string TrustWriteCommand = "settings_set_plugin_trust";

	private IReadOnlyList<PluginItem> _plugins = [];
	private bool _safeMode;
	private bool _trustConfirmed;

	/// <summary>创建插件 ViewModel。</summary>
	public PluginsSettingsViewModel(SettingsService service) : base(service) { }

	/// <summary>已发现插件。</summary>
	public IReadOnlyList<PluginItem> Plugins
	{
		get => _plugins;
		private set => SetProperty(ref _plugins, value);
	}

	/// <summary>当前是否为安全模式。</summary>
	public bool SafeMode
	{
		get => _safeMode;
		private set => SetProperty(ref _safeMode, value);
	}

	/// <summary>是否已经在宿主配置中确认进程内插件风险。</summary>
	public bool TrustConfirmed
	{
		get => _trustConfirmed;
		private set => SetProperty(ref _trustConfirmed, value);
	}

	/// <summary>是否允许开始安装。</summary>
	public bool CanInstall => !SafeMode && TrustConfirmed;

	/// <inheritdoc />
	public override async Task RefreshAsync(CancellationToken cancellationToken = default)
	{
		IsBusy = true;
		try
		{
			JsonElement snapshot = await Service.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
			SafeMode = SettingsJson.Bool(SettingsJson.Object(snapshot, "app"), "safeMode");
			JsonElement result = await Service.ExecuteAsync("plugin_list", cancellationToken: cancellationToken).ConfigureAwait(true);
			Plugins = SettingsJson.Array(result, "plugins").Select(ParsePlugin).Where(item => item.Id.Length > 0).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
			await RefreshTrustAsync(cancellationToken).ConfigureAwait(true);
			NotifyChanged(nameof(CanInstall));
		}
		finally
		{
			IsBusy = false;
		}
	}

	/// <summary>读取宿主配置中的风险确认标记。</summary>
	public async Task<bool> RefreshTrustAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			JsonElement result = await Service.ExecuteAsync(TrustReadCommand, cancellationToken: cancellationToken).ConfigureAwait(true);
			TrustConfirmed = result.ValueKind is JsonValueKind.True or JsonValueKind.False
				? result.GetBoolean()
				: SettingsJson.Bool(result, "confirmed");
		}
		catch (InvalidOperationException)
		{
			// 旧宿主没有风险确认命令时保持未确认，避免绕过宿主配置边界。
			TrustConfirmed = false;
		}
		NotifyChanged(nameof(CanInstall));
		return TrustConfirmed;
	}

	/// <summary>把插件进程内运行风险确认写入宿主配置。</summary>
	public async Task ConfirmTrustAsync(CancellationToken cancellationToken = default)
	{
		await ExecuteAsync(TrustWriteCommand, new {confirmed = true}, cancellationToken).ConfigureAwait(true);
		TrustConfirmed = true;
		NotifyChanged(nameof(CanInstall));
	}

	/// <summary>打开宿主文件选择器并安装本地插件包。</summary>
	public async Task<PluginItem?> InstallLocalAsync(CancellationToken cancellationToken = default)
	{
		EnsureCanMutate();
		JsonElement result = await ExecuteAsync("plugin_install_local", cancellationToken: cancellationToken).ConfigureAwait(true);
		bool cancelled = SettingsJson.Bool(result, "cancelled");
		PluginItem? plugin = SettingsJson.Object(result, "plugin") is {ValueKind: JsonValueKind.Object} item ? ParsePlugin(item) : null;
		if (!cancelled && plugin is not null) Replace(plugin);
		return plugin;
	}

	/// <summary>启用插件。</summary>
	public async Task<PluginItem> EnableAsync(PluginItem plugin, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plugin);
		EnsureCanMutate();
		JsonElement result = await ExecuteAsync("plugin_enable", new {id = plugin.Id}, cancellationToken).ConfigureAwait(true);
		PluginItem updated = ParsePlugin(result);
		Replace(updated);
		return updated;
	}

	/// <summary>停用插件。</summary>
	public async Task<PluginItem> DisableAsync(PluginItem plugin, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plugin);
		// 停用属于撤回授权，安全模式和未确认安装风险时仍然允许。
		JsonElement result = await ExecuteAsync("plugin_disable", new {id = plugin.Id}, cancellationToken).ConfigureAwait(true);
		PluginItem updated = ParsePlugin(result);
		Replace(updated);
		return updated;
	}

	/// <summary>卸载插件，可选择同时删除插件数据。</summary>
	public async Task<PluginUninstallItem> UninstallAsync(PluginItem plugin, bool deleteData, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plugin);
		JsonElement result = await ExecuteAsync("plugin_uninstall", new {id = plugin.Id, deleteData}, cancellationToken).ConfigureAwait(true);
		PluginItem? updated = SettingsJson.Object(result, "plugin") is {ValueKind: JsonValueKind.Object} item ? ParsePlugin(item) : null;
		PluginUninstallItem uninstall = new(SettingsJson.Bool(result, "success"), SettingsJson.Bool(result, "requiresRestart"), updated);
		if (updated is null) Plugins = Plugins.Where(item => item.Id != plugin.Id).ToArray();
		else Replace(updated);
		NotifyChanged(nameof(Plugins));
		return uninstall;
	}

	/// <summary>读取插件状态显示标签。</summary>
	public static string CapabilityLabel(PluginItem plugin, string capability)
	{
		PluginCapabilityItem? status = plugin.CapabilityStatuses.FirstOrDefault(item => string.Equals(item.Id, capability, StringComparison.Ordinal));
		if (status is null || !status.Declared) return "undeclared";
		if (!status.Granted || !status.Available) return "unavailable";
		return "granted";
	}

	private void EnsureCanMutate()
	{
		if (SafeMode) throw new InvalidOperationException("安全模式下暂时禁用插件操作。");
		if (!TrustConfirmed) throw new InvalidOperationException("请先确认进程内插件运行风险。");
	}

	private void Replace(PluginItem plugin)
	{
		if (plugin.Id.Length == 0) return;
		Plugins = Plugins.Where(item => item.Id != plugin.Id).Append(plugin).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
		NotifyChanged(nameof(Plugins));
	}

	private static PluginItem ParsePlugin(JsonElement value) => new(
		SettingsJson.String(value, "id"),
		SettingsJson.String(value, "name"),
		SettingsJson.String(value, "description"),
		SettingsJson.String(value, "version"),
		SettingsJson.String(value, "author"),
		SettingsJson.NullableString(value, "homepage"),
		SettingsJson.NullableString(value, "repository"),
		SettingsJson.NullableString(value, "license"),
		SettingsJson.String(value, "state", "installed"),
		SettingsJson.Bool(value, "enabled"),
		ReadStrings(value, "capabilities"),
		ReadStrings(value, "optionalCapabilities"),
		SettingsJson.Array(value, "capabilityStatuses").Select(item => new PluginCapabilityItem(
			SettingsJson.String(item, "id"),
			SettingsJson.Bool(item, "declared"),
			SettingsJson.Bool(item, "granted"),
			SettingsJson.Bool(item, "available"))).ToArray(),
		SettingsJson.NullableString(value, "errorCode"),
		SettingsJson.NullableString(value, "errorMessage"),
		SettingsJson.Bool(value, "requiresRestart"),
		SettingsJson.NullableString(value, "iconUrl"));

	private static IReadOnlyList<string> ReadStrings(JsonElement value, string name)
	{
		JsonElement array = SettingsJson.Property(value, name);
		return array.ValueKind == JsonValueKind.Array
			? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray()
			: [];
	}
}
