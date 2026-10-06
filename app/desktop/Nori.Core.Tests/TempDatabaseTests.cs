using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

public sealed class TempDatabaseTests
{
	[Fact]
	public void 数据库释放后可独占打开且临时库清理包含迁移备份()
	{
		using TempDatabase temp = new("nori-cleanup");
		using (NoriDatabase database = NoriDatabase.Open(temp.Path))
		{
			database.Locked(connection =>
			{
				using var command = connection.CreateCommand();
				command.CommandText = "PRAGMA user_version = 6;";
				command.ExecuteNonQuery();
			});
		}
		using (NoriDatabase database = NoriDatabase.Open(temp.Path))
		{
			database.Locked(connection =>
			{
				using var command = connection.CreateCommand();
				command.CommandText = "PRAGMA user_version;";
				Assert.Equal(NoriDatabase.DatabaseSchemaVersion, (long)command.ExecuteScalar()!);
			});
		}

		string[] backups = Directory.GetFiles(Path.GetDirectoryName(temp.Path)!, $"{Path.GetFileName(temp.Path)}.pre-migration-*");
		string backup = Assert.Single(backups);
		// 独占打开同时验证主连接和备份校验连接都已关闭，不依赖 GC 或全局清池。
		using (File.Open(temp.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
		using (File.Open(backup, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

		temp.Dispose();
		Assert.False(File.Exists(temp.Path));
		Assert.False(File.Exists($"{temp.Path}-wal"));
		Assert.False(File.Exists($"{temp.Path}-shm"));
		Assert.False(File.Exists(backup));
	}
}
