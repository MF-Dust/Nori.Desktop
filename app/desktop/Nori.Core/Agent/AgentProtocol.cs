using System.Text.Json.Nodes;

namespace Nori.Core.Agent;

/// <summary>
/// Agent 协议条目基类
///
/// 对应前端 services/agent/protocol.ts 的联合类型。
/// </summary>
public abstract record AgentProtocolItem;

/// <summary>
/// 文本回复消息 (带情绪、表情、动作联动)
/// </summary>
public sealed record ProtocolMessage(
	string Text,
	string? Emotion,
	string? Expression,
	string? Action) : AgentProtocolItem;

/// <summary>
/// 工具调用请求
/// </summary>
public sealed record ProtocolToolCall(
	string Id,
	string Name,
	JsonNode? Arguments) : AgentProtocolItem;

/// <summary>
/// 系统与环境事件
/// </summary>
public sealed record ProtocolEvent(
	string Name,
	JsonNode? Payload) : AgentProtocolItem;

/// <summary>
/// Agent 运行状态
/// </summary>
public enum AgentRunState
{
	Idle,
	Thinking,
	Streaming,
	ToolExecuting,
	Speaking,
	Error,
}

/// <summary>
/// 工具授权请求 (逐次授权 UI 展示用)
/// </summary>
public sealed record ToolApprovalRequest
{
	/// <summary>授权请求唯一 ID, 前端回传决定时携带</summary>
	public required string RequestId { get; init; }

	/// <summary>工具名</summary>
	public required string ToolName { get; init; }

	/// <summary>工具参数</summary>
	public JsonNode? Arguments { get; init; }

	/// <summary>工具描述</summary>
	public string? Description { get; init; }

	/// <summary>权限级别: confirm / dangerous</summary>
	public required string PermissionLevel { get; init; }

	/// <summary>工具分类</summary>
	public string? Category { get; init; }

	/// <summary>本次工具实际等待上限；界面延期不能越过该时刻。</summary>
	public DateTimeOffset? DeadlineUtc { get; init; }

	/// <summary>授权已被上游放弃的信号，不向 UI 序列化。</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public CancellationToken CancellationToken { get; init; }
}
