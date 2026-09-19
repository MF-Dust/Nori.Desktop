using System.Text.Json;
namespace Nori.PluginRuntime;

/// <summary>持久化用户对插件的启用意图与需要重启后完成的卸载请求。</summary>
internal sealed class PluginStateStore
{
	// 该键不可能通过插件 ID 正则，因此不会与真实插件冲突。
	// 一旦状态文件损坏，保留该哨兵使“无记录插件”在后续重启中继续 fail-closed，
	// 直到用户显式重新启用对应插件并写入独立状态。
	private const string FailClosedSentinel = "__fail_closed__";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	private readonly object _gate = new();
	private readonly string _path;
	private Dictionary<string, PluginRuntimeState> _states;

	public PluginStateStore(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		_path = Path.GetFullPath(path);
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		_states = Load(_path);
	}

	/// <summary>
	/// 正常状态文件中，既有插件没有记录时保持 1.0 兼容语义，默认视为启用。
	/// 如果状态文件曾损坏，则未知插件默认禁用，避免用户明确禁用过的代码被静默重新执行。
	/// </summary>
	public bool IsEnabled(string pluginId)
	{
		ValidateId(pluginId);
		lock (_gate)
		{
			if (_states.TryGetValue(pluginId, out PluginRuntimeState? state)) return state.Enabled;
			return !_states.ContainsKey(FailClosedSentinel);
		}
	}

	public void SetEnabled(string pluginId, bool enabled)
	{
		ValidateId(pluginId);
		lock (_gate)
		{
			_states.TryGetValue(pluginId, out PluginRuntimeState? previous);
			_states[pluginId] = (previous ?? new PluginRuntimeState()) with { Enabled = enabled };
			Persist();
		}
	}

	public void SetPendingUninstall(string pluginId, bool deleteData)
	{
		ValidateId(pluginId);
		lock (_gate)
		{
			_states.TryGetValue(pluginId, out PluginRuntimeState? previous);
			_states[pluginId] = (previous ?? new PluginRuntimeState()) with
			{
				Enabled = false,
				PendingUninstall = true,
				DeleteData = deleteData,
			};
			Persist();
		}
	}

	public bool TryGetPendingUninstall(string pluginId, out bool deleteData)
	{
		ValidateId(pluginId);
		lock (_gate)
		{
			if (_states.TryGetValue(pluginId, out PluginRuntimeState? state) && state.PendingUninstall)
			{
				deleteData = state.DeleteData;
				return true;
			}
		}
		deleteData = false;
		return false;
	}

	public IReadOnlyList<(string PluginId, bool DeleteData)> PendingUninstalls()
	{
		lock (_gate)
		{
			return _states
				.Where(pair => pair.Value.PendingUninstall && PluginManifestReader.IsValidPluginId(pair.Key))
				.OrderBy(pair => pair.Key, StringComparer.Ordinal)
				.Select(pair => (pair.Key, pair.Value.DeleteData))
				.ToArray();
		}
	}

	public void Remove(string pluginId)
	{
		ValidateId(pluginId);
		lock (_gate)
		{
			if (!_states.Remove(pluginId)) return;
			Persist();
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "插件状态临时文件清理失败不能覆盖已完成的持久化结果。")]
	private void Persist()
	{
		string temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temporary, JsonSerializer.Serialize(_states, JsonOptions));
			File.Move(temporary, _path, true);
		}
		finally
		{
			try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
		}
	}

	private static Dictionary<string, PluginRuntimeState> Load(string path)
	{
		if (!File.Exists(path)) return new(StringComparer.Ordinal);
		try
		{
			Dictionary<string, PluginRuntimeState>? states = JsonSerializer.Deserialize<Dictionary<string, PluginRuntimeState>>(File.ReadAllText(path), JsonOptions);
			if (states is null || states.Values.Any(state => state is null)) return FailClosedState();
			return new(states, StringComparer.Ordinal);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			// 状态损坏时绝不能退化为“所有未知插件默认启用”。保留一个不可伪造的
			// 哨兵；后续任意成功写入都会把它持久化，从而跨重启维持 fail-closed。
			return FailClosedState();
		}
	}

	private static Dictionary<string, PluginRuntimeState> FailClosedState() => new(StringComparer.Ordinal)
	{
		[FailClosedSentinel] = new PluginRuntimeState { Enabled = false },
	};

	private static void ValidateId(string pluginId)
	{
		if (!PluginManifestReader.IsValidPluginId(pluginId))
			throw new PluginException(PluginErrorCodes.InvalidManifest, "插件 ID 无效");
	}

	private sealed record PluginRuntimeState
	{
		public bool Enabled { get; init; } = true;
		public bool PendingUninstall { get; init; }
		public bool DeleteData { get; init; }
	}
}
