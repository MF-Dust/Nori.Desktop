using Nori.Core.Memory;

namespace Nori.Core.Tests;

public sealed class ReflectionParserTests
{
	[Fact]
	public void 标准JSON能够完整解析()
	{
		ReflectionResult result = ReflectionParser.Parse("""
			{
			  "shouldStore": true,
			  "summary": "用户正在开发 Nori",
			  "personaSummary": "最近在推进 Nori。",
			  "topics": ["Nori"],
			  "importance": 0.9,
			  "keyFacts": [
			    {
			      "type": "factual",
			      "content": "用户正在开发 Nori",
			      "importance": 0.9,
			      "confidence": 1.0,
			      "evidence": [123]
			    }
			  ]
			}
			""");

		Assert.True(result.ShouldStore);
		Assert.Equal("用户正在开发 Nori", result.Summary);
		Assert.Equal("最近在推进 Nori。", result.PersonaSummary);
		Assert.Equal(["Nori"], result.Topics);
		Assert.Equal(0.9, result.Importance);
		ReflectionFact fact = Assert.Single(result.KeyFacts);
		Assert.Equal(MemoryKind.Factual, fact.Kind);
		Assert.Equal([123], fact.Evidence);
	}

	[Fact]
	public void MarkdownJSON围栏继续支持()
	{
		ReflectionResult result = ReflectionParser.Parse("""
			```json
			{"shouldStore":false,"summary":"无需保存","personaSummary":"","topics":[],"importance":0.2,"keyFacts":[]}
			```
			""");

		Assert.False(result.ShouldStore);
		Assert.Equal("无需保存", result.Summary);
	}

	[Fact]
	public void 前导自然语言和尾随说明不会干扰JSON提取()
	{
		ReflectionResult result = ReflectionParser.Parse("""
			以下是整理结果：
			{"shouldStore":false,"summary":"字符串里的 { 花括号 } 不应截断","personaSummary":"","topics":[],"importance":0.2,"keyFacts":[]}
			以上是本次结果。
			""");

		Assert.False(result.ShouldStore);
		Assert.Equal("字符串里的 { 花括号 } 不应截断", result.Summary);
	}

	[Fact]
	public void Think前缀中的对象不会被当成最终结果()
	{
		ReflectionResult result = ReflectionParser.Parse("""
			<think>{"shouldStore":true,"summary":"推理草稿","personaSummary":"","topics":[],"importance":1,"keyFacts":[]}</think>
			{"shouldStore":false,"summary":"最终结果","personaSummary":"","topics":[],"importance":0.1,"keyFacts":[]}
			""");

		Assert.False(result.ShouldStore);
		Assert.Equal("最终结果", result.Summary);
	}

	[Fact]
	public void Evidence数字字符串转换为非负整数()
	{
		ReflectionFact fact = Assert.Single(ReflectionParser.Parse(PayloadWithFacts("""
			{"type":"factual","content":"事实","importance":0.8,"confidence":0.9,"evidence":["123","125"]}
			""")).KeyFacts);

		Assert.Equal([123, 125], fact.Evidence);
	}

	[Fact]
	public void Evidence混合坏数据时只保留合法整数()
	{
		ReflectionFact fact = Assert.Single(ReflectionParser.Parse(PayloadWithFacts("""
			{"type":"factual","content":"事实","importance":0.8,"confidence":0.9,"evidence":[123,"abc",null,{},-1,125]}
			""")).KeyFacts);

		Assert.Equal([123, 125], fact.Evidence);
	}

	[Fact]
	public void 单个KeyFact损坏时保留其他合法事实()
	{
		ReflectionResult result = ReflectionParser.Parse(PayloadWithFacts("""
			{"type":"factual","content":"事实一","importance":0.8,"confidence":0.9,"evidence":[1]},
			{"type":"factual","content":{},"importance":0.8,"confidence":0.9,"evidence":[2]},
			{"type":"preference","content":"事实二","importance":0.7,"confidence":0.8,"evidence":[3]},
			{"type":"unknown","content":"未知类型","importance":0.7,"confidence":0.8,"evidence":[4]},
			{"type":"planned","content":"事实三","importance":0.6,"confidence":0.7,"evidence":[5]}
			"""));

		Assert.Equal(["事实一", "事实二", "事实三"], result.KeyFacts.Select(fact => fact.Content));
	}

	[Theory]
	[InlineData("\"NaN\"")]
	[InlineData("\"Infinity\"")]
	[InlineData("-0.1")]
	[InlineData("1.1")]
	public void 顶层Importance非法时拒绝整个结果(string importance)
	{
		string payload = $$"""
			{"shouldStore":true,"summary":"摘要","personaSummary":"","topics":[],"importance":{{importance}},"keyFacts":[]}
			""";

		ReflectionParseException error = Assert.Throws<ReflectionParseException>(() => ReflectionParser.Parse(payload));
		Assert.Equal("validation", error.Category);
		Assert.Equal("importance", error.Stage);
	}

	[Fact]
	public void 事实中的非法分数只跳过对应事实()
	{
		ReflectionResult result = ReflectionParser.Parse(PayloadWithFacts("""
			{"type":"factual","content":"NaN","importance":"NaN","confidence":0.9,"evidence":[1]},
			{"type":"factual","content":"Infinity","importance":0.8,"confidence":"Infinity","evidence":[2]},
			{"type":"factual","content":"过小","importance":-0.1,"confidence":0.9,"evidence":[3]},
			{"type":"factual","content":"过大","importance":0.8,"confidence":1.1,"evidence":[4]},
			{"type":"factual","content":"合法","importance":0.8,"confidence":0.9,"evidence":[5]}
			"""));

		Assert.Equal("合法", Assert.Single(result.KeyFacts).Content);
	}

	[Fact]
	public void Topics混合非字符串时清理去重并保持顺序()
	{
		ReflectionResult result = ReflectionParser.Parse("""
			{"shouldStore":false,"summary":"","personaSummary":"","topics":[" Nori ",12,null,{},"","Nori","记忆"],"importance":0.2,"keyFacts":[]}
			""");

		Assert.Equal(["Nori", "记忆"], result.Topics);
	}

	[Fact]
	public void 完全损坏JSON抛出带安全诊断的解析异常()
	{
		const string Payload = "{\"shouldStore\":";

		ReflectionParseException error = Assert.Throws<ReflectionParseException>(() => ReflectionParser.Parse(Payload));
		Assert.Equal("json_parse", error.Category);
		Assert.Equal("root", error.Stage);
		Assert.Equal(Payload.Length, error.PayloadLength);
		Assert.DoesNotContain(Payload, error.Message, StringComparison.Ordinal);
	}

	private static string PayloadWithFacts(string facts) => $$"""
		{"shouldStore":true,"summary":"摘要","personaSummary":"画像","topics":["测试"],"importance":0.8,"keyFacts":[{{facts}}]}
		""";
}
