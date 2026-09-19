using System.Diagnostics;
using System.Text.Json;
using Nori.Core.Logging;

namespace Nori.Core.Tests;

public sealed class StructuredLoggerTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nori-structured-" + Guid.NewGuid().ToString("N"));
	public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

	[Fact]
	public async Task 并发写入磁盘与内存保持相同序号且不泄露调用文件路径()
	{
		await using FileLogger logger = new(_root);
		Parallel.For(0, 300, index => logger.Write(LogSource.Backend, "info", $"事件 {index}"));
		Assert.True(await logger.FlushAsync(TimeSpan.FromSeconds(5)));
		LogEntry[] memory = logger.RecentLogs().ToArray();
		Assert.Equal(Enumerable.Range(1, 300).Select(value => (long)value), memory.Select(entry => entry.Sequence));
		string[] lines = ReadLines();
		Assert.Equal(300, lines.Length);
		Assert.Equal(memory.Select(entry => entry.Sequence), lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("sequence").GetInt64()));
		Assert.All(lines, line =>
		{
			Assert.DoesNotContain(_root, line);
			Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 8192);
			using JsonDocument json = JsonDocument.Parse(line);
			Assert.Equal("backend", json.RootElement.GetProperty("source").GetString());
			Assert.Equal(TimeSpan.Zero, json.RootElement.GetProperty("timestamp").GetDateTimeOffset().Offset);
		});
	}

	[Fact]
	public async Task 默认级别及临时调整只影响之后的记录()
	{
		await using FileLogger logger = new(_root);
		logger.Write(LogSource.Backend, "debug", "忽略");
		Assert.Empty(logger.RecentLogs());
		logger.SetMinimumLevel("trace");
		logger.Write(LogSource.Frontend, "trace", "详细事件");
		Assert.Single(logger.RecentLogs());
		Assert.Throws<ArgumentException>(() => logger.SetMinimumLevel("invalid"));
		await using FileLogger second = new(Path.Combine(_root, "second"));
		Assert.Equal("info", second.GetStatus().MinimumLevel);
	}

	[Fact]
	public async Task 文件故障不阻塞调用且队列有界并可恢复()
	{
		Directory.CreateDirectory(_root);
		string blocked = Path.Combine(_root, "blocked");
		File.WriteAllText(blocked, "阻止创建目录");
		await using FileLogger logger = new(blocked, "info", new FileLoggerOptions { QueueCapacity = 2 });
		Stopwatch timer = Stopwatch.StartNew();
		for (int index = 0; index < 50; index++) logger.Write(LogSource.Backend, "error", "固定故障事件");
		Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
		Assert.True(logger.GetStatus().DroppedCount > 0);
		Assert.False(await logger.FlushAsync(TimeSpan.FromMilliseconds(50)));
		Assert.Equal(50, logger.RecentLogs().Count);
		File.Delete(blocked);
		Assert.True(await logger.FlushAsync(TimeSpan.FromSeconds(5)));
		Assert.True(logger.GetStatus().WriteFailureCount > 0);
		Assert.Null(logger.GetStatus().LastError);
	}

	[Fact]
	public async Task 正常释放刷新队列并关闭文件句柄且可重复调用()
	{
		FileLogger logger = new(_root);
		logger.Write(LogSource.Backend, "warn", "退出事件");
		await logger.DisposeAsync();
		await logger.DisposeAsync();
		Assert.Single(ReadLines());
		string file = Assert.Single(Directory.GetFiles(_root, "*.jsonl"));
		using FileStream exclusive = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
		logger.Write(LogSource.Backend, "warn", "迟到事件");
		Assert.Equal(1, logger.GetStatus().DroppedCount);
	}

	[Fact]
	public async Task 释放故障队列受时间限制且清空不重置计数()
	{
		Directory.CreateDirectory(_root);
		string blocked = Path.Combine(_root, "blocked"); File.WriteAllText(blocked, "占用");
		FileLogger logger = new(blocked, "info", new FileLoggerOptions { QueueCapacity = 1 });
		for (int index = 0; index < 20; index++) logger.Write(LogSource.Backend, "error", "固定事件");
		long dropped = logger.GetStatus().DroppedCount;
		logger.ClearRecentLogs();
		Assert.Empty(logger.RecentLogs()); Assert.Equal(dropped, logger.GetStatus().DroppedCount);
		Stopwatch timer = Stopwatch.StartNew(); await logger.DisposeAsync();
		Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
	}

	[Fact]
	public async Task 分卷和总量维护不会删除无关文件或提前删除旧日志()
	{
		Directory.CreateDirectory(_root);
		string unrelated = Path.Combine(_root, "important.jsonl"); File.WriteAllText(unrelated, "保留");
		string old = Path.Combine(_root, "backend_2026-01-01.log"); File.WriteAllText(old, "旧格式近期写入");
		string expired = Path.Combine(_root, "frontend_2026-01-01.log"); File.WriteAllText(expired, "过期");
		File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));
		await using FileLogger logger = new(_root, "info", new FileLoggerOptions { FileBytes = 2048, TotalBytes = 8192 });
		for (int index = 0; index < 80; index++) logger.Write(LogSource.Backend, "info", new string('a', 900));
		Assert.True(await logger.FlushAsync(TimeSpan.FromSeconds(5)));
		Assert.True(File.Exists(unrelated)); Assert.True(File.Exists(old)); Assert.False(File.Exists(expired));
		FileInfo[] files = new DirectoryInfo(_root).GetFiles("nori_*.jsonl");
		Assert.True(files.Length > 1);
		Assert.True(files.Sum(file => file.Length) <= 8192);
		Assert.All(ReadLines(), line => Assert.NotNull(JsonDocument.Parse(line)));
	}

	[Fact]
	public async Task 异常原文与任意元数据不进入结构化记录()
	{
		await using FileLogger logger = new(_root);
		logger.Write(LogSource.Backend, "error", "固定事件", category: "user content / secret", exception: new InvalidOperationException("聊天正文和请求正文"), operationId: "chat-id");
		Assert.True(await logger.FlushAsync());
		string line = Assert.Single(ReadLines());
		Assert.DoesNotContain("聊天正文", line); Assert.DoesNotContain("user content", line); Assert.DoesNotContain("chat-id", line);
		Assert.Contains("InvalidOperationException", line);
	}

	private string[] ReadLines() => Directory.GetFiles(_root, "nori_*.jsonl").SelectMany(file =>
	{
		using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using StreamReader reader = new(stream);
		return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
	}).ToArray();
}
