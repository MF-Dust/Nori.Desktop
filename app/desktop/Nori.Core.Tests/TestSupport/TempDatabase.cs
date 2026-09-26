namespace Nori.Core.Tests.TestSupport;

/// <summary>临时 SQLite 数据库文件，Dispose 时删除 db 及 WAL/SHM 附属文件。</summary>
internal sealed class TempDatabase : IDisposable
{
	public TempDatabase(string? prefix = null) =>
		Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix ?? "nori-test"}-{Guid.NewGuid():N}.db");

	public string Path { get; }

	public void Dispose()
	{
		try
		{
			File.Delete(Path);
			File.Delete($"{Path}-wal");
			File.Delete($"{Path}-shm");
		}
		catch (IOException)
		{
		}
	}
}
