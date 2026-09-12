using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Nori.Core.Chat.LuoLiCore;

/// <summary>一个已解析的 SSE 帧。</summary>
internal readonly record struct SseFrame(string Event, string Data);

/// <summary>
/// LuoLiCore <c>/sdk/v1/.../messages/stream</c> 的 SSE 读取。
///
/// 独立成一个纯函数层，是因为这条流的**顺序不变量**是有约束的，而约束只有拿字符串喂进来
/// 才测得动：
///
/// <list type="bullet">
/// <item><c>queued</c>（如果出现）先于任何 <c>delta</c>。它在 acquire() 之前、turnId 产生
/// 之前触发，排队可能长达数秒。</item>
/// <item><c>delta</c> 零至多次。</item>
/// <item>末尾**恰好** <c>done</c> 或 <c>error</c> 之一，二者互斥且终结这个流。</item>
/// </list>
///
/// 违反这几条说明对端或中间层出了问题，这里宁可抛也不静默吞掉 —— 一个半开的流在上层
/// 表现为「她开口说了一半就不动了」，没有任何错误可查。
/// </summary>
internal static class LuoLiCoreSseReader
{
	/// <summary>
	/// 按帧读。只认 <c>event:</c> 与 <c>data:</c> 两个字段，空行分帧。
	///
	/// 不合并多行 <c>data:</c>：对端每个事件的载荷都是单行 JSON，而按 SSE 规范做多行拼接
	/// 需要额外约定分隔符语义，这里用不到，写了反而多一处与对端不一致的可能。
	/// </summary>
	internal static async IAsyncEnumerable<SseFrame> ReadFramesAsync(
		TextReader reader,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		string? eventName = null;
		string? data = null;

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string? line = await reader.ReadLineAsync(cancellationToken);
			if (line is null)
			{
				// 流结束。攒了一半的帧不吐出去 —— 半个帧不是事件。
				yield break;
			}

			if (line.Length == 0)
			{
				if (eventName is not null && data is not null) yield return new SseFrame(eventName, data);
				eventName = null;
				data = null;
				continue;
			}

			// 注释帧（以 ':' 开头）按规范忽略。对端目前不发，但反代可能插入保活注释。
			if (line[0] == ':') continue;

			int colon = line.IndexOf(':');
			if (colon < 0) continue;

			string field = line[..colon];
			string value = line[(colon + 1)..];
			if (value.StartsWith(' ')) value = value[1..];

			if (field == "event") eventName = value;
			else if (field == "data") data = value;
		}
	}

	/// <summary>
	/// 把帧流翻译成语义事件，并在翻译过程中强制顺序不变量。
	///
	/// 返回的序列里，<c>done</c> / <c>error</c> 一定是最后一项；没有终结事件就走到流末尾的，
	/// 抛 <see cref="ChatException"/>：那是连接被掐断，不是一次正常的空回复。
	/// </summary>
	internal static async IAsyncEnumerable<LuoLiCoreStreamEvent> ReadAsync(
		TextReader reader,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		bool sawDelta = false;
		bool terminated = false;

		await foreach (SseFrame frame in ReadFramesAsync(reader, cancellationToken))
		{
			if (terminated)
			{
				// done/error 之后还有帧：对端规定 error 发出后立即 close()，done 同理终结流。
				throw new ChatException("SDK 流在终结事件之后仍有数据，协议被破坏");
			}

			switch (frame.Event)
			{
				case LuoLiCoreSdkProtocol.EventQueued:
					if (sawDelta) throw new ChatException("SDK 流的 queued 事件出现在 delta 之后，协议被破坏");
					yield return LuoLiCoreStreamEvent.Queued();
					break;

				case LuoLiCoreSdkProtocol.EventDelta:
				{
					SdkDelta? delta = Deserialize<SdkDelta>(frame.Data, "delta");
					sawDelta = true;
					yield return LuoLiCoreStreamEvent.Delta(delta?.Text ?? string.Empty);
					break;
				}

				case LuoLiCoreSdkProtocol.EventTool:
				{
					// 不参与顺序不变量：它可以夹在任意两个 delta 之间，也可以出现在没有 delta 的
					// 轮次里。因此这里既不检查 sawDelta，也不置 terminated。
					SdkStreamTool? tool = Deserialize<SdkStreamTool>(frame.Data, "tool");
					if (!string.IsNullOrWhiteSpace(tool?.Name))
						yield return LuoLiCoreStreamEvent.Tool(tool!.Name);
					break;
				}

				case LuoLiCoreSdkProtocol.EventDone:
				{
					SdkDone? done = Deserialize<SdkDone>(frame.Data, "done");
					terminated = true;
					yield return LuoLiCoreStreamEvent.Done(done?.Text ?? string.Empty, done?.Usage);
					break;
				}

				case LuoLiCoreSdkProtocol.EventError:
				{
					SdkError? error = Deserialize<SdkError>(frame.Data, "error");
					terminated = true;
					yield return LuoLiCoreStreamEvent.Failed(
						error ?? new SdkError("unknown", "SDK 返回了无法解析的错误", false));
					break;
				}

				default:
					// 未知事件名：忽略而不是抛。对端加新事件时不该把老客户端打死。
					break;
			}
		}

		if (!terminated) throw new ChatException("SDK 流未以 done 或 error 结束，连接被中断");
	}

	private static T? Deserialize<T>(string data, string what)
	{
		try
		{
			return JsonSerializer.Deserialize<T>(data, LuoLiCoreSdkProtocol.Json);
		}
		catch (JsonException exception)
		{
			throw new ChatException($"SDK {what} 事件解析失败: {exception.Message}", exception);
		}
	}
}

/// <summary>
/// 流式事件的判别联合。
///
/// 错误与用量在这里**摊平成普通字段**，不直接暴露线上 DTO —— 那几个类型是 `/sdk/v1` 的
/// 形状，对端改字段时不该波及本项目的公开签名。
/// </summary>
public readonly record struct LuoLiCoreStreamEvent(
	LuoLiCoreStreamEventKind Kind,
	string Text,
	int? InputTokens,
	int? OutputTokens,
	string? ErrorCode,
	string? ErrorMessage,
	bool Retryable)
{
	internal static LuoLiCoreStreamEvent Queued() =>
		new(LuoLiCoreStreamEventKind.Queued, string.Empty, null, null, null, null, false);

	internal static LuoLiCoreStreamEvent Delta(string text) =>
		new(LuoLiCoreStreamEventKind.Delta, text, null, null, null, null, false);

	/// <summary>工具名借 <see cref="Text"/> 承载，不为它单开一个字段。</summary>
	internal static LuoLiCoreStreamEvent Tool(string name) =>
		new(LuoLiCoreStreamEventKind.Tool, name, null, null, null, null, false);

	internal static LuoLiCoreStreamEvent Done(string text, SdkUsage? usage) =>
		new(LuoLiCoreStreamEventKind.Done, text, usage?.InputTokens, usage?.OutputTokens, null, null, false);

	internal static LuoLiCoreStreamEvent Failed(SdkError error) =>
		new(LuoLiCoreStreamEventKind.Error, string.Empty, null, null, error.Code, error.Message, error.Retryable);

	/// <summary>队列满。对端把它表达成 SSE 错误而非 HTTP 429，调用方据此固定退避 5 秒。</summary>
	public bool IsQueueFull => Kind == LuoLiCoreStreamEventKind.Error
		&& string.Equals(ErrorCode, LuoLiCoreSdkProtocol.CodeTurnQueueFull, StringComparison.Ordinal);
}

/// <summary>流式事件类型。</summary>
public enum LuoLiCoreStreamEventKind
{
	/// <summary>进入排队。至多一次，且一定在任何 Delta 之前。</summary>
	Queued,

	/// <summary>文本增量。零至多次。</summary>
	Delta,

	/// <summary>
	/// 对端开始执行一个工具，<see cref="LuoLiCoreStreamEvent.Text"/> 是工具名。
	///
	/// 只有请求里显式要了才会出现。不参与顺序不变量 —— 可以夹在任意两个 Delta 之间。
	/// </summary>
	Tool,

	/// <summary>正常结束。与 Error 互斥，二者之一必定是最后一项。</summary>
	Done,

	/// <summary>失败结束。与 Done 互斥。</summary>
	Error,
}
