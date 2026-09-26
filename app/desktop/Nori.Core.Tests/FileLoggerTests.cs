using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Nori.Core.Data;
using Nori.Core.Logging;

namespace Nori.Core.Tests;

/// <summary>
/// 文件日志的内存环形缓冲与结构化写入测试。
/// </summary>
[SuppressMessage("Security", "S2068", Justification = "伪凭据用于验证日志脱敏。")]
public class FileLoggerTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nori-log-test-{Guid.NewGuid():N}");
	private readonly FileLogger _logger;

	public FileLoggerTests()
	{
		_logger = new FileLogger(_directory);
	}

	public void Dispose()
	{
		_logger.Dispose();
		try
		{
			if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
		}
		catch (IOException)
		{
		}
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void 写入后能按顺序读到快照()
	{
		_logger.Write(LogSource.Backend, "info", "第一条");
		_logger.Write(LogSource.Frontend, "error", "第二条");

		IReadOnlyList<LogEntry> logs = _logger.RecentLogs();

		Assert.Equal(2, logs.Count);
		Assert.Equal("info", logs[0].Level);
		Assert.Equal(LogSource.Backend, logs[0].Source);
		Assert.Equal("第一条", logs[0].Message);
		Assert.Equal(LogSource.Frontend, logs[1].Source);
		Assert.Equal("第二条", logs[1].Message);
	}

	[Fact]
	public void 超出上限时裁掉最旧的日志()
	{
		for (int i = 0; i < 600; i++)
		{
			_logger.Write(LogSource.Backend, "info", $"第{i}条");
		}

		IReadOnlyList<LogEntry> logs = _logger.RecentLogs();

		Assert.Equal(500, logs.Count);
		Assert.Equal("第100条", logs[0].Message);
		Assert.Equal("第599条", logs[^1].Message);
	}

	[Fact]
	public void 快照与源隔离_后续写入不影响已取回列表()
	{
		_logger.Write(LogSource.Backend, "info", "快照前");

		IReadOnlyList<LogEntry> snapshot = _logger.RecentLogs();
		_logger.Write(LogSource.Backend, "info", "快照后");

		Assert.Single(snapshot);
		Assert.Equal("快照前", snapshot[0].Message);
		Assert.Equal(2, _logger.RecentLogs().Count);
	}

	[Fact]
	public async Task 清空只影响内存缓冲_不影响文件内容()
	{
		_logger.Initialize();
		_logger.Write(LogSource.Backend, "warn", "清空前的一条");
		Assert.True(await _logger.FlushAsync());
		string file = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));

		_logger.ClearRecentLogs();

		Assert.Empty(_logger.RecentLogs());
		Assert.True(File.Exists(file));
		using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using StreamReader reader = new(stream);
		Assert.Contains("清空前的一条", reader.ReadToEnd());
	}

	[Fact]
	public void 日志会统一脱敏凭据和路径()
	{
		_logger.Write(LogSource.Backend, "error", "api_key=secret-value https://user:pass@example.com /home/user/nori.db");

		string message = _logger.RecentLogs()[0].Message;
		Assert.DoesNotContain("secret-value", message, StringComparison.Ordinal);
		Assert.DoesNotContain("user:pass", message, StringComparison.Ordinal);
		Assert.DoesNotContain("/home/user/nori.db", message, StringComparison.Ordinal);
	}

	[Fact]
	public void 日志时间使用统一格式()
	{
		_logger.Write(LogSource.Backend, "info", "时间格式");

		LogEntry entry = _logger.RecentLogs()[0];

		Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", entry.Time);
	}

	[Fact]
	public async Task 并发写入磁盘与内存保持相同序号且不泄露调用文件路径()
	{
		await using FileLogger logger = new(_directory);
		Parallel.For(0, 300, index => logger.Write(LogSource.Backend, "info", $"事件 {index}"));
		Assert.True(await logger.FlushAsync(TimeSpan.FromSeconds(5)));
		LogEntry[] memory = logger.RecentLogs().ToArray();
		Assert.Equal(Enumerable.Range(1, 300).Select(value => (long)value), memory.Select(entry => entry.Sequence));
		string[] lines = ReadLines(_directory);
		Assert.Equal(300, lines.Length);
		Assert.Equal(memory.Select(entry => entry.Sequence), lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("sequence").GetInt64()));
		Assert.All(lines, line =>
		{
			Assert.DoesNotContain(_directory, line);
			Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 8192);
			using JsonDocument json = JsonDocument.Parse(line);
			Assert.Equal("backend", json.RootElement.GetProperty("source").GetString());
			Assert.Equal(TimeSpan.Zero, json.RootElement.GetProperty("timestamp").GetDateTimeOffset().Offset);
		});
	}

	[Fact]
	public async Task 默认级别及临时调整只影响之后的记录()
	{
		await using FileLogger logger = new(_directory);
		logger.Write(LogSource.Backend, "debug", "忽略");
		Assert.Empty(logger.RecentLogs());
		logger.SetMinimumLevel("trace");
		logger.Write(LogSource.Frontend, "trace", "详细事件");
		Assert.Single(logger.RecentLogs());
		Assert.Throws<ArgumentException>(() => logger.SetMinimumLevel("invalid"));
		await using FileLogger second = new(Path.Combine(_directory, "second"));
		Assert.Equal("info", second.GetStatus().MinimumLevel);
	}

	[Fact]
	public async Task 文件故障不阻塞调用且队列有界并可恢复()
	{
		Directory.CreateDirectory(_directory);
		string blocked = Path.Combine(_directory, "blocked");
		File.WriteAllText(blocked, "阻止创建目录");
		await using FileLogger logger = new(blocked, "info", new FileLoggerOptions { QueueCapacity = 2, WriteRetryInterval = TimeSpan.FromMilliseconds(1) });
		Stopwatch timer = Stopwatch.StartNew();
		for (int index = 0; index < 50; index++) logger.Write(LogSource.Backend, "error", "固定故障事件");
		Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
		await Task.Delay(200);
		Assert.True(logger.GetStatus().DroppedCount > 0);
		Assert.False(await logger.FlushAsync(TimeSpan.FromMilliseconds(50)));
		Assert.Equal(50, logger.RecentLogs().Count);
		Assert.True(logger.GetStatus().WriteFailureCount > 0);
		File.Delete(blocked);
		Assert.True(await logger.FlushAsync(TimeSpan.FromSeconds(5)));
		Assert.Null(logger.GetStatus().LastError);
	}

	[Fact]
	public async Task 正常释放刷新队列并关闭文件句柄且可重复调用()
	{
		FileLogger logger = new(_directory);
		logger.Write(LogSource.Backend, "warn", "退出事件");
		await logger.DisposeAsync();
		await logger.DisposeAsync();
		Assert.Single(ReadLines(_directory));
		string file = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
		using FileStream exclusive = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
		logger.Write(LogSource.Backend, "warn", "迟到事件");
		Assert.Equal(1, logger.GetStatus().DroppedCount);
	}

	[Fact]
	public async Task 释放故障队列受时间限制且清空不重置计数()
	{
		Directory.CreateDirectory(_directory);
		string blocked = Path.Combine(_directory, "blocked"); File.WriteAllText(blocked, "占用");
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
		Directory.CreateDirectory(_directory);
		string unrelated = Path.Combine(_directory, "important.jsonl"); File.WriteAllText(unrelated, "保留");
		string old = Path.Combine(_directory, "backend_2026-01-01.log"); File.WriteAllText(old, "旧格式近期写入");
		string expired = Path.Combine(_directory, "frontend_2026-01-01.log"); File.WriteAllText(expired, "过期");
		File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));
		await using FileLogger logger = new(_directory, "info", new FileLoggerOptions { FileBytes = 2048, TotalBytes = 8192 });
		for (int index = 0; index < 80; index++) logger.Write(LogSource.Backend, "info", new string('a', 900));
		Assert.True(await logger.FlushAsync(TimeSpan.FromSeconds(5)));
		Assert.True(File.Exists(unrelated)); Assert.True(File.Exists(old)); Assert.False(File.Exists(expired));
		FileInfo[] files = new DirectoryInfo(_directory).GetFiles("nori_*.jsonl");
		Assert.True(files.Length > 1);
		Assert.True(files.Sum(file => file.Length) <= 8192);
		Assert.All(ReadLines(_directory), line => Assert.NotNull(JsonDocument.Parse(line)));
	}

	[Fact]
	public async Task 系统临时目录可写且日志目录本身不能是符号链接()
	{
		string dir = Path.Combine(Path.GetTempPath(), "nori-log-" + Guid.NewGuid().ToString("N"));
		try
		{
			await using FileLogger logger = new(dir);
			logger.Write(LogSource.Backend, "info", "临时目录");
			Assert.True(await logger.FlushAsync(TimeSpan.FromSeconds(5)));
			Assert.NotEmpty(Directory.GetFiles(dir, "nori_*.jsonl"));
		}
		finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

		string real = Path.Combine(_directory, "real");
		Directory.CreateDirectory(real);
		string link = Path.Combine(_directory, "leaf-link");
		try { Directory.CreateSymbolicLink(link, real); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
		Assert.Throws<InvalidOperationException>(() => new FileLogger(link));
		Assert.Empty(Directory.GetFiles(real, "nori_*.jsonl"));
	}

	[Fact]
	public async Task 异常原文与任意元数据不进入结构化记录()
	{
		await using FileLogger logger = new(_directory);
		logger.Write(LogSource.Backend, "error", "固定事件", category: "user content / secret", exception: new InvalidOperationException("聊天正文和请求正文"), operationId: "chat-id");
		Assert.True(await logger.FlushAsync());
		string line = Assert.Single(ReadLines(_directory));
		Assert.DoesNotContain("聊天正文", line); Assert.DoesNotContain("user content", line); Assert.DoesNotContain("chat-id", line);
		Assert.Contains("InvalidOperationException", line);
	}

	private static string[] ReadLines(string directory) => Directory.GetFiles(directory, "nori_*.jsonl").SelectMany(file =>
	{
		using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using StreamReader reader = new(stream);
		return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
	}).ToArray();
}
