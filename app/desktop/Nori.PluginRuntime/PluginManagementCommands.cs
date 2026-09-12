using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Nori.PluginRuntime;

/// <summary>供插件管理命令使用的最小宿主来源。</summary>
internal sealed record PluginManagementSource(
	string Label,
	bool IsVisible,
	Window? Owner = null,
	bool IsTrustedNativeSettings = false);

/// <summary>宿主本地插件包文件选择器。</summary>
internal interface IPluginPackagePicker
{
	Task<string?> PickAsync(Window? owner, CancellationToken cancellationToken = default);
}

internal sealed class AvaloniaPluginPackagePicker : IPluginPackagePicker
{
	public Task<string?> PickAsync(Window? owner, CancellationToken cancellationToken = default)
	{
		if (Dispatcher.UIThread.CheckAccess()) return PickCoreAsync(owner, cancellationToken);

		TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Dispatcher.UIThread.Post(() => _ = CompleteOnUiAsync(completion, owner, cancellationToken));
		return completion.Task;
	}

	private static async Task CompleteOnUiAsync(
		TaskCompletionSource<string?> completion,
		Window? owner,
		CancellationToken cancellationToken)
	{
		try { completion.TrySetResult(await PickCoreAsync(owner, cancellationToken).ConfigureAwait(true)); }
		catch (Exception exception) { completion.TrySetException(exception); }
	}

	private static async Task<string?> PickCoreAsync(Window? owner, CancellationToken cancellationToken)
	{
		if (owner is null) throw new InvalidOperationException("来源窗口不可用");
		cancellationToken.ThrowIfCancellationRequested();
		IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "选择 Nori 插件包 (.noripack)",
			AllowMultiple = false,
			FileTypeFilter =
			[
				new FilePickerFileType("Nori 插件包 (*.noripack)") { Patterns = ["*.noripack"] },
			],
		});
		cancellationToken.ThrowIfCancellationRequested();
		return files.Count == 0 ? null : files[0].Path.LocalPath;
	}
}

/// <summary>插件管理命令的实现；只服务宿主可见主窗口。</summary>
internal sealed class PluginManagementCommands
{
	private readonly PluginManager _manager;
	private readonly string _mainWindowLabel;
	private readonly Func<string, string, Uri>? _assetUriFactory;
	private readonly IPluginPackagePicker _picker;

	public PluginManagementCommands(
		PluginManager manager,
		string mainWindowLabel,
		Func<string, string, Uri>? assetUriFactory,
		IPluginPackagePicker? picker)
	{
		_manager = manager ?? throw new ArgumentNullException(nameof(manager));
		_mainWindowLabel = string.IsNullOrWhiteSpace(mainWindowLabel) ? "main" : mainWindowLabel;
		_assetUriFactory = assetUriFactory;
		_picker = picker ?? new AvaloniaPluginPackagePicker();
	}

	public async Task<object?> InvokeAsync(
		PluginManagementSource source,
		string command,
		JsonElement args,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(source);
		RequireVisibleMain(source);
		return command switch
		{
			"plugin_list" => List(),
			"plugin_install_local" => await InstallLocalAsync(source, cancellationToken).ConfigureAwait(false),
			"plugin_enable" => await EnableAsync(args, cancellationToken).ConfigureAwait(false),
			"plugin_disable" => await DisableAsync(args, cancellationToken).ConfigureAwait(false),
			"plugin_uninstall" => await UninstallAsync(args, cancellationToken).ConfigureAwait(false),
			_ => throw new InvalidOperationException($"未知插件管理命令: {command}"),
		};
	}

	private PluginListResultDto List() => new(
		_manager.Discover()
			.OrderBy(item => item.Manifest.Name, StringComparer.OrdinalIgnoreCase)
			.Select(ToDto)
			.ToArray());

	private async Task<PluginInstallResultDto> InstallLocalAsync(
		PluginManagementSource source,
		CancellationToken cancellationToken)
	{
		if (_manager.IsSafeMode) throw new PluginException(PluginErrorCodes.SafeModeDisabled, "安全模式下不允许安装插件");
		string? packagePath = await _picker.PickAsync(source.Owner, cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(packagePath)) return new PluginInstallResultDto(true, null);

		PluginManifest manifest = await _manager.InstallAsync(packagePath, cancellationToken).ConfigureAwait(false);
		PluginInfo info = _manager.Plugins.Single(item => string.Equals(item.Id, manifest.Id, StringComparison.Ordinal));
		return new PluginInstallResultDto(false, ToDto(info));
	}

	private async Task<PluginListItemDto> EnableAsync(JsonElement args, CancellationToken cancellationToken)
	{
		if (_manager.IsSafeMode) throw new PluginException(PluginErrorCodes.SafeModeDisabled, "安全模式下不允许启用插件");
		string id = RequiredId(args);
		await _manager.EnableAsync(id, cancellationToken).ConfigureAwait(false);
		return ToDto(Find(id));
	}

	private async Task<PluginListItemDto> DisableAsync(JsonElement args, CancellationToken cancellationToken)
	{
		string id = RequiredId(args);
		await _manager.DisableAsync(id, cancellationToken).ConfigureAwait(false);
		return ToDto(Find(id));
	}

	private async Task<PluginUninstallResultDto> UninstallAsync(JsonElement args, CancellationToken cancellationToken)
	{
		string id = RequiredId(args);
		bool deleteData = OptionalBool(args, "deleteData");
		PluginUninstallResult result = await _manager.UninstallAsync(id, deleteData, cancellationToken).ConfigureAwait(false);
		return new PluginUninstallResultDto(result.Success, result.RequiresRestart, result.Plugin is null ? null : ToDto(result.Plugin));
	}

	private PluginInfo Find(string id) => _manager.Plugins.Single(item => string.Equals(item.Id, id, StringComparison.Ordinal));

	private PluginListItemDto ToDto(PluginInfo info)
	{
		string author = string.Join(", ", info.Manifest.Authors.Select(item => item.Name.Trim()).Where(name => name.Length > 0));
		string? iconUrl = null;
		string? assetRoot = _manager.ResolveAssetRoot(info.Id);
		if (_assetUriFactory is not null && assetRoot is not null && File.Exists(Path.Combine(assetRoot, "icon.png")))
			iconUrl = _assetUriFactory(info.Id, "icon.png").ToString();

		return new PluginListItemDto(
			info.Id,
			info.Manifest.Name,
			info.Manifest.Description,
			info.Manifest.Version,
			author,
			info.Manifest.Homepage,
			info.Manifest.Repository,
			info.Manifest.License,
			StateName(info.State),
			info.UserEnabled,
			info.Manifest.Capabilities.ToArray(),
			info.Manifest.OptionalCapabilities.ToArray(),
			info.CapabilityStatuses.Select(status => new PluginCapabilityStatusDto(status.Id, status.Declared, status.Granted, status.Available)).ToArray(),
			info.ErrorCode,
			info.ErrorMessage,
			info.RequiresRestart,
			iconUrl);
	}

	private static string StateName(PluginLifecycleState state) => state switch
	{
		PluginLifecycleState.Discovered or PluginLifecycleState.Installed => "installed",
		PluginLifecycleState.Loading => "loading",
		PluginLifecycleState.Active => "active",
		PluginLifecycleState.Stopping => "stopping",
		PluginLifecycleState.Disabled => "disabled",
		PluginLifecycleState.Failed => "failed",
		PluginLifecycleState.Incompatible => "incompatible",
		PluginLifecycleState.PendingRestart => "pending_restart",
		_ => "installed",
	};

	private static string RequiredId(JsonElement args)
	{
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("id", out JsonElement value) || value.ValueKind != JsonValueKind.String)
			throw new PluginException(PluginErrorCodes.InvalidPluginId, "插件 ID 无效");
		string id = value.GetString() ?? string.Empty;
		if (!PluginManifestReader.IsValidPluginId(id))
			throw new PluginException(PluginErrorCodes.InvalidPluginId, "插件 ID 无效");
		return id;
	}

	private static bool OptionalBool(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object
		&& args.TryGetProperty(name, out JsonElement value)
		&& value.ValueKind is JsonValueKind.True or JsonValueKind.False
		&& value.GetBoolean();

	private void RequireVisibleMain(PluginManagementSource source)
	{
		if ((!source.IsTrustedNativeSettings && !string.Equals(source.Label, _mainWindowLabel, StringComparison.Ordinal))
			|| !source.IsVisible)
			throw new UnauthorizedAccessException("插件管理仅允许可见的主窗口调用");
	}
}
