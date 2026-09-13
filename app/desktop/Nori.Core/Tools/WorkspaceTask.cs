using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;

namespace Nori.Core.Tools;

/// <summary>
/// 一条具名任务：用户配好的命令，模型只能按名触发。
///
/// 这是命令执行的核心约束 —— **模型不参与命令行的构造**。让模型自由拼命令时，一次提示
/// 注入就能得到任意执行，而用户在确认对话框里要逐字审一条命令行才能判断，实际上做不到。
/// 按名触发时，注入最多只能触发用户已经配置并授权过的任务。
/// </summary>
public sealed record WorkspaceTask
{
	/// <summary>任务名。模型按这个名字调用。</summary>
	public required string Name { get; init; }

	/// <summary>完整命令行，在工作目录下执行。</summary>
	public required string Command { get; init; }
}

/// <summary>具名任务清单的读写与校验。</summary>
public static class WorkspaceTaskList
{
	/// <summary>任务条数上限。每条都会进模型的工具描述，条数过多会挤占上下文。</summary>
	public const int MaxTasks = 12;

	/// <summary>任务名长度上限。</summary>
	public const int MaxNameLength = 32;

	/// <summary>命令行长度上限。</summary>
	public const int MaxCommandLength = 500;

	/// <summary>
	/// 从配置值读出任务清单，容错。
	///
	/// 非法条目跳过而不是整体失败：配置损坏时应当退化成「这条任务不可用」，而不是让整个
	/// 应用起不来。写入侧由 <see cref="Validate"/> 严格把关，损坏只可能来自外部改库。
	/// </summary>
	public static IReadOnlyList<WorkspaceTask> Read(string? json)
	{
		if (string.IsNullOrWhiteSpace(json)) return [];

		try
		{
			return Read(JsonNode.Parse(json) as JsonArray);
		}
		catch (JsonException)
		{
			return [];
		}
	}

	/// <summary>
	/// 从配置值读出任务清单。
	///
	/// **必须按 <see cref="ConfigValue.Json"/> 存取，不能用 <see cref="ConfigValue.Text"/>。**
	/// 配置层会把内容形如 JSON 容器的文本识别成 Json 值，而 <c>AsStringOr</c> 没有 Json 分支，
	/// 于是 <c>GetStringOr</c> 读回来的是 fallback —— 表现为保存成功但配置像是没写进去。
	/// 为兼容可能存在的旧 Text 值，这里两种都接。
	/// </summary>
	public static IReadOnlyList<WorkspaceTask> Read(ConfigValue? value) => value switch
	{
		ConfigValue.Json {Value: JsonArray array} => Read(array),
		ConfigValue.Text text => Read(text.Value),
		_ => [],
	};

	/// <summary>从已解析的数组读出任务清单，容错。</summary>
	public static IReadOnlyList<WorkspaceTask> Read(JsonArray? array)
	{
		if (array is null) return [];

		List<WorkspaceTask> tasks = [];
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		foreach (JsonNode? node in array)
		{
			if (tasks.Count >= MaxTasks) break;
			if (node is not JsonObject entry) continue;

			string name = (entry["name"]?.GetValue<string>() ?? "").Trim();
			string command = (entry["command"]?.GetValue<string>() ?? "").Trim();
			if (name.Length == 0 || command.Length == 0) continue;
			if (name.Length > MaxNameLength || command.Length > MaxCommandLength) continue;
			if (!seen.Add(name)) continue;

			tasks.Add(new WorkspaceTask { Name = name, Command = command });
		}

		return tasks;
	}

	/// <summary>写入前的严格校验。任何一条不合法都整体拒绝，并说明是哪一条。</summary>
	public static IReadOnlyList<WorkspaceTask> Validate(IEnumerable<WorkspaceTask> tasks)
	{
		ArgumentNullException.ThrowIfNull(tasks);
		List<WorkspaceTask> result = [];
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

		foreach (WorkspaceTask task in tasks)
		{
			string name = (task.Name ?? "").Trim();
			string command = (task.Command ?? "").Trim();

			if (name.Length == 0) throw new InvalidOperationException("任务名不能为空");
			if (command.Length == 0) throw new InvalidOperationException($"任务「{name}」的命令不能为空");
			if (name.Length > MaxNameLength)
			{
				throw new InvalidOperationException($"任务名超过 {MaxNameLength} 个字符: {name}");
			}

			if (command.Length > MaxCommandLength)
			{
				throw new InvalidOperationException($"任务「{name}」的命令超过 {MaxCommandLength} 个字符");
			}

			if (!seen.Add(name)) throw new InvalidOperationException($"任务名重复: {name}");
			if (result.Count >= MaxTasks) throw new InvalidOperationException($"任务最多 {MaxTasks} 条");

			result.Add(new WorkspaceTask { Name = name, Command = command });
		}

		return result;
	}

	/// <summary>序列化成配置值。存的是 Json 而不是 Text，原因见 <see cref="Read(ConfigValue?)"/>。</summary>
	public static ConfigValue Write(IEnumerable<WorkspaceTask> tasks)
	{
		ArgumentNullException.ThrowIfNull(tasks);
		JsonArray array = [];
		foreach (WorkspaceTask task in tasks)
		{
			array.Add(new JsonObject { ["name"] = task.Name, ["command"] = task.Command });
		}

		return new ConfigValue.Json(array);
	}
}
