using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nori.PluginRuntime;

internal sealed class PluginLogger(Action<string, Exception?>? sink = null) : IPluginLogger
{
	private readonly Action<string, Exception?> _sink = sink ?? ((_, _) => { });

	public void Debug(string message) => Write("debug", message, null);
	public void Info(string message) => Write("info", message, null);
	public void Warn(string message) => Write("warn", message, null);
	public void Error(string message, Exception? exception = null) => Write("error", message, exception);

	private void Write(string level, string message, Exception? exception)
	{
		ArgumentNullException.ThrowIfNull(message);
		_sink($"{level}: {message}", exception);
	}
}

internal sealed class PluginRegistration(PluginContributionRegistry owner, IPluginContribution contribution) : IPluginRegistration
{
	private int _disposed;

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Remove(contribution);
	}
}

internal sealed class PluginContributionRegistry : IContributionRegistry
{
	private readonly object _gate = new();
	private readonly HashSet<IPluginContribution> _items = new(ReferenceEqualityComparer.Instance);

	public IPluginRegistration Register<T>(T contribution)
		where T : class, IPluginContribution
	{
		ArgumentNullException.ThrowIfNull(contribution);
		lock (_gate)
		{
			if (!_items.Add(contribution))
				throw new PluginException(PluginErrorCodes.DuplicateContribution, "插件重复注册了同一个贡献对象");
		}
		return new PluginRegistration(this, contribution);
	}

	internal void Remove(IPluginContribution contribution)
	{
		lock (_gate) _items.Remove(contribution);
	}

	internal void RevokeAll()
	{
		lock (_gate) _items.Clear();
	}

	internal IReadOnlyList<T> GetAll<T>()
		where T : class, IPluginContribution
	{
		lock (_gate) return _items.OfType<T>().ToArray();
	}

	internal IReadOnlyList<IPluginContribution> Snapshot()
	{
		lock (_gate) return _items.ToArray();
	}
}

internal sealed class PluginCapabilityRegistry : IPluginCapabilities, IDisposable
{
	private readonly object _gate = new();
	private readonly Dictionary<string, PluginCapabilityStatus> _statuses = new(StringComparer.Ordinal);
	private readonly Dictionary<string, IPluginCapability> _available = new(StringComparer.Ordinal);

	internal PluginCapabilityRegistry(IEnumerable<string> declared, IEnumerable<string> known, IEnumerable<IPluginCapability> available)
	{
		HashSet<string> knownIds = new(known, StringComparer.Ordinal);
		foreach (string id in declared.Distinct(StringComparer.Ordinal))
		{
			bool granted = knownIds.Contains(id);
			_statuses[id] = new PluginCapabilityStatus(id, true, granted, false);
		}
		foreach (IPluginCapability capability in available)
		{
			ArgumentNullException.ThrowIfNull(capability);
			string? id = GetCapabilityId(capability.GetType());
			if (id is null || !_statuses.TryGetValue(id, out PluginCapabilityStatus? status) || !status.Granted) continue;
			_available[id] = capability;
			_statuses[id] = status with { Available = true };
		}
	}

	public IReadOnlyList<PluginCapabilityStatus> Statuses
	{
		get
		{
			lock (_gate) return _statuses.Values.OrderBy(status => status.Id, StringComparer.Ordinal).ToArray();
		}
	}

	public bool TryGet<T>(out T? capability)
		where T : class, IPluginCapability
	{
		lock (_gate)
		{
			foreach (IPluginCapability item in _available.Values)
			{
				if (item is T typed)
				{
					capability = typed;
					return true;
				}
			}
		}
		capability = null;
		return false;
	}

	public T GetRequired<T>()
		where T : class, IPluginCapability
	{
		if (TryGet(out T? capability) && capability is not null) return capability;
		string id = GetCapabilityId(typeof(T)) ?? typeof(T).FullName ?? typeof(T).Name;
		lock (_gate)
		{
			if (!_statuses.TryGetValue(id, out PluginCapabilityStatus? status) || !status.Declared)
				throw new PluginException(PluginErrorCodes.CapabilityMissing, $"插件未声明能力: {id}");
			if (!status.Granted)
				throw new PluginException(PluginErrorCodes.CapabilityNotGranted, $"插件能力未获授权: {id}");
		}
		throw new PluginException(PluginErrorCodes.CapabilityUnavailable, $"插件能力当前不可用: {id}");
	}

	public void Dispose()
	{
		IPluginCapability[] capabilities;
		lock (_gate)
		{
			capabilities = _available.Values.ToArray();
			_available.Clear();
			foreach (string id in _statuses.Keys.ToArray())
			{
				PluginCapabilityStatus status = _statuses[id];
				_statuses[id] = status with { Available = false };
			}
		}
		foreach (IPluginCapability capability in capabilities)
		{
			if (capability is IDisposable disposable) disposable.Dispose();
		}
	}

	private static string? GetCapabilityId(Type type)
	{
		return type.GetCustomAttribute<PluginCapabilityAttribute>()?.Id
			?? type.GetInterfaces().Select(interfaceType => interfaceType.GetCustomAttribute<PluginCapabilityAttribute>()?.Id).FirstOrDefault(id => id is not null);
	}
}

/// <summary>插件独立 JSON 存储。</summary>
internal sealed class JsonPluginStorage : IPluginStorage
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
	private readonly object _gate = new();
	private readonly string _path;
	private JsonObject _values;

	public JsonPluginStorage(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		string fullDirectory = Path.GetFullPath(directory);
		Directory.CreateDirectory(fullDirectory);
		EnsureNoReparsePoints(fullDirectory);
		_path = Path.Combine(fullDirectory, "storage.json");
		_values = Load();
	}

	public ValueTask<JsonNode?> GetAsync(string key, CancellationToken cancellationToken = default)
	{
		ValidateKey(key);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate) return ValueTask.FromResult(_values[key]?.DeepClone());
	}

	public ValueTask SetAsync(string key, JsonNode? value, CancellationToken cancellationToken = default)
	{
		ValidateKey(key);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			JsonNode? previous = _values[key];
			_values[key] = value?.DeepClone();
			try { Save(); }
			catch
			{
				_values[key] = previous;
				throw;
			}
		}
		return ValueTask.CompletedTask;
	}

	public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
	{
		ValidateKey(key);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			JsonNode? previous = _values[key];
			_values.Remove(key);
			try { Save(); }
			catch
			{
				_values[key] = previous;
				throw;
			}
		}
		return ValueTask.CompletedTask;
	}

	private JsonObject Load()
	{
		if (!File.Exists(_path)) return [];
		try
		{
			JsonNode? node = JsonNode.Parse(File.ReadAllText(_path));
			return node as JsonObject ?? throw new JsonException("根节点不是对象");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new PluginException(PluginErrorCodes.StorageFailed, "插件存储无法读取", exception);
		}
	}

	private void Save()
	{
		string temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temporary, _values.ToJsonString(JsonOptions));
			File.Move(temporary, _path, true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new PluginException(PluginErrorCodes.StorageFailed, "插件存储无法写入", exception);
		}
		finally
		{
			try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } // NOSONAR: 临时文件清理失败不应覆盖已完成的导出结果。
		}
	}

	private static void ValidateKey(string key)
	{
		if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
			throw new PluginException(PluginErrorCodes.StorageFailed, "插件存储键无效");
	}

	/// <summary>拒绝插件自己的存储目录是链接，但允许宿主运行于系统级 symlink 祖先之下。</summary>
	private static void EnsureNoReparsePoints(string path) =>
		PluginPathSafety.EnsureNoReparsePoint(path, PluginErrorCodes.StorageFailed, "插件存储目录包含符号链接");
}

/// <summary>插件包公开资源读取器。</summary>
internal sealed class PluginAssetProvider : IPluginAssets
{
	private readonly string _root;
	private readonly Func<string, Uri>? _uriFactory;

	public PluginAssetProvider(string root, Func<string, Uri>? uriFactory = null)
	{
		_root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		_uriFactory = uriFactory;
	}

	public Stream OpenRead(string relativePath)
	{
		string path = Resolve(relativePath) ?? throw new PluginException(PluginErrorCodes.AssetDenied, "插件资源路径不允许访问");
		return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
	}

	public Uri GetUri(string relativePath)
	{
		string path = Resolve(relativePath) ?? throw new PluginException(PluginErrorCodes.AssetDenied, "插件资源路径不允许访问");
		return _uriFactory?.Invoke(relativePath) ?? new Uri(path, UriKind.Absolute);
	}

	internal static bool IsPublicAsset(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.StartsWith('\\') || path.Contains('\\') || path.Contains(':', StringComparison.Ordinal) || path.Any(char.IsControl)) return false;
		string[] parts = path.Split('/');
		if (parts.Any(part => part.Length == 0 || part is "." or "..")) return false;
		return path.Equals("icon.png", StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("web/", StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("locales/", StringComparison.OrdinalIgnoreCase);
	}

	private string? Resolve(string relativePath)
	{
		if (!IsPublicAsset(relativePath)) return null;
		string fullPath;
		try { fullPath = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar))); }
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return null; }
		if (!IsWithin(_root, fullPath) || !File.Exists(fullPath)) return null;
		try
		{
			if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0) return null;
		}
		catch (FileNotFoundException) { return null; }
		catch (DirectoryNotFoundException) { return null; }
		catch (UnauthorizedAccessException) { return null; }
		catch (IOException) { return null; }
		try
		{
			EnsureNoReparsePoints(_root, fullPath);
			return fullPath;
		}
		catch (PluginException) { return null; }
		catch (IOException) { return null; }
		catch (UnauthorizedAccessException) { return null; }
	}

	private static bool IsWithin(string root, string path)
	{
		string separatorRoot = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
		return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(separatorRoot, StringComparison.OrdinalIgnoreCase);
	}

	private static void EnsureNoReparsePoints(string root, string path)
	{
		string relative = Path.GetRelativePath(root, path);
		string current = root;
		foreach (string segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
		{
			if (segment is "." or "..") throw new PluginException(PluginErrorCodes.AssetDenied, "插件资源路径无效");
			current = Path.Combine(current, segment);
			if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new PluginException(PluginErrorCodes.AssetDenied, "插件资源路径包含符号链接");
		}
	}
}

internal sealed class PluginContext : IPluginContext
{
	internal required PluginCapabilityRegistry CapabilityRegistry { get; init; }
	internal required PluginContributionRegistry ContributionRegistry { get; init; }
	internal required CancellationTokenSource StoppingSource { get; init; }

	public required PluginDescriptor Plugin { get; init; }
	public required IPluginLogger Logger { get; init; }
	public required IPluginStorage Storage { get; init; }
	public required IPluginAssets Assets { get; init; }
	public required IContributionRegistry Contributions { get; init; }
	public required IPluginCapabilities Capabilities { get; init; }
	public CancellationToken StoppingToken => StoppingSource.Token;

	internal void Revoke()
	{
		try { StoppingSource.Cancel(throwOnFirstException: false); } catch { } // NOSONAR: 撤销阶段必须继续释放其余插件资源。
		try { ContributionRegistry.RevokeAll(); } catch { } // NOSONAR: 撤销阶段必须继续释放其余插件资源。
		try { CapabilityRegistry.Dispose(); } catch { } // NOSONAR: 撤销阶段必须继续释放其余插件资源。
	}

	internal void Dispose()
	{
		StoppingSource.Dispose();
	}
}
