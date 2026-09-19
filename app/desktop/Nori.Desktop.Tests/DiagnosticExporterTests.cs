using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Nori.Core.Data;
using Nori.Core.Logging;
using Nori.Desktop.Diagnostics;

namespace Nori.Desktop.Tests;

public sealed class DiagnosticExporterTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nori-diagnostic-{Guid.NewGuid():N}");

	public void Dispose()
	{
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
	public void 导出包只包含白名单文件并再次脱敏日志()
	{
		Directory.CreateDirectory(_directory);
		string logDirectory = Path.Combine(_directory, "logs");
		using FileLogger logger = new(logDirectory);
		logger.Write(LogSource.Backend, "error", "api_key=secret-value /home/user/private.db");
		string target = Path.Combine(_directory, "diagnostics.zip");

		DiagnosticExporter.Result result = DiagnosticExporter.Export(target, logger, null, new AppStoragePaths(_directory), safeMode: true);

		Assert.Equal("diagnostics.zip", result.FileName);
		Assert.True(result.Bytes > 0);
		using ZipArchive archive = ZipFile.OpenRead(target);
		string[] names = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray();
		Assert.Equal(
			["agent-trace.json", "diagnostics.json", "manifest.json", "README.txt", "recent-logs.json", "runtime.json"],
			names);
		string logs = ReadEntry(archive, "recent-logs.json");
		Assert.DoesNotContain("secret-value", logs, StringComparison.Ordinal);
		Assert.DoesNotContain("/home/user/private.db", logs, StringComparison.Ordinal);
		Assert.Contains("safe_mode", ReadEntry(archive, "diagnostics.json"), StringComparison.Ordinal);

		using JsonDocument manifest = JsonDocument.Parse(ReadEntry(archive, "manifest.json"));
		Assert.Equal(1, manifest.RootElement.GetProperty("formatVersion").GetInt32());
		Assert.Equal(
			["diagnostics.json", "runtime.json", "recent-logs.json", "agent-trace.json", "README.txt"],
			manifest.RootElement.GetProperty("files").EnumerateArray().Select(item => item.GetString() ?? "").ToArray());
		Assert.Equal(
			["diagnostics.data_dir", "diagnostics.resources_dir", "diagnostics.log_dir", "diagnostics.database_path"],
			manifest.RootElement.GetProperty("skipped").EnumerateArray().Select(item => item.GetString() ?? "").ToArray());
		Assert.Equal(4, result.Skipped.Count);

		string archiveText = string.Join("\n", archive.Entries.Select(entry => ReadEntry(archive, entry.FullName)));
		Assert.DoesNotContain("nori.db", archiveText, StringComparison.Ordinal);
		Assert.DoesNotContain("secret-value", archiveText, StringComparison.Ordinal);
		Assert.DoesNotContain("/home/user/private.db", archiveText, StringComparison.Ordinal);
		Assert.DoesNotContain(new AppStoragePaths(Environment.CurrentDirectory).DataRoot, archiveText, StringComparison.Ordinal);
	}

	[Fact]
	public void 大量日志导出包受大小与日志条数上限约束()
	{
		Directory.CreateDirectory(_directory);
		using FileLogger logger = new(Path.Combine(_directory, "logs"));
		for (int index = 0; index < 500; index++)
		{
			string payload = Convert.ToBase64String(RandomNumberGenerator.GetBytes(768));
			logger.Write(LogSource.Backend, "info", $"entry-{index:D3}-{payload}");
		}

		string target = Path.Combine(_directory, "large-diagnostics.zip");
		DiagnosticExporter.Result result = DiagnosticExporter.Export(target, logger, null, new AppStoragePaths(_directory), safeMode: false);

		Assert.InRange(result.Bytes, 1, 8L * 1024 * 1024);
		using ZipArchive archive = ZipFile.OpenRead(target);
		using JsonDocument logs = JsonDocument.Parse(ReadEntry(archive, "recent-logs.json"));
		Assert.Equal(200, logs.RootElement.GetArrayLength());
		Assert.All(logs.RootElement.EnumerateArray(), entry =>
			Assert.InRange(entry.GetProperty("message").GetString()!.Length, 0, 1000));
	}

	[Fact]
	public void 取消导出后不残留临时文件()
	{
		Directory.CreateDirectory(_directory);
		using FileLogger logger = new(Path.Combine(_directory, "logs"));
		string target = Path.Combine(_directory, "cancelled.zip");
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		Assert.Throws<OperationCanceledException>(() =>
			DiagnosticExporter.Export(target, logger, null, new AppStoragePaths(_directory), safeMode: false, cancellationToken: cancellation.Token));

		Assert.False(File.Exists(target));
		Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.TopDirectoryOnly));
	}

	private static string ReadEntry(ZipArchive archive, string name)
	{
		ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidOperationException(name);
		using Stream stream = entry.Open();
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}
}
