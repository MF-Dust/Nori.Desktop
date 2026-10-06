namespace Nori.Core.Tests.TestSupport;

/// <summary>临时 SQLite 数据库文件，Dispose 时删除数据库、附属文件及迁移备份。</summary>
internal sealed class TempDatabase : IDisposable
{
	public TempDatabase(string? prefix = null) =>
		Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix ?? "nori-test"}-{Guid.NewGuid():N}.db");

	public string Path { get; }

	public void Dispose()
	{
		File.Delete(Path);
		File.Delete($"{Path}-wal");
		File.Delete($"{Path}-shm");
		foreach (string backup in Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(Path)!, $"{System.IO.Path.GetFileName(Path)}.pre-migration-*"))
			File.Delete(backup);
	}
}
