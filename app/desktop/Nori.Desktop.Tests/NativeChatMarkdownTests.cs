using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Nori.Desktop.Chat;

namespace Nori.Desktop.Tests;

[Collection("Native settings")]
public sealed class NativeChatMarkdownTests
{
	[Fact]
	public Task MarkdownUsesNativeBlocksAndSourceTypography() => BridgeCommandsTests.WithSettingsUiAsync(() =>
	{
		string headings = string.Join("\n\n", Enumerable.Range(1, 6).Select(level => new string('#', level) + " 标题"));
		Control root = ChatMarkdown.Render(headings + "\n\n**粗体** 与 *斜体*、~~删除~~、`x < y`、&amp;。\n软换行  \n硬换行\n\n3. 第一项\n4. 第二项\n   - 子项\n\n> 引用\n>\n> 后一段\n\n---\n\n| 列 | 值 |\n| --- | --- |\n| **甲** | 1 |", _ => throw new InvalidOperationException("渲染不得打开链接"));
		Control[] controls = Controls(root).ToArray();
		TextBlock[] titles = controls.OfType<TextBlock>().Where(text => text.Name == "ChatMarkdownHeading").ToArray();
		Assert.Equal(6, titles.Length);
		Assert.All(titles, title =>
		{
			Assert.Equal(14, title.FontSize);
			Assert.Equal(19.6, title.LineHeight);
			Assert.Equal(FontWeight.Bold, title.FontWeight);
			Assert.Equal(Color.Parse("#111827"), Assert.IsAssignableFrom<ISolidColorBrush>(title.Foreground).Color);
		});
		Inline[] inlines = controls.OfType<TextBlock>().SelectMany(text => InlineNodes(text.Inlines)).ToArray();
		Assert.Contains(inlines.OfType<Span>(), span => span.FontWeight == FontWeight.Bold);
		Assert.Contains(inlines.OfType<Span>(), span => span.FontStyle == FontStyle.Italic);
		Assert.Contains(inlines.OfType<Span>(), span => span.TextDecorations == TextDecorations.Strikethrough);
		Assert.Contains(inlines.OfType<Run>(), run => run.Text == "&");
		Assert.Equal(2, inlines.OfType<LineBreak>().Count());
		TextBlock inlineCode = Assert.Single(controls.OfType<TextBlock>(), text => text.Text == "x < y");
		Assert.Equal(12, inlineCode.FontSize);
		Assert.Equal(FontWeight.SemiBold, inlineCode.FontWeight);
		Assert.Equal(Color.Parse("#082f49"), Assert.IsAssignableFrom<ISolidColorBrush>(inlineCode.Foreground).Color);
		Assert.Contains(controls.OfType<TextBlock>(), text => text.Text == "3.");
		Assert.Contains(controls.OfType<TextBlock>(), text => text.Text == "4.");
		Assert.Equal(2, controls.Count(control => control.Name == "ChatMarkdownList"));
		Assert.Single(controls, control => control.Name == "ChatMarkdownQuote");
		Assert.Single(controls, control => control.Name == "ChatMarkdownRule");
		Grid table = Assert.IsType<Grid>(Assert.Single(controls, control => control.Name == "ChatMarkdownTable"));
		Assert.Equal(2, table.ColumnDefinitions.Count);
		Assert.Equal(2, table.RowDefinitions.Count);
		Assert.Equal(4, table.Children.Count);
		Assert.All(Controls(table).OfType<TextBlock>(), text => Assert.Equal(12, text.FontSize));
		Assert.DoesNotContain(controls, control => control is Image || control.GetType().Name.Contains("WebView", StringComparison.Ordinal));
		return Task.CompletedTask;
	});

	[Theory]
	[InlineData("```cs\n  a\t b  \n\n    c\n```", "  a\t b  \n\n    c")]
	[InlineData("~~~\n  first\n  second", "  first\n  second")]
	[InlineData("    first  \n    \n        second\n", "first  \n\n    second")]
	public Task CodePreservesWhitespaceAndCopiesOnlyText(string markdown, string expected) => BridgeCommandsTests.WithSettingsUiAsync(() =>
	{
		Control root = ChatMarkdown.Render(markdown, _ => throw new InvalidOperationException("代码不得打开链接"));
		Border border = Assert.IsType<Border>(Assert.Single(Controls(root), control => control.Name == "ChatMarkdownCode"));
		ScrollViewer scroll = Assert.IsType<ScrollViewer>(border.Child);
		Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
		Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, scroll.VerticalScrollBarVisibility);
		SelectableTextBlock code = Assert.IsType<SelectableTextBlock>(scroll.Content);
		Assert.Equal(expected, code.Text);
		Assert.Equal(TextWrapping.NoWrap, code.TextWrapping);
		Assert.Equal(12, code.FontSize);
		Assert.Equal(19.2, code.LineHeight);
		Assert.Equal(Color.Parse("#171B22"), Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
		Assert.Equal(Color.Parse("#ecf8ff"), Assert.IsAssignableFrom<ISolidColorBrush>(code.Foreground).Color);
		code.SelectAll();
		Assert.Equal(expected, code.SelectedText);
		return Task.CompletedTask;
	});

	[Theory]
	[InlineData("[文档](https://example.test/docs?x=1&amp;y=2)", "https://example.test/docs?x=1&y=2")]
	[InlineData("[**粗体** *标签*](HTTP://example.test/docs)", "http://example.test/docs")]
	[InlineData("https://example.test/docs", "https://example.test/docs")]
	[InlineData("<https://example.test/docs>", "https://example.test/docs")]
	[InlineData("[文档][站点]\n\n[站点]: https://example.test/docs \"标题\"", "https://example.test/docs")]
	public Task SafeLinksUseHostCallbackWithoutAutomaticNavigation(string markdown, string expected) => BridgeCommandsTests.WithSettingsUiAsync(() =>
	{
		List<string> opened = [];
		Control root = ChatMarkdown.Render(markdown, opened.Add);
		HyperlinkButton link = Assert.Single(Controls(root).OfType<HyperlinkButton>());
		Assert.Empty(opened);
		Assert.Null(link.NavigateUri);
		Assert.True(link.Focusable);
		Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(link)));
		link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.Equal([expected], opened);
		Assert.Null(link.NavigateUri);
		return Task.CompletedTask;
	});

	[Fact]
	public Task HtmlImagesAndUnsafeLinksCannotCreateExecutableControls() => BridgeCommandsTests.WithSettingsUiAsync(() =>
	{
		const string Markdown = "<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>\n\n<iframe src=\"file:///x\"></iframe>\n\n![替代文本](https://example.test/image.png)\n\n[脚本](javascript:alert(1)) [文件](file:///C:/x) [数据](data:text/html,hi) [协议](//example.test) [相对](/help) [邮件](mailto:nori@example.test) [换行](https://example.test/a&#x0A;b)\n\n&lt;b&gt;安全文本&lt;/b&gt;";
		Control root = ChatMarkdown.Render(Markdown, _ => throw new InvalidOperationException("不安全地址不得进入宿主"));
		Control[] controls = Controls(root).ToArray();
		Assert.Empty(controls.OfType<HyperlinkButton>());
		Assert.Empty(controls.OfType<Image>());
		Assert.DoesNotContain(controls, control => control.GetType().Name.Contains("WebView", StringComparison.Ordinal));
		string text = string.Join("\n", controls.OfType<TextBlock>().Select(block => block.Text ?? block.Inlines?.Text));
		Assert.Contains("<script>alert(1)</script>", text);
		Assert.Contains("<img src=x onerror=alert(1)>", text);
		Assert.Contains("<b>安全文本</b>", text);
		Assert.DoesNotContain("替代文本", text);
		return Task.CompletedTask;
	});

	[Theory]
	[InlineData("第一句\r\n第二句\n\n第三句", new[] { "第一句", "第二句", "第三句" })]
	[InlineData("你好呀！今天想聊点什么？", new[] { "你好呀！", "今天想聊点什么？" })]
	[InlineData("English. Stays together.", new[] { "English. Stays together." })]
	[InlineData("第一句。 第二句! 第三句;结束", new[] { "第一句。", "第二句!", "第三句;", "结束" })]
	[InlineData("\uFEFF　正文　\uFEFF", new[] { "正文" })]
	[InlineData("\u0085正文\u0085", new[] { "\u0085正文\u0085" })]
	[InlineData("说明\n١. 项目", new[] { "说明", "١. 项目" })]
	[InlineData("    code\nnext", new[] { "code", "next" })]
	public void SplitMatchesVueTextRules(string text, string[] expected) => Assert.Equal(expected, ChatMarkdown.Split(text));

	[Theory]
	[InlineData("说明\n```ts\nconst a = 1\n```")]
	[InlineData("说明\n~~~\n代码\n~~~")]
	[InlineData("# 标题\n正文")]
	[InlineData("说明\r# 标题")]
	[InlineData("说明\u2028# 标题")]
	[InlineData("说明\n> 引用\n正文")]
	[InlineData("步骤:\n- 第一步\n+ 第二步")]
	[InlineData("1. 第一条\n2. 第二条")]
	[InlineData("1) 第一条\n2) 第二条")]
	[InlineData("| 列 | 值 |\n| --- | --- |\n| a | 1 |")]
	[InlineData("说明\n---\n正文")]
	[InlineData("说明\n___\n正文")]
	[InlineData("说明\n***\n正文")]
	[InlineData("说明\n    代码\n结尾")]
	public void SplitPreservesStructuredMarkdown(string text) => Assert.Equal([text], ChatMarkdown.Split(text));

	[Fact]
	public void SplitKeepsStreamingStableAndUsesUtf16LengthForLongSentences()
	{
		Assert.Empty(ChatMarkdown.Split(" \n\uFEFF ", streaming: true));
		Assert.Equal(["你好！\n第二行。"], ChatMarkdown.Split(" 你好！\n第二行。 ", streaming: true));
		string eighty = new string('甲', 39) + "，" + new string('乙', 40);
		Assert.Equal([eighty], ChatMarkdown.Split(eighty));
		Assert.Equal([new string('甲', 39) + "，", new string('乙', 40) + "。"], ChatMarkdown.Split(eighty + "。"));
		string emoji = string.Concat(Enumerable.Repeat("🙂", 39)) + "，";
		Assert.Equal([emoji, "结束"], ChatMarkdown.Split(emoji + "结束"));
	}

	private static IEnumerable<Control> Controls(Control root)
	{
		yield return root;
		IEnumerable<Control> children = root switch
		{
			Panel panel => panel.Children,
			Decorator { Child: { } child } => [child],
			ContentControl { Content: Control content } => [content],
			TextBlock text => InlineNodes(text.Inlines).OfType<InlineUIContainer>().Select(inline => inline.Child),
			_ => [],
		};
		foreach (Control child in children)
			foreach (Control descendant in Controls(child)) yield return descendant;
	}

	private static IEnumerable<Inline> InlineNodes(InlineCollection? inlines)
	{
		if (inlines is null) yield break;
		foreach (Inline inline in inlines)
		{
			yield return inline;
			if (inline is Span span)
				foreach (Inline child in InlineNodes(span.Inlines)) yield return child;
		}
	}
}
