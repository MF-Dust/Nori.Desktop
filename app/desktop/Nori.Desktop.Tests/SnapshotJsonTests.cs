using System.Text.Json;

namespace Nori.Desktop.Tests;

public class SnapshotJsonTests
{
	[Theory]
	[InlineData("""{}""", "missing", "fallback", 0, false)]
	[InlineData("""{"name":null}""", "name", "fallback", 0, false)]
	[InlineData("""{"count":"1.5"}""", "count", "1.5", 0, false)]
	[InlineData("""{"flag":"true"}""", "flag", "true", 0, false)]
	[InlineData("""{"obj":{"a":1}}""", "obj", "{\"a\":1}", 0, false)]
	[InlineData("""{"items":["x"]}""", "items", "[\"x\"]", 0, false)]
	[InlineData("""{"n":1.25}""", "n", "1.25", 1.25, false)]
	[InlineData("""{"ok":true}""", "ok", "True", 0, true)]
	public void 严格读取缺失null原文且不解析数字字符串(string json, string key, string text, double number, bool flag)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement root = document.RootElement;
		JsonElement value = SnapshotJson.P(root, key);
		Assert.Equal(text, SnapshotJson.S(root, key, "fallback"));
		Assert.Equal(number, SnapshotJson.N(root, key));
		Assert.Equal(flag, SnapshotJson.B(root, key));
		if (value.ValueKind == JsonValueKind.Array) Assert.Equal(["x"], SnapshotJson.Items(value).Select(item => item.ToString()).ToArray());
		else Assert.Empty(SnapshotJson.Items(value));
	}
}
