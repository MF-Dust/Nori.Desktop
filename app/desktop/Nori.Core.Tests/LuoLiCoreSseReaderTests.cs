using Nori.Core.Chat;
using Nori.Core.Chat.LuoLiCore;

namespace Nori.Core.Tests;

/// <summary>
/// LuoLiCore SDK 流的解析与顺序不变量。
///
/// 这条流的三条约束来自对端契约：`queued`（如果出现）先于任何 `delta`；`delta` 零至多次；
/// 末尾恰好 `done` 或 `error` 之一。违反时必须抛而不是静默吞 —— 一个半开的流在界面上
/// 表现为「她说到一半不动了」，没有任何错误可查，那是最贵的一类故障。
/// </summary>
public sealed class LuoLiCoreSseReaderTests
{
	private static async Task<List<LuoLiCoreStreamEvent>> ReadAsync(string sse)
	{
		using StringReader reader = new(sse);
		List<LuoLiCoreStreamEvent> items = [];
		await foreach (LuoLiCoreStreamEvent item in LuoLiCoreSseReader.ReadAsync(reader))
		{
			items.Add(item);
		}

		return items;
	}

	private static Task<ChatException> ReadFailureAsync(string sse) =>
		Assert.ThrowsAsync<ChatException>(() => ReadAsync(sse));

	[Fact]
	public async Task 正常一轮_排队在前_增量在中_done收尾()
	{
		List<LuoLiCoreStreamEvent> items = await ReadAsync(
			"event: queued\ndata: {}\n\n"
			+ "event: delta\ndata: {\"text\":\"你\"}\n\n"
			+ "event: delta\ndata: {\"text\":\"好\"}\n\n"
			+ "event: done\ndata: {\"text\":\"你好\",\"usage\":{\"inputTokens\":12,\"outputTokens\":3}}\n\n");

		Assert.Equal(
			[LuoLiCoreStreamEventKind.Queued, LuoLiCoreStreamEventKind.Delta, LuoLiCoreStreamEventKind.Delta, LuoLiCoreStreamEventKind.Done],
			items.Select(i => i.Kind));
		Assert.Equal("你好", string.Concat(items.Where(i => i.Kind == LuoLiCoreStreamEventKind.Delta).Select(i => i.Text)));

		LuoLiCoreStreamEvent done = items[^1];
		Assert.Equal("你好", done.Text);
		Assert.Equal(12, done.InputTokens);
		Assert.Equal(3, done.OutputTokens);
	}

	[Fact]
	public async Task 没有排队事件也合法()
	{
		List<LuoLiCoreStreamEvent> items = await ReadAsync(
			"event: delta\ndata: {\"text\":\"嗯\"}\n\n"
			+ "event: done\ndata: {\"text\":\"嗯\"}\n\n");

		Assert.Equal([LuoLiCoreStreamEventKind.Delta, LuoLiCoreStreamEventKind.Done], items.Select(i => i.Kind));
	}

	[Fact]
	public async Task 零个增量也合法()
	{
		List<LuoLiCoreStreamEvent> items = await ReadAsync("event: done\ndata: {\"text\":\"\"}\n\n");

		LuoLiCoreStreamEvent only = Assert.Single(items);
		Assert.Equal(LuoLiCoreStreamEventKind.Done, only.Kind);
	}

	[Fact]
	public async Task 排队事件排在增量之后就是协议被破坏()
	{
		ChatException failure = await ReadFailureAsync(
			"event: delta\ndata: {\"text\":\"半\"}\n\n"
			+ "event: queued\ndata: {}\n\n"
			+ "event: done\ndata: {\"text\":\"半\"}\n\n");

		Assert.Contains("queued", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 终结事件之后还有数据就是协议被破坏()
	{
		ChatException failure = await ReadFailureAsync(
			"event: done\ndata: {\"text\":\"完\"}\n\n"
			+ "event: delta\ndata: {\"text\":\"又来\"}\n\n");

		Assert.Contains("终结事件", failure.Message, StringComparison.Ordinal);
	}

	/// <summary>连接被中途掐断。这一条是「无心跳帧 + 反代空闲超时」最常见的落地形态。</summary>
	[Fact]
	public async Task 流没有以done或error结束就是被中断()
	{
		ChatException failure = await ReadFailureAsync(
			"event: delta\ndata: {\"text\":\"说到一半\"}\n\n");

		Assert.Contains("中断", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task 错误事件终结流并摊平成字段()
	{
		List<LuoLiCoreStreamEvent> items = await ReadAsync(
			"event: queued\ndata: {}\n\n"
			+ "event: error\ndata: {\"code\":\"turn_queue_full\",\"message\":\"排队已满\",\"retryable\":true}\n\n");

		LuoLiCoreStreamEvent failure = items[^1];
		Assert.Equal(LuoLiCoreStreamEventKind.Error, failure.Kind);
		Assert.Equal("turn_queue_full", failure.ErrorCode);
		Assert.Equal("排队已满", failure.ErrorMessage);
		Assert.True(failure.Retryable);

		// 队列满在流式端点上不是 HTTP 429，调用方只能靠这个判据退避。
		Assert.True(failure.IsQueueFull);
	}

	[Fact]
	public async Task 未知事件名忽略而不是抛()
	{
		List<LuoLiCoreStreamEvent> items = await ReadAsync(
			"event: heartbeat\ndata: {}\n\n"
			+ "event: done\ndata: {\"text\":\"在\"}\n\n");

		LuoLiCoreStreamEvent only = Assert.Single(items);
		Assert.Equal(LuoLiCoreStreamEventKind.Done, only.Kind);
	}

	[Fact]
	public async Task 注释帧忽略()
	{
		List<LuoLiCoreStreamEvent> items = await ReadAsync(
			": 反代插的保活注释\n\n"
			+ "event: done\ndata: {\"text\":\"在\"}\n\n");

		Assert.Single(items);
	}

	[Fact]
	public async Task 半个帧不吐出去()
	{
		// data 缺失：这不是一个事件，读到流末尾应当按「被中断」处理。
		await ReadFailureAsync("event: delta\n\n");
	}

	[Fact]
	public async Task 载荷不是合法json就报解析失败而不是当成空文本()
	{
		ChatException failure = await ReadFailureAsync("event: delta\ndata: {不是json}\n\n");

		Assert.Contains("解析失败", failure.Message, StringComparison.Ordinal);
	}

	/// <summary>SSE 规范里 `data:` 之后的单个空格属于分隔符，不是正文。</summary>
	[Fact]
	public async Task 字段冒号后的单个空格被剥掉()
	{
		List<LuoLiCoreStreamEvent> items = await ReadAsync("event: done\ndata: {\"text\":\" 前导空格保留\"}\n\n");

		Assert.Equal(" 前导空格保留", items[^1].Text);
	}
}
