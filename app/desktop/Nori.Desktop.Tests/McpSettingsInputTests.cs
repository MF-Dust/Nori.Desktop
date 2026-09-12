using System.Text.Json;
using Nori.Desktop.Settings.Pages;

namespace Nori.Desktop.Tests;

/// <summary>MCP 编辑器验证真实启动参数、环境变量和工具 schema 的输入契约。</summary>
public sealed class McpSettingsInputTests
{
	[Fact]
	public void ArgumentsPreservePathsSpacesEmptyValuesAndUnicode()
	{
		string[] expected = [@"C:\Program Files\Nori\server.js", "", "two words", "中文路径"];
		IReadOnlyList<string> actual = McpSettingsViewModel.ParseArgumentList(JsonSerializer.Serialize(expected));
		Assert.Equal(expected, actual);
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("[1]")]
	[InlineData("[null]")]
	[InlineData("npx --help")]
	public void ArgumentsRejectNonStringArrays(string input)
	{
		Assert.Throws<ArgumentException>(() => McpSettingsViewModel.ParseArgumentList(input));
	}

	[Fact]
	public void EnvironmentPreservesValuesAndSeparatesKeys()
	{
		IReadOnlyDictionary<string, string> result = McpSettingsViewModel.ParseEnvironment(" TOKEN =  a=b  \r\nEMPTY=\n\n");
		Assert.Equal("  a=b  ", result["TOKEN"]);
		Assert.Equal("", result["EMPTY"]);
		Assert.Equal(2, result.Count);
		Assert.Throws<ArgumentException>(() => McpSettingsViewModel.ParseEnvironment("MISSING_VALUE"));
	}

	[Fact]
	public void ToolSamplesPreserveSchemaValueKinds()
	{
		using JsonDocument schema = JsonDocument.Parse("""
			{"type":"object","properties":{"count":{"type":"integer"},"enabled":{"type":"boolean"},"items":{"type":"array"},"options":{"type":"object"},"query":{"type":"string"}}}
			""");
		McpToolItem tool = new("example", "", "safe", "mcp", true, "server", schema.RootElement.Clone());
		using JsonDocument sample = JsonDocument.Parse(McpSettingsViewModel.CreateToolArguments(tool));
		Assert.Equal(JsonValueKind.Number, sample.RootElement.GetProperty("count").ValueKind);
		Assert.Equal(JsonValueKind.True, sample.RootElement.GetProperty("enabled").ValueKind);
		Assert.Equal(JsonValueKind.Array, sample.RootElement.GetProperty("items").ValueKind);
		Assert.Equal(JsonValueKind.Object, sample.RootElement.GetProperty("options").ValueKind);
		Assert.Equal(JsonValueKind.String, sample.RootElement.GetProperty("query").ValueKind);
	}
}
