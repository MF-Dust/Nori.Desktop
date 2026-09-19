using System.Diagnostics.CodeAnalysis;
using Nori.Core.Data;
using Nori.Core.Logging;

namespace Nori.Core.Tests;

/// <summary>
/// 文件日志的内存环形缓冲 (调试页日志查看器的数据源)
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
		try
		{
			Directory.Delete(_directory, recursive: true);
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
	public void 清空只影响内存缓冲_不影响文件内容()
	{
		_logger.Initialize();
		_logger.Write(LogSource.Backend, "warn", "清空前的一条");
		string file = Path.Combine(_directory, $"backend_{DateTime.Now:yyyy-MM-dd}.log");

		_logger.ClearRecentLogs();

		Assert.Empty(_logger.RecentLogs());
		Assert.True(File.Exists(file));
		// 常驻写入器仍握着文件句柄, 需以共享读方式打开
		using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using StreamReader reader = new(stream);
		Assert.Contains("清空前的一条", reader.ReadToEnd());
	}

	[Fact]
	public void 日志会统一脱敏凭据和路径()
	{
		// 脱敏测试必须包含会被清除的伪凭据与用户信息。
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
}
