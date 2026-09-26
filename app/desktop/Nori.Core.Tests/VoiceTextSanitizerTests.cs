using Nori.Core.Voice;

namespace Nori.Core.Tests;

/// <summary>TTS 文本清洗（颜文字/装饰符号移除）测试，行为对齐 IndexTTS 技能 tts_cli。</summary>
public class VoiceTextSanitizerTests
{
	[Theory]
	[InlineData("小卡～新版TTS升级完成啦(๑•̀ㅂ•́)و✧", "小卡～新版TTS升级完成啦")]
	[InlineData("今天心情不错(づ￣3￣)づ╭❤～", "今天心情不错")]
	[InlineData("好耶(^_^)", "好耶")]
	[InlineData("别这样(T_T)", "别这样")]
	[InlineData("啊咧(>_<)", "啊咧")]
	[InlineData("随便(・_・)", "随便")]
	[InlineData("嗯嗯(｡･ω･｡)", "嗯嗯")]
	public void 括号颜文字被移除(string input, string expected)
	{
		Assert.Equal(expected, VoiceTextSanitizer.StripKaomoji(input));
	}

	[Theory]
	[InlineData("（开心）", "（开心）")]
	[InlineData("（重要）", "（重要）")]
	[InlineData("(メニュー)", "(メニュー)")]
	[InlineData("(x86)", "(x86)")]
	[InlineData("（银河）", "（银河）")]
	public void 正常括号不被误删(string input, string expected)
	{
		Assert.Equal(expected, VoiceTextSanitizer.StripKaomoji(input));
	}

	[Theory]
	[InlineData("好耶✧", "好耶")]
	[InlineData("谢谢啦❤", "谢谢啦")]
	[InlineData("晚安～♪", "晚安～")]
	[InlineData("开心✨", "开心")]
	public void 尾部硬装饰被移除但语气波浪保留(string input, string expected)
	{
		Assert.Equal(expected, VoiceTextSanitizer.StripKaomoji(input));
	}

	[Fact]
	public void 多个颜文字连排全部移除()
	{
		Assert.Equal("今天天气不错", VoiceTextSanitizer.StripKaomoji("今天(^_^)天气(≧▽≦)不错(๑˃̵ᴗ˂̵)"));
	}

	[Fact]
	public void 全颜文字文本清洗后为空()
	{
		Assert.Equal("", VoiceTextSanitizer.StripKaomoji("(๑•̀ㅂ•́)و✧"));
	}

	[Fact]
	public void 无颜文字文本原样返回()
	{
		const string input = "今天天气真好呀，我们一起去散步吧？";
		Assert.Equal(input, VoiceTextSanitizer.StripKaomoji(input));
	}

	[Fact]
	public void 空串与null安全()
	{
		Assert.Equal("", VoiceTextSanitizer.StripKaomoji(""));
		Assert.Equal("", VoiceTextSanitizer.StripKaomoji(null));
	}

	[Theory]
	[InlineData("今天好开心😊", "今天好开心")]
	[InlineData("谢谢啦❤️", "谢谢啦")]
	[InlineData("超棒！🎉", "超棒！")]
	[InlineData("我是狼🐺", "我是狼")]
	[InlineData("晚安🌙", "晚安")]
	[InlineData("加油💪", "加油")]
	[InlineData("震惊🤯", "震惊")]
	[InlineData("欢迎👋", "欢迎")]
	public void 单字符emoji被移除(string input, string expected)
	{
		Assert.Equal(expected, VoiceTextSanitizer.StripKaomoji(input));
	}

	[Theory]
	[InlineData("庆祝🎉🎊", "庆祝")]
	[InlineData("爱你❤️😘", "爱你")]
	[InlineData("太棒了👍👍", "太棒了")]
	public void 多个emoji连排全部移除(string input, string expected)
	{
		Assert.Equal(expected, VoiceTextSanitizer.StripKaomoji(input));
	}

	[Fact]
	public void emoji与颜文字混合全部移除()
	{
		Assert.Equal("今天真不错", VoiceTextSanitizer.StripKaomoji("今天(^_^)真不错😊✨"));
	}

	[Fact]
	public void 纯emoji文本清洗后为空()
	{
		Assert.Equal("", VoiceTextSanitizer.StripKaomoji("😊😂❤️"));
	}

	[Fact]
	public void emoji序列含变体选择符与ZWJ被整体移除()
	{
		Assert.Equal("全家福", VoiceTextSanitizer.StripKaomoji("全家福👨‍👩‍👧"));
	}

	[Theory]
	[InlineData("𠮷野家", "𠮷野家")]
	[InlineData("数学字符𝕏仍保留", "数学字符𝕏仍保留")]
	[InlineData("𠮷野家😊", "𠮷野家")]
	public void 非Emoji补充平面字符会保留(string input, string expected)
	{
		Assert.Equal(expected, VoiceTextSanitizer.StripKaomoji(input));
	}

	[Fact]
	public void 版权与商标符号被移除()
	{
		// © 被删除后其两侧空格可能保留（正常文本场景罕见，不做额外空格合并）
		Assert.Equal("版本v2.0  2026", VoiceTextSanitizer.StripKaomoji("版本v2.0 © 2026"));
		Assert.Equal("注册标志", VoiceTextSanitizer.StripKaomoji("注册标志®"));
	}
}
