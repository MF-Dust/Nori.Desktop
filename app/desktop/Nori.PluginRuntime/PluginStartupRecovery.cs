using System.Text.Json;

namespace Nori.PluginRuntime;

/// <summary>记录插件启动失败次数，避免坏插件在每次启动时重复执行。</summary>
internal sealed class PluginStartupRecoveryStore
{
	private readonly object _gate = new();
	private readonly string _path;
	private readonly Dictionary<string, StartupState> _states;

	public PluginStartupRecoveryStore(string path)
	{
		_path = path;
		_states = Load(path);
	}

	public bool IsDisabled(string pluginId)
	{
		lock (_gate) return _states.TryGetValue(pluginId, out StartupState? state) && state.Disabled;
	}

	public bool RecordFailure(string pluginId)
	{
		lock (_gate)
		{
			_states.TryGetValue(pluginId, out StartupState? previous);
			int failures = (previous?.Failures ?? 0) + 1;
			bool disabled = failures >= 2;
			_states[pluginId] = new StartupState(failures, disabled);
			Persist();
			return disabled;
		}
	}

	public void Clear(string pluginId)
	{
		lock (_gate)
		{
			if (!_states.Remove(pluginId)) return;
			Persist();
		}
	}

	private void Persist()
	{
		string temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temporary, JsonSerializer.Serialize(_states));
			File.Move(temporary, _path, true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 恢复记录失败不能阻断宿主；本次进程仍会隔离插件故障。
		}
		finally
		{
			try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } // NOSONAR: 临时恢复文件清理失败不应覆盖恢复结果。
		}
	}

	private static Dictionary<string, StartupState> Load(string path)
	{
		if (!File.Exists(path)) return new(StringComparer.Ordinal);
		try
		{
			Dictionary<string, StartupState>? states = JsonSerializer.Deserialize<Dictionary<string, StartupState>>(File.ReadAllText(path));
			return states is null ? new(StringComparer.Ordinal) : new(states, StringComparer.Ordinal);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return new(StringComparer.Ordinal);
		}
	}

	private sealed record StartupState(int Failures = 0, bool Disabled = false);
}
