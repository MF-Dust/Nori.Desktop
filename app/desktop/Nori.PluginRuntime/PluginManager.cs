using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
namespace Nori.PluginRuntime;

/// <summary>插件生命周期状态。</summary>
internal enum PluginLifecycleState
{
	Discovered,
	Installed,
	Loading,
	Active,
	Stopping,
	Disabled,
	Failed,
	Incompatible,
	PendingRestart,
}

/// <summary>插件宿主运行时配置。</summary>
internal sealed record PluginRuntimeOptions
{
	public required string PluginsDirectory { get; init; }
	public required string DataDirectory { get; init; }
	public string? PackageInboxDirectory { get; init; }
	public string? StagingDirectory { get; init; }
	public PluginApiVersion HostApiVersion { get; init; } = new(2, 0);
	public PluginVersion HostVersion { get; init; } = new(1, 0, 0);
	public bool DevelopmentHost { get; init; }
	public bool SafeMode { get; init; }
	public IReadOnlyCollection<string> KnownCapabilityIds { get; init; } =
	[
		PluginCapabilityIds.WebView,
	];
	public Func<PluginDescriptor, CancellationToken, IEnumerable<IPluginCapability>>? CapabilityFactory { get; init; }
	public Func<string, string, Uri>? AssetUriFactory { get; init; }
	public Func<string, CancellationToken, Task>? ClosePluginWindowsAsync { get; init; }
	public Action<PluginException>? OnError { get; init; }
	public Action<PluginDescriptor, string, Exception?>? OnLog { get; init; }
	public TimeSpan ActivationTimeout { get; init; } = TimeSpan.FromSeconds(15);
	public TimeSpan DeactivationTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>活跃插件聊天卡片部件 (约定 web/card.html)。</summary>
public sealed record PluginChatWidget(string PluginId, string Title, Uri EntryUrl);

/// <summary>插件当前状态快照。只包含可安全暴露给宿主 UI 的运行时信息。</summary>
internal sealed record PluginInfo(
	string Id,
	PluginManifest Manifest,
	PluginLifecycleState State,
	string? ErrorCode)
{
	public bool UserEnabled { get; init; } = true;
	public string? ErrorMessage { get; init; }
	public bool RequiresRestart { get; init; }
	public IReadOnlyList<PluginCapabilityStatus> CapabilityStatuses { get; init; } = [];
}

internal sealed record PluginUninstallResult(bool Success, bool RequiresRestart, PluginInfo? Plugin);

/// <summary>插件发现、加载、激活、停用和卸载管理器。</summary>
internal sealed class PluginManager : IAsyncDisposable
{
	private readonly PluginRuntimeOptions _options;
	private readonly PluginPackageInstaller _installer;
	private readonly PluginLoader _loader = new();
	private readonly ConcurrentDictionary<string, PluginHandle> _plugins = new(StringComparer.Ordinal);
	private readonly CancellationTokenSource _shutdownSource = new();
	private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
	private readonly PluginStartupRecoveryStore _startupRecovery;
	private readonly PluginStateStore _stateStore;
	private int _disposed;

	/// <summary>活跃插件集合发生变化 (激活完成 / 停用完成) 时触发，供宿主刷新贡献派生状态。</summary>
	public event Action? ActivePluginsChanged;

	private void RaiseActiveChanged() => ActivePluginsChanged?.Invoke();

	public PluginManager(PluginRuntimeOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentException.ThrowIfNullOrWhiteSpace(options.PluginsDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(options.DataDirectory);
		if (options.ActivationTimeout <= TimeSpan.Zero || options.DeactivationTimeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(options), "插件生命周期超时必须大于零");
		_options = options;
		Directory.CreateDirectory(options.PluginsDirectory);
		Directory.CreateDirectory(options.DataDirectory);
		string runtimeDirectory = Path.Combine(options.DataDirectory, "runtime");
		Directory.CreateDirectory(runtimeDirectory);
		_startupRecovery = new PluginStartupRecoveryStore(Path.Combine(runtimeDirectory, "plugin-startup.json"));
		_stateStore = new PluginStateStore(Path.Combine(runtimeDirectory, "plugin-state.json"));
		_installer = new PluginPackageInstaller(options.PluginsDirectory, options.PackageInboxDirectory, options.StagingDirectory);
		CompletePendingUninstalls();
	}

	public IReadOnlyCollection<PluginInfo> Plugins => _plugins.Values.Select(CreateInfo).ToArray();
	public PluginPackageInstaller Installer => _installer;
	internal bool IsSafeMode => _options.SafeMode;

	/// <summary>读取当前版本的 manifest。发现阶段不会创建插件 ALC。</summary>
	public IReadOnlyCollection<PluginInfo> Discover()
	{
		EnsureNotDisposed();
		CompletePendingUninstalls();
		if (!_options.SafeMode) InstallInboxPackages();
		HashSet<string> seen = new(StringComparer.Ordinal);
		foreach (string id in _installer.InstalledIds.Order(StringComparer.Ordinal))
		{
			try
			{
				if (!PluginManifestReader.IsValidPluginId(id)) continue;
				seen.Add(id);
				string? directory = _installer.ResolveCurrentDirectory(id);
				if (directory is null) throw new PluginException(PluginErrorCodes.InvalidPackage, "current.json 指向的版本不存在");
				PluginManifest manifest = PluginManifestReader.Read(Path.Combine(directory, PluginPackageInstaller.ManifestFileName));
				if (!string.Equals(id, manifest.Id, StringComparison.Ordinal))
					throw new PluginException(PluginErrorCodes.InvalidManifest, "插件目录 ID 与 manifest.json 不一致");

				(bool compatible, string? compatibilityCode) = Compatibility(manifest);
				bool userEnabled = _stateStore.IsEnabled(manifest.Id);
				PluginLifecycleState state;
				string? errorCode;
				string? errorMessage;

				if (!compatible)
				{
					state = PluginLifecycleState.Incompatible;
					errorCode = compatibilityCode;
					errorMessage = FriendlyMessage(compatibilityCode);
				}
				else if (_stateStore.TryGetPendingUninstall(manifest.Id, out _))
				{
					state = PluginLifecycleState.PendingRestart;
					errorCode = PluginErrorCodes.UninstallPendingRestart;
					errorMessage = "插件将在重启后完成卸载";
				}
				else if (_options.SafeMode)
				{
					state = PluginLifecycleState.Disabled;
					errorCode = PluginErrorCodes.SafeModeDisabled;
					errorMessage = "安全模式临时禁用了第三方插件";
				}
				else if (!userEnabled)
				{
					state = PluginLifecycleState.Disabled;
					errorCode = PluginErrorCodes.UserDisabled;
					errorMessage = "插件已由用户禁用";
				}
				else if (_startupRecovery.IsDisabled(manifest.Id))
				{
					state = PluginLifecycleState.Disabled;
					errorCode = PluginErrorCodes.StartupRecoveryDisabled;
					errorMessage = "插件因连续启动失败被保护性禁用";
				}
				else
				{
					state = PluginLifecycleState.Discovered;
					errorCode = null;
					errorMessage = null;
				}

				Register(directory, manifest, state, errorCode, errorMessage);
			}
			catch (PluginException exception)
			{
				Report(exception);
			}
		}

		foreach (string stale in _plugins.Keys.Where(id => !seen.Contains(id)).ToArray())
		{
			PluginHandle handle = _plugins[stale];
			if (handle.Instance is null && handle.LoadContext is null) _plugins.TryRemove(stale, out _);
		}
		ValidateDependencies();
		return Plugins;
	}

	/// <summary>安装本地 .noripack。新安装默认禁用，安全模式拒绝执行安装。</summary>
	public async Task<PluginManifest> InstallAsync(string packagePath, CancellationToken cancellationToken = default)
	{
		EnsureNotDisposed();
		if (_options.SafeMode) throw new PluginException(PluginErrorCodes.SafeModeDisabled, "安全模式下不允许安装插件");
		await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			PluginManifest inspected = _installer.InspectPackage(packagePath);
			bool existed = _installer.ResolveCurrentDirectory(inspected.Id) is not null;
			PluginManifest manifest = await Task.Run(() => _installer.Install(packagePath, cancellationToken), cancellationToken).ConfigureAwait(false);
			if (!existed) _stateStore.SetEnabled(manifest.Id, false);
			Discover();
			return manifest;
		}
		finally
		{
			_lifecycleGate.Release();
		}
	}


	/// <summary>返回依赖优先的插件快照顺序。</summary>
	public IReadOnlyList<PluginInfo> DependencyOrder()
	{
		EnsureNotDisposed();
		ValidateDependencies();
		List<PluginInfo> ordered = [];
		HashSet<string> visiting = new(StringComparer.Ordinal);
		HashSet<string> visited = new(StringComparer.Ordinal);
		foreach (PluginHandle handle in _plugins.Values.OrderBy(item => item.Manifest.Id, StringComparer.Ordinal))
		{
			if (handle.State is PluginLifecycleState.Discovered or PluginLifecycleState.Installed)
				Visit(handle, visiting, visited, ordered);
		}
		return ordered;
	}

	/// <summary>按依赖顺序激活全部可用插件。单个插件失败不会阻断宿主。</summary>
	public async Task StartAllAsync(CancellationToken cancellationToken = default)
	{
		EnsureNotDisposed();
		if (_options.SafeMode)
		{
			DisableAllThirdPartyPlugins();
			return;
		}
		IReadOnlyList<PluginInfo> ordered;
		try { ordered = DependencyOrder(); }
		catch (PluginException exception)
		{
			Report(exception);
			return;
		}
		foreach (PluginInfo info in ordered)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try { await ActivateAsync(info.Id, cancellationToken).ConfigureAwait(false); }
			catch (PluginException) { }
		}
	}

	/// <summary>显式启用并热激活一个插件。</summary>
	public async Task EnableAsync(string pluginId, CancellationToken cancellationToken = default)
	{
		EnsureNotDisposed();
		ValidatePluginId(pluginId);
		if (_options.SafeMode) throw new PluginException(PluginErrorCodes.SafeModeDisabled, "安全模式下不允许启用插件");
		await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			Discover();
			PluginHandle handle = GetRequiredHandle(pluginId);
			if (_stateStore.TryGetPendingUninstall(pluginId, out _))
				throw new PluginException(PluginErrorCodes.UninstallPendingRestart, "插件正在等待重启后卸载");
			if (handle.State == PluginLifecycleState.Incompatible)
				throw new PluginException(handle.ErrorCode ?? PluginErrorCodes.IncompatibleHost, "插件与当前宿主不兼容");

			_stateStore.SetEnabled(pluginId, true);
			_startupRecovery.Clear(pluginId);
			if (handle.State == PluginLifecycleState.Active) return;
			handle.State = PluginLifecycleState.Discovered;
			handle.ErrorCode = null;
			handle.ErrorMessage = null;
			ValidateDependenciesFor(handle);
			await ActivateCoreAsync(handle, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_lifecycleGate.Release();
		}
	}

	/// <summary>加载并激活一个已启用插件。宿主启动流程使用此入口。</summary>
	public async Task ActivateAsync(string pluginId, CancellationToken cancellationToken = default)
	{
		EnsureNotDisposed();
		ValidatePluginId(pluginId);
		if (_options.SafeMode)
		{
			DisableAllThirdPartyPlugins();
			return;
		}
		await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			PluginHandle handle = GetRequiredHandle(pluginId);
			if (!_stateStore.IsEnabled(pluginId))
				throw new PluginException(PluginErrorCodes.UserDisabled, "插件已由用户禁用");
			if (_startupRecovery.IsDisabled(pluginId))
				throw new PluginException(PluginErrorCodes.StartupRecoveryDisabled, "插件已被启动失败保护禁用");
			if (handle.State == PluginLifecycleState.Active) return;
			if (handle.State == PluginLifecycleState.Incompatible)
				throw new PluginException(handle.ErrorCode ?? PluginErrorCodes.IncompatibleHost, "插件与当前宿主不兼容");
			if (handle.State == PluginLifecycleState.PendingRestart)
				throw new PluginException(handle.ErrorCode ?? PluginErrorCodes.UnloadPendingRestart, "插件需要重启后继续操作");
			ValidateDependenciesFor(handle);
			await ActivateCoreAsync(handle, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_lifecycleGate.Release();
		}
	}

	/// <summary>停用一个插件并撤销所有贡献，不改变用户启用意图。</summary>
	public async Task DeactivateAsync(string pluginId, CancellationToken cancellationToken = default)
	{
		EnsureNotDisposed();
		ValidatePluginId(pluginId);
		await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_plugins.TryGetValue(pluginId, out PluginHandle? handle))
			{
				try { await DeactivateCoreAsync(pluginId, cancellationToken, disabled: false).ConfigureAwait(false); }
				finally { UnloadContext(handle); }
			}
		}
		finally { _lifecycleGate.Release(); }
	}


	/// <summary>显式禁用插件并保留安装文件与用户数据。</summary>
	public async Task DisableAsync(string pluginId, CancellationToken cancellationToken = default)
	{
		EnsureNotDisposed();
		ValidatePluginId(pluginId);
		await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			Discover();
			PluginHandle handle = GetRequiredHandle(pluginId);
			EnsureNoActiveDependents(pluginId);
			_stateStore.SetEnabled(pluginId, false);
			try
			{
				await DeactivateCoreAsync(pluginId, cancellationToken, disabled: true).ConfigureAwait(false);
			}
			finally
			{
				bool unloaded = UnloadContext(handle);
				if (unloaded)
				{
					handle.UpdatePendingRestart = false;
					handle.State = PluginLifecycleState.Disabled;
					handle.ErrorCode = PluginErrorCodes.UserDisabled;
					handle.ErrorMessage = "插件已由用户禁用";
				}
				else
				{
					handle.State = PluginLifecycleState.PendingRestart;
					handle.ErrorCode = PluginErrorCodes.UnloadPendingRestart;
					handle.ErrorMessage = "插件程序集仍被占用，重启后完成禁用";
				}
			}
		}
		finally { _lifecycleGate.Release(); }
	}

	/// <summary>禁用并安全删除插件安装目录；用户数据默认保留。</summary>
	public async Task<PluginUninstallResult> UninstallAsync(string pluginId, bool deleteData = false, CancellationToken cancellationToken = default)
	{
		EnsureNotDisposed();
		ValidatePluginId(pluginId);
		await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			Discover();
			PluginHandle handle = GetRequiredHandle(pluginId);
			EnsureNoActiveDependents(pluginId);
			_stateStore.SetEnabled(pluginId, false);

			try { await DeactivateCoreAsync(pluginId, cancellationToken, disabled: true).ConfigureAwait(false); }
			catch (PluginException) { /* cleanup still decides whether uninstall can continue */ }

			if (!UnloadContext(handle))
			{
				_stateStore.SetPendingUninstall(pluginId, deleteData);
				handle.State = PluginLifecycleState.PendingRestart;
				handle.ErrorCode = PluginErrorCodes.UninstallPendingRestart;
				handle.ErrorMessage = "插件正在使用中，将在重启后完成卸载";
				return new PluginUninstallResult(true, true, CreateInfo(handle));
			}

			try
			{
				_installer.Uninstall(pluginId);
				if (deleteData) DeletePluginData(pluginId);
				_plugins.TryRemove(pluginId, out _);
				_startupRecovery.Clear(pluginId);
				_stateStore.Remove(pluginId);
				return new PluginUninstallResult(true, false, null);
			}
			catch (PluginException exception) when (exception.Code == PluginErrorCodes.UnloadPendingRestart)
			{
				_stateStore.SetPendingUninstall(pluginId, deleteData);
				handle.State = PluginLifecycleState.PendingRestart;
				handle.ErrorCode = PluginErrorCodes.UninstallPendingRestart;
				handle.ErrorMessage = "插件文件当前被占用，将在重启后完成卸载";
				return new PluginUninstallResult(true, true, CreateInfo(handle));
			}
		}
		finally { _lifecycleGate.Release(); }
	}

	/// <summary>安全模式钩子：不创建 ALC、不调用任何第三方入口。</summary>
	public void DisableAllThirdPartyPlugins()
	{
		foreach (PluginHandle handle in _plugins.Values)
		{
			handle.State = PluginLifecycleState.Disabled;
			handle.ErrorCode = PluginErrorCodes.SafeModeDisabled;
			handle.ErrorMessage = "安全模式临时禁用了第三方插件";
		}
	}

	/// <summary>返回当前活动插件提供的指定类型贡献快照。</summary>
	public IReadOnlyList<T> GetContributions<T>()
		where T : class, IPluginContribution =>
		_plugins.Values.Where(handle => handle.State == PluginLifecycleState.Active)
			.SelectMany(handle => handle.Contributions.GetAll<T>())
			.ToArray();

	/// <summary>
	/// 返回活跃插件的聊天卡片部件: 约定为插件包内存在 web/card.html,
	/// 标题取插件名, 入口为宿主资源服务的同源 URL。由宿主聊天界面的通用卡片槽挂载。
	/// </summary>
	public IReadOnlyList<PluginChatWidget> GetChatWidgets()
	{
		List<PluginChatWidget> widgets = [];
		foreach (PluginHandle handle in _plugins.Values.Where(handle => handle.State == PluginLifecycleState.Active))
		{
			if (!File.Exists(Path.Combine(handle.Directory, "web", "card.html"))) continue;
			Uri entry = _options.AssetUriFactory?.Invoke(handle.Manifest.Id, "web/card.html")
				?? new Uri(Path.Combine(handle.Directory, "web", "card.html"), UriKind.Absolute);
			widgets.Add(new PluginChatWidget(handle.Manifest.Id, handle.Manifest.Name, entry));
		}
		return widgets;
	}

	/// <summary>返回当前活动插件提供的指定类型贡献及其来源插件描述。</summary>
	public IReadOnlyList<(PluginDescriptor Plugin, T Contribution)> GetContributionsWithSource<T>()
		where T : class, IPluginContribution =>
		_plugins.Values.Where(handle => handle.State == PluginLifecycleState.Active)
			.SelectMany(handle => handle.Contributions.GetAll<T>().Select(contribution => (Plugin: new PluginDescriptor
			{
				Id = handle.Manifest.Id,
				Name = handle.Manifest.Name,
				Description = handle.Manifest.Description,
				Version = handle.Manifest.Version,
				ApiVersion = handle.Manifest.ApiVersion,
				Capabilities = handle.Manifest.Capabilities,
			}, Contribution: contribution)))
			.ToArray();

	/// <summary>返回当前插件的安装目录，供 AssetServer 做公开资源映射。</summary>
	public string? ResolveAssetRoot(string pluginId) =>
		_plugins.TryGetValue(pluginId, out PluginHandle? handle)
			? handle.Directory
			: _installer.ResolveCurrentDirectory(pluginId);

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_shutdownSource.Cancel();
		foreach (string id in _plugins.Keys.ToArray())
		{
			bool acquired = false;
			try
			{
				acquired = await _lifecycleGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
				if (!acquired)
				{
					if (_plugins.TryGetValue(id, out PluginHandle? timedOutHandle)) timedOutHandle.State = PluginLifecycleState.PendingRestart;
					continue;
				}
				try
				{
					if (_plugins.TryGetValue(id, out PluginHandle? handle))
					{
						try { await DeactivateCoreAsync(id, CancellationToken.None, disabled: false).ConfigureAwait(false); }
						finally { UnloadContext(handle); }
					}
				}
				finally { if (acquired) _lifecycleGate.Release(); }
			}
			catch (Exception exception) when (exception is PluginException or TimeoutException or OperationCanceledException)
			{
				if (_plugins.TryGetValue(id, out PluginHandle? handle)) handle.State = PluginLifecycleState.PendingRestart;
			}
		}
		_shutdownSource.Dispose();
		_lifecycleGate.Dispose();
	}

	private async Task ActivateCoreAsync(PluginHandle handle, CancellationToken cancellationToken)
	{
		handle.State = PluginLifecycleState.Loading;
		handle.ErrorMessage = null;
		PluginLoadContext? loadContext = null;
		try
		{
			PluginManifestReader.EnsureCompatible(_options.HostApiVersion, handle.Manifest.Api);
			if (!_options.DevelopmentHost && !PluginManifestReader.IsHostVersionSupported(_options.HostVersion, handle.Manifest.MinHostVersion))
				throw new PluginException(PluginErrorCodes.IncompatibleHost, "宿主版本低于插件要求");
			handle.Context = CreateContext(handle);
			ValidateRequiredCapabilities(handle.Context.CapabilityRegistry, handle.Manifest);
			INoriPlugin instance = _loader.Load(handle.Manifest, handle.Directory, out loadContext);

			handle.LoadContext = loadContext;
			handle.Instance = instance;
			await instance.ActivateAsync(handle.Context, cancellationToken).AsTask().WaitAsync(_options.ActivationTimeout, cancellationToken).ConfigureAwait(false);
			handle.State = PluginLifecycleState.Active;
			handle.UpdatePendingRestart = false;
			handle.ErrorCode = null;
			handle.ErrorMessage = null;
			ClearStartupFailure(handle.Manifest.Id);
			RaiseActiveChanged();
		}
		catch (PluginException exception)
		{
			FailActivation(handle, exception, loadContext);
			throw;
		}
		catch (Exception exception)
		{
			PluginException wrapped = new(PluginErrorCodes.ActivationFailed, $"插件激活失败: {handle.Manifest.Name}", exception);
			FailActivation(handle, wrapped, loadContext);
			throw wrapped;
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "插件停止阶段必须继续撤销其余资源，不能让单个插件异常阻断卸载。")]
	private async Task DeactivateCoreAsync(string pluginId, CancellationToken cancellationToken, bool disabled)
	{
		if (!_plugins.TryGetValue(pluginId, out PluginHandle? handle)) return;
		if (handle.Instance is null)
		{
			handle.Context?.Revoke();
			handle.Context?.Dispose();
			handle.Context = null;
			handle.Contributions.RevokeAll();
			if (disabled) handle.State = PluginLifecycleState.Disabled;
			return;
		}

		handle.State = PluginLifecycleState.Stopping;
		try { handle.StopSource?.Cancel(throwOnFirstException: false); } catch { }
		try { handle.Contributions.RevokeAll(); } catch { }
		try { handle.Context?.Revoke(); } catch { }

		PluginException? failure = null;
		if (_options.ClosePluginWindowsAsync is not null)
		{
			try
			{
				await _options.ClosePluginWindowsAsync(pluginId, cancellationToken).WaitAsync(_options.DeactivationTimeout, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				failure = new PluginException(PluginErrorCodes.DeactivationFailed, "插件窗口关闭失败", exception);
				Report(failure);
			}
		}

		try
		{
			await handle.Instance.DeactivateAsync(cancellationToken).AsTask().WaitAsync(_options.DeactivationTimeout, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			PluginException deactivateFailure = exception is PluginException pluginException && pluginException.Code == PluginErrorCodes.DeactivationFailed
				? pluginException
				: new PluginException(PluginErrorCodes.DeactivationFailed, $"插件停用失败: {handle.Manifest.Name}", exception);
			failure ??= deactivateFailure;
			Report(deactivateFailure);
		}
		finally
		{
			handle.Instance = null;
			handle.Context?.Dispose();
			handle.Context = null;
			handle.StopSource = null;
			handle.State = failure is null ? (disabled ? PluginLifecycleState.Disabled : PluginLifecycleState.Installed) : PluginLifecycleState.Failed;
			handle.ErrorCode = failure?.Code;
			handle.ErrorMessage = failure is null ? null : SanitizeMessage(failure.Message);
			RaiseActiveChanged();
		}
		if (failure is not null) throw failure;
	}

	private static PluginDescriptor ToDescriptor(PluginHandle handle) => new()
	{
		Id = handle.Manifest.Id,
		Name = handle.Manifest.Name,
		Description = handle.Manifest.Description,
		Version = handle.Manifest.Version,
		ApiVersion = handle.Manifest.ApiVersion,
		Capabilities = handle.Manifest.Capabilities.Concat(handle.Manifest.OptionalCapabilities).Distinct(StringComparer.Ordinal).ToArray(),
	};

	private PluginContext CreateContext(PluginHandle handle)
	{
		PluginDescriptor descriptor = ToDescriptor(handle);
		CancellationTokenSource stopSource = CancellationTokenSource.CreateLinkedTokenSource(_shutdownSource.Token);
		handle.StopSource = stopSource;
		IEnumerable<IPluginCapability> capabilities = _options.CapabilityFactory?.Invoke(descriptor, stopSource.Token) ?? [];
		PluginCapabilityRegistry capabilityRegistry = new(
			handle.Manifest.Capabilities.Concat(handle.Manifest.OptionalCapabilities),
			_options.KnownCapabilityIds,
			capabilities);
		return new PluginContext
		{
			Plugin = descriptor,
			Logger = new PluginLogger((message, exception) => _options.OnLog?.Invoke(descriptor, message, exception)),
			Storage = new JsonPluginStorage(Path.Combine(_options.DataDirectory, handle.Manifest.Id)),
			Assets = new PluginAssetProvider(handle.Directory, path => _options.AssetUriFactory?.Invoke(handle.Manifest.Id, path) ?? new Uri(Path.Combine(handle.Directory, path.Replace('/', Path.DirectorySeparatorChar)), UriKind.Absolute)),
			Contributions = handle.Contributions,
			Capabilities = capabilityRegistry,
			CapabilityRegistry = capabilityRegistry,
			ContributionRegistry = handle.Contributions,
			StoppingSource = stopSource,
		};
	}

	private static void ValidateRequiredCapabilities(PluginCapabilityRegistry registry, PluginManifest manifest)
	{
		foreach (PluginCapabilityStatus status in registry.Statuses.Where(status => manifest.Capabilities.Contains(status.Id, StringComparer.Ordinal)))
		{
			if (!status.Granted) throw new PluginException(PluginErrorCodes.CapabilityNotGranted, $"插件能力未获授权: {status.Id}");
			if (!status.Available) throw new PluginException(PluginErrorCodes.CapabilityUnavailable, $"插件能力当前不可用: {status.Id}");
		}
	}

	private void FailActivation(PluginHandle handle, PluginException exception, PluginLoadContext? loadContext)
	{
		handle.ErrorCode = exception.Code;
		handle.ErrorMessage = SanitizeMessage(exception.Message);
		handle.State = PluginLifecycleState.Failed;
		RecordStartupFailure(handle);
		handle.Context?.Revoke();
		handle.Context?.Dispose();
		if (handle.Context is null) handle.StopSource?.Dispose();
		handle.Context = null;
		handle.Instance = null;
		handle.StopSource = null;
		try { (loadContext ?? handle.LoadContext)?.Unload(); } catch { handle.State = PluginLifecycleState.PendingRestart; }
		handle.LoadContext = null;
		Report(exception, handle);
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1854", Justification = "清除局部加载上下文引用以便弱引用卸载检查，避免 GC 保留插件上下文。")]
	private bool UnloadContext(PluginHandle handle)
	{
		PluginLoadContext? context = handle.LoadContext;
		handle.LoadContext = null;
		if (context is null) return true;
		WeakReference weak;
		try { weak = UnloadAndTrack(context); }
		catch (Exception exception)
		{
			handle.State = PluginLifecycleState.PendingRestart;
			handle.ErrorCode = PluginErrorCodes.UnloadPendingRestart;
			handle.ErrorMessage = "插件程序集无法卸载，需要重启";
			Report(new PluginException(PluginErrorCodes.UnloadPendingRestart, "插件程序集无法卸载", exception), handle);
			return false;
		}
		context = null;
		for (int index = 0; index < 10 && weak.IsAlive; index++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			if (weak.IsAlive) Thread.Sleep(10);
		}
		if (!weak.IsAlive) return true;
		handle.State = PluginLifecycleState.PendingRestart;
		handle.ErrorCode = PluginErrorCodes.UnloadPendingRestart;
		handle.ErrorMessage = "插件程序集仍被引用，需要重启";
		return false;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static WeakReference UnloadAndTrack(PluginLoadContext context)
	{
		WeakReference weak = new(context);
		context.Unload();
		return weak;
	}

	private void Register(string directory, PluginManifest manifest, PluginLifecycleState state, string? errorCode, string? errorMessage)
	{
		if (_plugins.TryGetValue(manifest.Id, out PluginHandle? existing))
		{
			bool sameDirectory = PathsEqual(existing.Directory, directory);
			if (existing.State == PluginLifecycleState.Active)
			{
				if (!sameDirectory)
				{
					// current.json 已切到新版本，但旧版本实例仍在本进程运行。此时不能把
					// LifecycleState 改成 PendingRestart，否则宿主会立刻停止枚举其贡献，
					// 形成“代码仍运行但贡献消失”的半活动状态。保持 Active，并单独标记
					// 重启需求；下次启动自然从 current.json 加载新版本。
					existing.UpdatePendingRestart = true;
					existing.ErrorCode = null;
					existing.ErrorMessage = "已安装新版本，重启后切换";
				}
				else if (existing.UpdatePendingRestart)
				{
					existing.UpdatePendingRestart = false;
					existing.ErrorCode = null;
					existing.ErrorMessage = null;
				}
				return;
			}
			if (sameDirectory && existing.State is PluginLifecycleState.Failed or PluginLifecycleState.PendingRestart
				&& state is PluginLifecycleState.Discovered or PluginLifecycleState.Installed)
				return;
		}
		_plugins[manifest.Id] = new PluginHandle(directory, manifest, state, errorCode, errorMessage);
	}

	private void InstallInboxPackages()
	{
		foreach (string packagePath in Directory.EnumerateFiles(_installer.InboxDirectory, "*.noripack").Order(StringComparer.OrdinalIgnoreCase))
		{
			try
			{
				PluginManifest manifest = _installer.InspectPackage(packagePath);
				if (!_installer.IsVersionInstalled(manifest.Id, manifest.Version))
				{
					bool existed = _installer.ResolveCurrentDirectory(manifest.Id) is not null;
					_installer.Install(packagePath);
					if (!existed) _stateStore.SetEnabled(manifest.Id, false);
				}
			}
			catch (PluginException exception)
			{
				Report(exception);
			}
		}
	}

	private void ValidateDependencies()
	{
		foreach (PluginHandle handle in _plugins.Values)
		{
			if (handle.State is PluginLifecycleState.Disabled or PluginLifecycleState.Failed or PluginLifecycleState.PendingRestart) continue;
			foreach (PluginDependency dependency in handle.Manifest.Dependencies)
			{
				if (!_plugins.TryGetValue(dependency.Id, out PluginHandle? target))
				{
					if (!dependency.Optional) SetIncompatible(handle, PluginErrorCodes.MissingDependency);
					continue;
				}
				if (!PluginRange.Satisfies(target.Manifest.PluginVersion, dependency.Version) && !dependency.Optional)
					SetIncompatible(handle, PluginErrorCodes.IncompatibleHost);
			}
		}
	}

	private void ValidateDependenciesFor(PluginHandle handle)
	{
		foreach (PluginDependency dependency in handle.Manifest.Dependencies)
		{
			if (!_plugins.TryGetValue(dependency.Id, out PluginHandle? target))
			{
				if (!dependency.Optional) throw new PluginException(PluginErrorCodes.MissingDependency, $"缺少插件依赖: {dependency.Id}");
				continue;
			}
			if (!PluginRange.Satisfies(target.Manifest.PluginVersion, dependency.Version) && !dependency.Optional)
				throw new PluginException(PluginErrorCodes.IncompatibleHost, $"插件依赖版本不满足: {dependency.Id}");
			if (!dependency.Optional && target.State != PluginLifecycleState.Active && !string.Equals(target.Manifest.Id, handle.Manifest.Id, StringComparison.Ordinal))
				throw new PluginException(PluginErrorCodes.MissingDependency, $"插件依赖尚未激活: {dependency.Id}");
		}
	}

	private void EnsureNoActiveDependents(string pluginId)
	{
		PluginHandle? dependent = _plugins.Values.FirstOrDefault(handle =>
			handle.State == PluginLifecycleState.Active &&
			handle.Manifest.Dependencies.Any(dependency => !dependency.Optional && string.Equals(dependency.Id, pluginId, StringComparison.Ordinal)));
		if (dependent is not null)
			throw new PluginException(PluginErrorCodes.DependencyInUse, $"插件仍被活动依赖使用: {dependent.Manifest.Id}");
	}

	private void Visit(PluginHandle handle, HashSet<string> visiting, HashSet<string> visited, List<PluginInfo> ordered)
	{
		if (visited.Contains(handle.Manifest.Id)) return;
		if (!visiting.Add(handle.Manifest.Id)) throw new PluginException(PluginErrorCodes.DependencyCycle, $"插件依赖存在循环: {handle.Manifest.Id}");
		foreach (PluginDependency dependency in handle.Manifest.Dependencies.Where(item => !item.Optional))
		{
			if (!_plugins.TryGetValue(dependency.Id, out PluginHandle? target)) throw new PluginException(PluginErrorCodes.MissingDependency, $"缺少插件依赖: {dependency.Id}");
			if (target.State is PluginLifecycleState.Discovered or PluginLifecycleState.Installed) Visit(target, visiting, visited, ordered);
		}
		visiting.Remove(handle.Manifest.Id);
		visited.Add(handle.Manifest.Id);
		ordered.Add(CreateInfo(handle));
	}

	private void SetIncompatible(PluginHandle handle, string code)
	{
		if (handle.State is PluginLifecycleState.Active or PluginLifecycleState.Loading) return;
		handle.State = PluginLifecycleState.Incompatible;
		handle.ErrorCode = code;
		handle.ErrorMessage = FriendlyMessage(code);
	}

	private (bool Compatible, string? ErrorCode) Compatibility(PluginManifest manifest)
	{
		if (!PluginManifestReader.IsPlatformSupported(manifest.Platforms)) return (false, PluginErrorCodes.UnsupportedPlatform);
		if (!PluginManifestReader.IsCompatible(_options.HostApiVersion, manifest.Api)) return (false, PluginErrorCodes.IncompatibleApi);
		if (!_options.DevelopmentHost && !PluginManifestReader.IsHostVersionSupported(_options.HostVersion, manifest.MinHostVersion)) return (false, PluginErrorCodes.IncompatibleHost);
		if (manifest.Capabilities.Any(capability => !_options.KnownCapabilityIds.Contains(capability, StringComparer.Ordinal))) return (false, PluginErrorCodes.UnknownCapability);
		return (true, null);
	}

	private void RecordStartupFailure(PluginHandle handle)
	{
		bool disabled = _startupRecovery.RecordFailure(handle.Manifest.Id);
		if (disabled)
		{
			handle.State = PluginLifecycleState.Disabled;
			handle.ErrorCode = PluginErrorCodes.StartupRecoveryDisabled;
			handle.ErrorMessage = "插件因连续启动失败被保护性禁用";
		}
	}

	private void ClearStartupFailure(string pluginId) => _startupRecovery.Clear(pluginId);

	private void CompletePendingUninstalls()
	{
		foreach ((string pluginId, bool deleteData) in _stateStore.PendingUninstalls())
		{
			try
			{
				_installer.Uninstall(pluginId);
				if (deleteData) DeletePluginData(pluginId);
				_startupRecovery.Clear(pluginId);
				_stateStore.Remove(pluginId);
				_plugins.TryRemove(pluginId, out _);
			}
			catch (PluginException exception)
			{
				Report(exception);
			}
		}
	}

	private void DeletePluginData(string pluginId)
	{
		ValidatePluginId(pluginId);
		string dataRoot = Path.GetFullPath(_options.DataDirectory);
		string pluginData = Path.GetFullPath(Path.Combine(dataRoot, pluginId));
		PluginPathSafety.EnsureTreeNoReparsePoints(dataRoot, pluginData, PluginErrorCodes.StorageFailed, "插件数据目录越过宿主管理边界或包含符号链接");
		if (!Directory.Exists(pluginData)) return;
		try { Directory.Delete(pluginData, recursive: true); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new PluginException(PluginErrorCodes.StorageFailed, "插件数据删除失败", exception);
		}
	}

	private PluginInfo CreateInfo(PluginHandle handle)
	{
		IReadOnlyList<PluginCapabilityStatus> statuses = handle.Context?.CapabilityRegistry.Statuses ??
			handle.Manifest.Capabilities.Concat(handle.Manifest.OptionalCapabilities)
				.Distinct(StringComparer.Ordinal)
				.OrderBy(id => id, StringComparer.Ordinal)
				.Select(id => new PluginCapabilityStatus(id, true, _options.KnownCapabilityIds.Contains(id, StringComparer.Ordinal), false))
				.ToArray();
		return new PluginInfo(handle.Manifest.Id, handle.Manifest, handle.State, handle.ErrorCode)
		{
			UserEnabled = _stateStore.IsEnabled(handle.Manifest.Id),
			ErrorMessage = handle.ErrorMessage,
			RequiresRestart = handle.State == PluginLifecycleState.PendingRestart || handle.UpdatePendingRestart,
			CapabilityStatuses = statuses,
		};
	}

	private PluginHandle GetRequiredHandle(string pluginId)
	{
		if (_plugins.TryGetValue(pluginId, out PluginHandle? handle)) return handle;
		throw new PluginException(PluginErrorCodes.PluginNotFound, "插件不存在或尚未安装");
	}

	private static void ValidatePluginId(string pluginId)
	{
		if (!PluginManifestReader.IsValidPluginId(pluginId))
			throw new PluginException(PluginErrorCodes.InvalidPluginId, "插件 ID 无效");
	}

	private string SanitizeMessage(string? message)
	{
		if (string.IsNullOrWhiteSpace(message)) return "插件操作失败";
		string sanitized = message.Replace('\r', ' ').Replace('\n', ' ')
			.Replace(_options.PluginsDirectory, "<plugins>", StringComparison.OrdinalIgnoreCase)
			.Replace(_options.DataDirectory, "<plugin-data>", StringComparison.OrdinalIgnoreCase);
		while (sanitized.Contains("  ", StringComparison.Ordinal)) sanitized = sanitized.Replace("  ", " ", StringComparison.Ordinal);
		sanitized = sanitized.Trim();
		return sanitized.Length <= 512 ? sanitized : sanitized[..512];
	}

	private static string? FriendlyMessage(string? code) => code switch
	{
		PluginErrorCodes.UnsupportedPlatform => "插件不支持当前平台",
		PluginErrorCodes.IncompatibleApi => "插件 API 与当前宿主不兼容",
		PluginErrorCodes.IncompatibleHost => "当前 Nori 版本不满足插件要求",
		PluginErrorCodes.UnknownCapability => "插件声明了宿主未知的必需权限",
		PluginErrorCodes.MissingDependency => "插件缺少必需依赖",
		_ => null,
	};

	private static bool PathsEqual(string left, string right)
	{
		StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), comparison);
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "诊断回调失败不能阻断主流程或覆盖原始插件错误。")]
	private void Report(PluginException exception, PluginHandle? handle = null)
	{
		try
		{
			_options.OnError?.Invoke(PluginDiagnostics.Attach(
				exception,
				handle?.Manifest.Id,
				handle?.Manifest.Version,
				$"{_options.HostApiVersion.Major}.{_options.HostApiVersion.Minor}",
				_options.HostVersion.ToString()));
		}
		catch { /* 插件窗口关闭失败不应阻断版本提示。 */ }
	}

	private void EnsureNotDisposed()
	{
		if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PluginManager));
	}

	private sealed class PluginHandle
	{
		public PluginHandle(string directory, PluginManifest manifest, PluginLifecycleState state, string? errorCode, string? errorMessage)
		{
			Directory = directory;
			Manifest = manifest;
			State = state;
			ErrorCode = errorCode;
			ErrorMessage = errorMessage;
		}

		public string Directory { get; }
		public PluginManifest Manifest { get; }
		public PluginLifecycleState State { get; set; }
		public string? ErrorCode { get; set; }
		public string? ErrorMessage { get; set; }
		public bool UpdatePendingRestart { get; set; }
		public INoriPlugin? Instance { get; set; }
		public PluginLoadContext? LoadContext { get; set; }
		public PluginContext? Context { get; set; }
		public CancellationTokenSource? StopSource { get; set; }
		public PluginContributionRegistry Contributions { get; } = new();
	}
}
