using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Nori.Core.Agent;
using Nori.Core.Data;
using Nori.Core.Logging;
using Nori.Core.Security;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Diagnostics;

/// <summary>
/// 生成最小化、可分享的诊断 ZIP。
///
/// 只写入白名单运行信息和最近日志，不复制数据库、配置、聊天、记忆、资源或请求正文。
/// </summary>
public static class DiagnosticExporter
{
	private const int MaxLogEntries = 200;
	private const int MaxLogCharacters = 1000;
	private const long MaxArchiveBytes = 8L * 1024 * 1024;

	public sealed record Result(string FileName, long Bytes, IReadOnlyList<string> Skipped);

	/// <summary>写出诊断 ZIP；调用方应在后台线程执行。</summary>
	public static Result Export(
		string targetPath,
		FileLogger logger,
		PetRuntime? pet,
		AppStoragePaths paths,
		bool safeMode,
		CancellationToken cancellationToken = default,
		AgentTraceCollector? trace = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentNullException.ThrowIfNull(paths);
		cancellationToken.ThrowIfCancellationRequested();

		string fullPath = Path.GetFullPath(targetPath);
		string? directory = Path.GetDirectoryName(fullPath);
		if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("诊断文件保存位置无效");
		Directory.CreateDirectory(directory);

		string temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
		List<string> skipped = [];
		try
		{
			Dictionary<string, string> diagnostics = DiagnosticInfo.Build(pet, paths, safeMode);
			foreach (string key in new[] {"data_dir", "resources_dir", "log_dir", "database_path"})
			{
				if (diagnostics.Remove(key)) skipped.Add($"diagnostics.{key}");
			}
			IReadOnlyList<LogEntry> logs = logger.RecentLogs();
			IReadOnlyList<AgentTraceRecord> traces = trace?.Snapshot() ?? [];
			var runtime = new
			{
				productVersion = Nori.Core.ProductVersion.Current,
				safeMode,
				generatedAt = DateTimeOffset.UtcNow.ToString("O"),
				logCount = Math.Min(logs.Count, MaxLogEntries),
				traceCount = traces.Count,
				logging = logger.GetStatus(),
			};
			var safeLogs = logs
			.TakeLast(MaxLogEntries)
			.Select(entry => new
			{
				time = entry.Time,
				level = entry.Level,
				source = entry.Source == LogSource.Backend ? "backend" : "frontend",
				message = Limit(SensitiveDataRedactor.Redact(entry.Message), MaxLogCharacters),
				timestamp = entry.Timestamp,
				sessionId = entry.SessionId,
				sequence = entry.Sequence,
				category = entry.Category,
				eventId = entry.EventId,
				windowLabel = entry.WindowLabel,
				operationId = entry.OperationId,
				exceptionType = entry.ExceptionType,
				exceptionSite = entry.ExceptionSite,
			})
			.ToArray();

			using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
			using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
			{
				WriteJson(archive, "diagnostics.json", diagnostics, cancellationToken);
				WriteJson(archive, "runtime.json", runtime, cancellationToken);
				WriteJson(archive, "recent-logs.json", safeLogs, cancellationToken);
				WriteJson(archive, "agent-trace.json", traces, cancellationToken);
				WriteText(archive, "README.txt", "本压缩包只包含脱敏的运行诊断、Agent 性能元数据与最近日志，不包含数据库、聊天、记忆、资源或密钥。\n", cancellationToken);
				WriteJson(archive, "manifest.json", new
				{
					formatVersion = 1,
					files = new[] {"diagnostics.json", "runtime.json", "recent-logs.json", "agent-trace.json", "README.txt"},
					skipped,
				}, cancellationToken);
				archive.Dispose();
				stream.Flush(flushToDisk: true);
				if (stream.Length > MaxArchiveBytes) throw new InvalidOperationException("诊断包超过大小限制");
			}

			File.Move(temporary, fullPath, overwrite: true);
			return new Result(Path.GetFileName(fullPath), new FileInfo(fullPath).Length, skipped);
		}
		catch
		{
			TryDelete(temporary);
			throw;
		}
		finally
		{
			TryDelete(temporary);
		}
	}

	private static void WriteJson(ZipArchive archive, string name, object value, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
		using Stream stream = entry.Open();
		using Utf8JsonWriter writer = new(stream, new JsonWriterOptions {Indented = true});
		JsonSerializer.Serialize(writer, value);
	}

	private static void WriteText(ZipArchive archive, string name, string value, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
		using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
		writer.Write(value);
	}

	private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "…";

	private static void TryDelete(string path)
	{
		try { if (File.Exists(path)) File.Delete(path); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}
}
