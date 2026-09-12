using System.Text.Json;

namespace Nori.Core.Data;

/// <summary>两阶段建立当前版本包内 data，不负责历史目录迁移。</summary>
public static class StorageBootstrapper
{
	private const int MarkerSchemaVersion = 1;

	/// <summary>在数据库打开前建立或验证当前包内数据布局。</summary>
	public static void Bootstrap(AppStoragePaths paths, string productVersion, string rid)
	{
		ArgumentNullException.ThrowIfNull(paths);
		ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
		ArgumentException.ThrowIfNullOrWhiteSpace(rid);
		ValidateExistingTarget(paths);
		if (Directory.Exists(paths.DataRoot) && File.Exists(paths.MarkerPath))
		{
			ValidateMarker(paths.MarkerPath);
			paths.EnsureCreated();
			return;
		}
		if (Directory.Exists(paths.DataRoot) && Directory.EnumerateFileSystemEntries(paths.DataRoot).Any())
			throw new InvalidOperationException($"新数据目录不是空目录且缺少有效 marker: {paths.DataRoot}");

		string staging = paths.DataRoot + $".staging-{Guid.NewGuid():N}";
		try
		{
			Directory.CreateDirectory(staging);
			AppStoragePaths.EnsureNoReparsePoints(staging, paths.PackageRoot);
			CreateLayout(staging);
			WriteMarker(staging, productVersion, rid);
			ValidateStaging(staging, paths);
			if (Directory.Exists(paths.DataRoot) && Directory.EnumerateFileSystemEntries(paths.DataRoot).Any())
				throw new InvalidOperationException("初始化期间 data 目录发生变化，已拒绝覆盖");
			if (Directory.Exists(paths.DataRoot)) Directory.Delete(paths.DataRoot);
			MoveStaging(staging, paths.DataRoot);
		}
		catch
		{
			TryDeleteDirectory(staging);
			throw;
		}
	}

	private static void ValidateExistingTarget(AppStoragePaths paths)
	{
		if (File.Exists(paths.DataRoot)) throw new IOException($"data 路径被文件占用: {paths.DataRoot}");
		if (Directory.Exists(paths.DataRoot)) AppStoragePaths.EnsureNoReparsePoints(paths.DataRoot, paths.PackageRoot);
	}

	private static void CreateLayout(string root)
	{
		string[] directories = [
			"core/database", "core/security", "knowledge/documents", "resources/installed/live2d", "resources/cache", "resources/temp/import",
			"plugins/installed", "plugins/data", "plugins/cache/webview", "plugins/cache/packages/inbox", "plugins/temp/staging",
			"webview/cache/host", "automation/temp/browser", "diagnostics/logs",
		];
		foreach (string relative in directories) Directory.CreateDirectory(Path.Combine(root, relative));
	}

	private static void WriteMarker(string root, string productVersion, string rid)
	{
		string marker = Path.Combine(root, AppStoragePaths.MarkerFileName);
		string json = JsonSerializer.Serialize(new
		{
			schema_version = MarkerSchemaVersion,
			status = "ready",
			product_version = productVersion,
			numeric_version = ExtractNumericVersion(productVersion),
			rid,
			created_at = DateTimeOffset.UtcNow,
		});
		WriteAtomicFile(marker, json + Environment.NewLine);
	}

	private static void ValidateMarker(string path)
	{
		if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
			throw new InvalidOperationException($"数据 marker 不是普通文件: {path}");
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
		JsonElement root = document.RootElement;
		if (!root.TryGetProperty("schema_version", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != MarkerSchemaVersion
			|| !root.TryGetProperty("status", out JsonElement status) || status.ValueKind != JsonValueKind.String || status.GetString() != "ready"
			|| !root.TryGetProperty("product_version", out JsonElement product) || product.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(product.GetString())
			|| !root.TryGetProperty("numeric_version", out JsonElement numeric) || numeric.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(numeric.GetString())
			|| !root.TryGetProperty("rid", out JsonElement rid) || rid.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(rid.GetString()))
			throw new InvalidOperationException($"数据 marker 无效: {path}");
		if (!IsNumericVersionSupported(numeric.GetString()!))
			throw new InvalidOperationException($"数据 marker 的 numeric_version 无效: {path}");
	}

	private static void ValidateStaging(string staging, AppStoragePaths paths)
	{
		ValidateMarker(Path.Combine(staging, AppStoragePaths.MarkerFileName));
		AppStoragePaths.EnsureNoReparsePoints(staging, paths.PackageRoot);
	}

	private static string ExtractNumericVersion(string version)
	{
		if (version.Equals("Dev", StringComparison.Ordinal)) return "0.0.0";
		string value = version.TrimStart('v', 'V').Split(['-', '+'], 2)[0];
		if (!IsNumericVersionSupported(value)) throw new InvalidOperationException("产品版本各段必须在 .NET Version、Int32 和 JS 安全整数范围内");
		return value;
	}

	private static bool IsNumericVersionSupported(string value) =>
		value.Split('.') is [_, _, _] && value.Split('.').All(segment => ushort.TryParse(segment, out _));

	private static void WriteAtomicFile(string path, string content)
	{
		string temporary = path + $".tmp-{Guid.NewGuid():N}";
		try
		{
			using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			using (StreamWriter writer = new(stream, new System.Text.UTF8Encoding(false), leaveOpen: true))
			{
				writer.Write(content);
				writer.Flush();
				stream.Flush(true);
			}
			File.Move(temporary, path, true);
		}
		finally
		{
			try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
		}
	}

	private static void MoveStaging(string staging, string target)
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		IOException? last = null;
		for (int attempt = 0; attempt < 10; attempt++)
		{
			try { Directory.Move(staging, target); return; }
			catch (IOException exception) { last = exception; if (attempt < 9) Thread.Sleep(50); }
		}
		throw new IOException("无法提交 data staging", last);
	}

	private static void TryDeleteDirectory(string path)
	{
		try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
	}
}
