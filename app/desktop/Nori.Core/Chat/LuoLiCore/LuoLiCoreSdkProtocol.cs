using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nori.Core.Chat.LuoLiCore;

/// <summary>
/// LuoLiCore <c>/sdk/v1</c> 的线上形状。
///
/// 契约唯一来源是对端的 <c>packages/sdk-client/src/schema.ts</c>（zod），这里只镜像本
/// 集成用到的那几个形状，不复刻整套十端点。字段名与 zod 逐一对齐；对端新增字段时
/// 反序列化会忽略未知键，不会因为对端先行升级而整条失败。
/// </summary>
internal static class LuoLiCoreSdkProtocol
{
	/// <summary>SSE 事件名。顺序不变量见 <see cref="LuoLiCoreSseReader"/>。</summary>
	internal const string EventQueued = "queued";
	internal const string EventDelta = "delta";
	internal const string EventDone = "done";
	internal const string EventError = "error";

	/// <summary>
	/// 工具活动事件。**只有请求里显式要了才会收到**（见 <see cref="SdkSendMessageInput"/>）。
	///
	/// 它不参与顺序不变量：可以出现在任意两个 delta 之间，也可以出现在一个没有任何 delta 的
	/// 轮次里，但仍然排在 done / error 之前。
	/// </summary>
	internal const string EventTool = "tool";

	/// <summary>请求 <c>tool</c> 事件时填进 <c>events</c> 的取值。</summary>
	internal const string OptInTool = "tool";

	/// <summary>
	/// 队列满在流式端点上不表现为 HTTP 429。
	///
	/// 响应头在配额判定通过、流开始建立时就锁定成了 200 + text/event-stream，之后再发现
	/// 队列满已经改不了状态码，只能以 SSE <c>error</c> 事件表达。对端文档把这一点记为架构
	/// 限制而非缺陷，并规定客户端固定按 5 秒退避。
	/// </summary>
	internal const string CodeTurnQueueFull = "turn_queue_full";

	/// <summary>队列满时的固定退避。对端 <c>@luoli/sdk-client</c> 内置同一个常量。</summary>
	internal static readonly TimeSpan QueueFullBackoff = TimeSpan.FromSeconds(5);

	/// <summary>
	/// 读超时必须给到对端的轮次超时之上。
	///
	/// 流在轮次执行期间**没有任何保活帧**（对端文档：「无心跳帧」）。按 HttpClient 默认的
	/// 100 秒超时去读，一个跑满 10 分钟的轮次会在客户端这边被当成传输层中断掐掉，而且读到的
	/// 不是 <c>error</c> 事件，是一次没有原因的断流。对端默认 <c>turnTimeoutMs = 600s</c>。
	/// </summary>
	internal static readonly TimeSpan StreamReadTimeout = TimeSpan.FromSeconds(660);

	internal static readonly JsonSerializerOptions Json = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};
}

/// <summary>建会话入参。三项都可选；<c>outputFormat</c> 建会话后不可改。</summary>
internal sealed record SdkCreateSessionInput(
	[property: JsonPropertyName("modelAlias")] string? ModelAlias,
	[property: JsonPropertyName("outputFormat")] string? OutputFormat,
	[property: JsonPropertyName("label")] string? Label);

/// <summary>会话。</summary>
internal sealed record SdkSession(
	[property: JsonPropertyName("id")] string Id,
	[property: JsonPropertyName("label")] string? Label,
	[property: JsonPropertyName("modelAlias")] string? ModelAlias,
	[property: JsonPropertyName("outputFormat")] string? OutputFormat,
	[property: JsonPropertyName("createdAt")] long CreatedAt,
	[property: JsonPropertyName("deletedAt")] long? DeletedAt);

/// <summary>重置会话上下文的结果。<c>commitId</c> 是那条重置提交。</summary>
internal sealed record SdkResetResult(
	[property: JsonPropertyName("ok")] bool Ok,
	[property: JsonPropertyName("commitId")] string? CommitId);

/// <summary>
/// 发消息入参。<c>endUserId</c> 落库前由服务端加 <c>&lt;sourceId&gt;:</c> 前缀。
///
/// <c>events</c> 是加法式的开关：不填就只收到 delta / done / error / queued 四种，与对端
/// 未支持工具事件时的行为一致。因此这个字段发给旧版服务端也是安全的 —— 它会被 zod 的
/// 宽松解析忽略（多余键不报错），行为退化成没有工具事件。
/// </summary>
internal sealed record SdkSendMessageInput(
	[property: JsonPropertyName("endUserId")] string EndUserId,
	[property: JsonPropertyName("text")] string Text,
	[property: JsonPropertyName("modelAlias")] string? ModelAlias,
	[property: JsonPropertyName("events")] IReadOnlyList<string>? Events = null);

/// <summary>SSE <c>tool</c> 的载荷。只有名字：入参与结果不出网。</summary>
internal sealed record SdkStreamTool(
	[property: JsonPropertyName("name")] string Name);

/// <summary>
/// <c>GET /sdk/v1/tools</c> 的一行。
///
/// <c>approval</c> 是 never / conditional / always 三档。SDK 会话没有人工审批通道，需要
/// 审批的调用会直接被拒 —— 这一项让界面能提前把它标出来，而不是等一次失败。
/// </summary>
internal sealed record SdkToolInfo(
	[property: JsonPropertyName("name")] string Name,
	[property: JsonPropertyName("description")] string? Description,
	[property: JsonPropertyName("approval")] string? Approval);

internal sealed record SdkToolListResponse(
	[property: JsonPropertyName("tools")] IReadOnlyList<SdkToolInfo>? Tools);

/// <summary>用量。</summary>
internal sealed record SdkUsage(
	[property: JsonPropertyName("inputTokens")] int? InputTokens,
	[property: JsonPropertyName("outputTokens")] int? OutputTokens);

/// <summary>
/// 统一错误信封。
///
/// 十个错误码共用这一个形状，HTTP 错误体与 SSE <c>error</c> 事件同形 —— 所以两条路径
/// 可以用同一个类型解析，不必各写一份。
/// </summary>
internal sealed record SdkError(
	[property: JsonPropertyName("code")] string Code,
	[property: JsonPropertyName("message")] string? Message,
	[property: JsonPropertyName("retryable")] bool Retryable);

/// <summary>SSE <c>delta</c> 的载荷。</summary>
internal sealed record SdkDelta(
	[property: JsonPropertyName("text")] string Text);

/// <summary>
/// SSE <c>done</c> 的载荷。
///
/// **不含 turnId** —— 与非流式端点的响应不同，后者含。需要 turnId 时要经
/// <c>GET /sdk/v1/sessions/:id/commits</c> 反查最近一条 <c>kind='turn'</c> 的提交。
/// </summary>
internal sealed record SdkDone(
	[property: JsonPropertyName("text")] string Text,
	[property: JsonPropertyName("usage")] SdkUsage? Usage);
