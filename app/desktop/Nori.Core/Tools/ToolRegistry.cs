using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Agent;

namespace Nori.Core.Tools;

/// <summary>
/// 工具定义与执行体
/// </summary>
public sealed class RegisteredTool
{
	/// <summary>工具名 (注入 Prompt 与协议调用的标识)</summary>
	public required string Name { get; init; }

	/// <summary>面向模型的中文描述</summary>
	public required string Description { get; init; }

	/// <summary>参数 JSON Schema</summary>
	public required JsonObject Parameters { get; init; }

	/// <summary>权限级别: safe / confirm / dangerous</summary>
	public required string PermissionLevel { get; init; }

	/// <summary>分类: builtin / mcp / custom</summary>
	public string Category { get; init; } = "builtin";

	/// <summary>是否启用</summary>
	public bool Enabled { get; set; } = true;

	/// <summary>执行体: 参数非法时抛异常, 异常消息即工具错误</summary>
	public required Func<JsonNode?, ToolContext, Task<object?>> Execute { get; init; }
}

/// <summary>
/// 工具执行结果
/// </summary>
public sealed record ToolResult(object? Result, string? Error)
{
	public bool IsSuccess => Error is null;
}

/// <summary>
/// 工具注册表与管理器
///
/// 负责注册/注销/启停与带权限校验的统一执行入口:
/// safe 工具直接运行; confirm/dangerous 工具必须经逐调用授权,
/// 授权回调缺失、会话取消或用户拒绝时一律 fail-closed 返回可序列化错误。
/// </summary>
public sealed class ToolRegistry
{
	private static readonly JsonSerializerOptions JsonOptions = new() {PropertyNamingPolicy = JsonNamingPolicy.CamelCase};

	private readonly Lock _gate = new();
	private Dictionary<string, RegisteredTool> _tools = [];
	private readonly HashSet<string> _disabled = [];

	/// <summary>注册一个工具 (重名覆盖)</summary>
	public void Register(RegisteredTool tool)
	{
		ValidateTool(tool);
		lock (_gate)
		{
			if (_disabled.Contains(tool.Name)) tool.Enabled = false;
			_tools[tool.Name] = tool;
		}
	}

	/// <summary>
	/// 原子替换指定分类的全部工具。新集合校验或构建失败时保留旧集合。
	/// </summary>
	public void ReplaceCategory(string category, IEnumerable<RegisteredTool> tools)
	{
		if (string.IsNullOrWhiteSpace(category)) throw new InvalidOperationException("工具分类不能为空");
		ArgumentNullException.ThrowIfNull(tools);

		List<RegisteredTool> replacements = tools.ToList();
		HashSet<string> replacementNames = new(StringComparer.Ordinal);
		foreach (RegisteredTool tool in replacements)
		{
			ValidateTool(tool);
			if (!string.Equals(tool.Category, category, StringComparison.Ordinal))
			{
				throw new InvalidOperationException($"工具 {tool.Name} 不属于分类 {category}");
			}
			if (!replacementNames.Add(tool.Name))
			{
				throw new InvalidOperationException($"工具集合包含重复名称: {tool.Name}");
			}
		}

		lock (_gate)
		{
			Dictionary<string, RegisteredTool> next = _tools
				.Where(pair => !string.Equals(pair.Value.Category, category, StringComparison.Ordinal))
				.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
			foreach (RegisteredTool tool in replacements)
			{
				if (next.ContainsKey(tool.Name))
				{
					throw new InvalidOperationException($"工具名称与其他分类冲突: {tool.Name}");
				}
				if (_disabled.Contains(tool.Name)) tool.Enabled = false;
				next.Add(tool.Name, tool);
			}
			_tools = next;
		}
	}

	/// <summary>注销工具</summary>
	public void Unregister(string name)
	{
		lock (_gate) _tools.Remove(name);
	}

	/// <summary>获取指定工具</summary>
	public RegisteredTool? Get(string name)
	{
		lock (_gate) return _tools.GetValueOrDefault(name);
	}

	/// <summary>获取全部工具列表</summary>
	public IReadOnlyList<RegisteredTool> List()
	{
		lock (_gate) return _tools.Values.ToList();
	}

	/// <summary>获取所有当前启用的工具列表</summary>
	public IReadOnlyList<RegisteredTool> ListEnabled()
	{
		lock (_gate)
		{
			return _tools.Values.Where(tool => tool.Enabled && !_disabled.Contains(tool.Name)).ToList();
		}
	}

	/// <summary>设置工具启用状态</summary>
	public bool SetEnabled(string name, bool enabled)
	{
		lock (_gate)
		{
			if (enabled) _disabled.Remove(name);
			if (!_tools.TryGetValue(name, out RegisteredTool? tool)) return false;
			if (!enabled) _disabled.Add(name);
			tool.Enabled = enabled;
			return true;
		}
	}

	/// <summary>导出禁用清单 (宿主负责持久化)</summary>
	public IReadOnlyList<string> DisabledNames()
	{
		lock (_gate) return _disabled.ToList();
	}

	/// <summary>恢复禁用清单 (启动时从配置回放)</summary>
	public void RestoreDisabled(IEnumerable<string> names)
	{
		ArgumentNullException.ThrowIfNull(names);
		lock (_gate)
		{
			foreach (string name in names)
			{
				_disabled.Add(name);
				if (_tools.TryGetValue(name, out RegisteredTool? tool)) tool.Enabled = false;
			}
		}
	}

	private static void ValidateTool(RegisteredTool tool)
	{
		ArgumentNullException.ThrowIfNull(tool);
		if (string.IsNullOrWhiteSpace(tool.Name) || tool.Name.Length > ToolLimits.MaxNameCharacters)
		{
			throw new InvalidOperationException($"工具名称不能为空且不能超过 {ToolLimits.MaxNameCharacters} 个字符");
		}
		if (tool.Execute is null) throw new InvalidOperationException($"工具 {tool.Name} 缺少执行体");
	}

	private bool IsDisabled(string name)
	{
		lock (_gate) return _disabled.Contains(name);
	}

	/// <summary>
	/// 生成注入 Prompt 的可用工具清单文本 (仅包含当前启用的工具)
	/// </summary>
	public string BuildToolsPrompt()
	{
		List<object> list = [];
		foreach (RegisteredTool tool in ListEnabled())
		{
			object candidate = new
			{
				name = ToolLimits.CapText(tool.Name, ToolLimits.MaxNameCharacters),
				description = ToolLimits.CapText(tool.Description, ToolLimits.MaxDescriptionCharacters),
				parameters = ToolLimits.CapSchema(tool.Parameters),
			};
			List<object> next = [.. list, candidate];
			string serialized = JsonSerializer.Serialize(next, JsonOptions);
			if (serialized.Length > ToolLimits.MaxToolsPromptCharacters)
			{
				// 工具清单按注册顺序确定性截断，不能让后注册的外部工具挤掉全部内置工具。
				if (list.Count == 0)
				{
					list.Add(new
					{
						name = ToolLimits.CapText(tool.Name, ToolLimits.MaxNameCharacters),
						description = "工具定义已达到安全长度上限。",
						parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
					});
				}
				break;
			}
			list.Add(candidate);
		}
		return JsonSerializer.Serialize(list, JsonOptions);
	}
	/// <summary>为已注册工具添加别名入口 (同名执行体, 独立描述)</summary>
	public void RegisterAlias(string sourceName, string aliasName, string description)
	{
		RegisteredTool? source = Get(sourceName) ?? throw new InvalidOperationException($"别名源工具不存在: {sourceName}");
		Register(new RegisteredTool
		{
			Name = aliasName,
			Description = description,
			Parameters = source.Parameters,
			PermissionLevel = source.PermissionLevel,
			Category = source.Category,
			Enabled = source.Enabled,
			Execute = source.Execute,
		});
	}

	/// <summary>
	/// 执行工具调用
	///
	/// safe 直接运行; confirm/dangerous 必须先经逐调用授权 (fail-closed),
	/// 授权等待期间会话取消同样拒绝执行。执行异常转为可序列化错误文本。
	/// </summary>
	public async Task<ToolResult> ExecuteAsync(string name, JsonNode? args, ToolContext? context = null)
	{
		ToolContext effectiveContext = context ?? new ToolContext();
		if (effectiveContext.CancellationToken.IsCancellationRequested)
		{
			return new ToolResult(null, $"会话已取消，工具 {name} 不再执行");
		}
		RegisteredTool? tool = Get(name);
		if (tool is null) return new ToolResult(null, $"未找到工具: {name}");
		if (!tool.Enabled || IsDisabled(name)) return new ToolResult(null, $"工具 {name} 已被禁用");
		if (tool.PermissionLevel is not ("safe" or "confirm" or "dangerous"))
		{
			return new ToolResult(null, $"工具 {name} 的权限级别无效，已拒绝执行");
		}
		if (ToolLimits.SerializedLength(args) > ToolLimits.MaxArgumentsCharacters)
		{
			return new ToolResult(null, $"工具 {name} 的参数超过安全长度上限 ({ToolLimits.MaxArgumentsCharacters} 字符)");
		}

		if (tool.PermissionLevel != "safe")
		{
			ToolApprovalHandler? approve = effectiveContext.Approve;
			if (approve is null)
			{
				return new ToolResult(null,
					$"工具 {name} 标记为 {(tool.PermissionLevel == "dangerous" ? "危险" : "需确认")}，但当前没有可用的用户授权通道，已拒绝执行");
			}

			bool approved;
			try
			{
				TimeSpan approvalTimeout = effectiveContext.ApprovalTimeout <= TimeSpan.Zero
					? TimeSpan.FromMinutes(2)
					: effectiveContext.ApprovalTimeout;
				DateTimeOffset deadlineUtc = DateTimeOffset.UtcNow.Add(approvalTimeout);
				if (effectiveContext.DeadlineUtc is { } outerDeadline && outerDeadline < deadlineUtc) deadlineUtc = outerDeadline;
				using CancellationTokenSource approvalLifetime = CancellationTokenSource.CreateLinkedTokenSource(effectiveContext.CancellationToken);
				TimeSpan remaining = deadlineUtc - DateTimeOffset.UtcNow;
				approvalLifetime.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
				Task<bool> approvalTask = approve(new ToolApprovalRequest
				{
					RequestId = $"approval-{Guid.NewGuid():N}",
					ToolName = name,
					Arguments = args,
					Description = ToolLimits.CapText(tool.Description, ToolLimits.MaxDescriptionCharacters),
					PermissionLevel = tool.PermissionLevel,
					Category = tool.Category,
					DeadlineUtc = deadlineUtc,
					CancellationToken = approvalLifetime.Token,
				});
				approved = await approvalTask.WaitAsync(approvalLifetime.Token);
			}
			catch
			{
				// 授权通道异常、超时或取消同样视为拒绝, 绝不默认放行。
				return new ToolResult(null, $"工具 {name} 的授权请求失败或已取消，已拒绝执行");
			}

			if (!approved) return new ToolResult(null, $"用户拒绝执行工具: {name}");
			if (effectiveContext.CancellationToken.IsCancellationRequested)
			{
				return new ToolResult(null, $"等待授权期间会话已取消，工具 {name} 不再执行");
			}
		}

		try
		{
			Task<object?> executeTask = tool.Execute(args, effectiveContext);
			object? result = effectiveContext.CancellationToken.CanBeCanceled
				? await executeTask.WaitAsync(effectiveContext.CancellationToken)
				: await executeTask;
			return new ToolResult(ToolLimits.CapResult(result), null);
		}
		catch (OperationCanceledException) when (effectiveContext.CancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			return new ToolResult(null, ToolLimits.CapError(exception.Message));
		}
	}
}
