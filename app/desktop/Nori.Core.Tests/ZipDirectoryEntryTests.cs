using System.IO.Compression;
using Nori.Core.Resources;

namespace Nori.Core.Tests;

public class ZipDirectoryEntryTests
{
	[Fact]
	public void 目录条目只按空名称判断()
	{
		using MemoryStream stream = new();
		using (ZipArchive archive = new(stream, ZipArchiveMode.Create, true))
		{
			archive.CreateEntry("nested/");
			archive.CreateEntry("nested/file.txt");
			archive.CreateEntry("folder");
		}
		stream.Position = 0;
		using ZipArchive read = new(stream, ZipArchiveMode.Read);
		ZipArchiveEntry directory = read.Entries.Single(entry => entry.FullName == "nested/");
		ZipArchiveEntry file = read.Entries.Single(entry => entry.FullName == "nested/file.txt");
		ZipArchiveEntry namedLikeFolder = read.Entries.Single(entry => entry.FullName == "folder");
		Assert.Equal(directory.Name.Length == 0, ZipExtractor.IsDirectoryEntry(directory));
		Assert.True(ZipExtractor.IsDirectoryEntry(directory));
		Assert.False(ZipExtractor.IsDirectoryEntry(file));
		Assert.False(ZipExtractor.IsDirectoryEntry(namedLikeFolder));
	}
}
