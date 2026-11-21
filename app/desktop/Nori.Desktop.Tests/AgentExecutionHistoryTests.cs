using System.Text.Json;
using Nori.Core.Agent;
using Nori.Core.Logging;
using Nori.Desktop.Diagnostics;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[Fact]
	public void 导出的执行记录沿用脱敏边界()
	{
		string directory = Path.Combine(Path.GetTempPath(), "nori-exec-export-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			using AgentExecutionJournal journal = new();
			AgentExecutionRequest request = journal.Open("session");
			request.ToolStarted(0, "call", "tool token=secret-value");
			request.FinishTool(0, "call", "tool", 1, AgentExecutionStatus.Failed, "boom /home/user/notes.txt api_key=secret-value", false);
			using FileLogger logger = new(Path.Combine(directory, "logs"));
			string target = Path.Combine(directory, "diagnostics.zip");
			DiagnosticExporter.Export(target, logger, null, new Nori.Core.Data.AppStoragePaths(directory), true, default, null, journal);
			using System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.OpenRead(target);
			string execution = ReadZip(archive, "agent-execution.json");
			Assert.Contains("Failed", execution, StringComparison.Ordinal);
			Assert.DoesNotContain("secret-value", execution, StringComparison.Ordinal);
			Assert.DoesNotContain("/home/user", execution, StringComparison.Ordinal);
			Assert.DoesNotContain("notes.txt", execution, StringComparison.Ordinal);
			using JsonDocument document = JsonDocument.Parse(execution);
			Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("summary").GetProperty("PromptTokens").ValueKind);
		}
		finally
		{
			try { Directory.Delete(directory, true); } catch (IOException) { }
		}
	}

	private static string ReadZip(System.IO.Compression.ZipArchive archive, string name)
	{
		System.IO.Compression.ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidOperationException(name);
		using Stream stream = entry.Open();
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}
}
