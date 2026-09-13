using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using NativeInline = Avalonia.Controls.Documents.Inline;

namespace Nori.Desktop.Chat;

/// <summary>将聊天 Markdown 解析为原生控件，并保留 Vue 助手回复的分泡规则。</summary>
public static class ChatMarkdown
{
	private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
		.DisableHtml()
		.UseAutoLinks()
		.UsePipeTables(new PipeTableOptions { UseHeaderForColumnCount = true })
		.UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
		.UsePreciseSourceLocation()
		.Build();

	// JavaScript 的空白集合不含 U+0085，但包含 U+FEFF；不能直接换成 .NET 的 Trim/\s。
	private const string JsSpace = @"\t-\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
	private static readonly Regex TrimWhitespace = new($@"\A[{JsSpace}]+|[{JsSpace}]+\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
	private static readonly Regex BlockStructure = new(
		$@"```|~~~|^[{JsSpace}]{{0,3}}(?:#{{1,6}}[{JsSpace}]|>[{JsSpace}]|(?:[-*+]|[0-9]+[.)])[{JsSpace}]|\|.*\||(?:-{{3,}}|\*{{3,}}|_{{3,}})[{JsSpace}]*$)|^[{JsSpace}]{{4,}}[^{JsSpace}]",
		RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

	// 尺寸对应 theme.less 中的 rem × 10，画刷只供 Markdown 使用，不注册共享主题资源。
	private static readonly FontFamily BodyFont = new("-apple-system, BlinkMacSystemFont, Segoe UI, PingFang SC, Hiragino Sans GB, Microsoft YaHei, Roboto, Helvetica Neue, Arial, sans-serif");
	private static readonly FontFamily CodeFont = new("ui-monospace, SFMono-Regular, Menlo, Consolas, monospace");
	private static readonly IBrush BodyText = Brush("#111827");
	private static readonly IBrush CodeBackground = Brush("#171B22");
	private static readonly IBrush CodeText = Brush("#ecf8ff");
	private static readonly IBrush InlineCodeText = Brush("#082f49");
	private static readonly IBrush InlineCodeBackground = Brush("#1F000000");
	private static readonly IBrush RuleBrush = Brush("#26000000");
	private static readonly IBrush QuoteBackground = Brush("#0F000000");
	private static readonly IBrush QuoteBorder = Brush("#4a7c82");
	private static readonly IBrush TableText = Brush("#1e293b");
	private static readonly IBrush TableHeaderText = Brush("#0f172a");
	private static readonly IBrush TableHeaderBackground = Brush("#14000000");
	private static readonly IBrush LinkText = Brush("#0369a1");

	/// <summary>仅生成原生文本、排版和按钮控件；外链交由宿主处理，不执行 HTML 或加载图片。</summary>
	/// <param name="text">消息原文。</param>
	/// <param name="openUrl">宿主的外链打开回调，仅收到经过验证的绝对 HTTP/HTTPS 地址。</param>
	/// <returns>可直接放入助手气泡的原生控件。</returns>
	public static Control Render(string text, Action<string> openUrl)
	{
		ArgumentNullException.ThrowIfNull(text);
		ArgumentNullException.ThrowIfNull(openUrl);
		return new Renderer(text, openUrl).Blocks(Markdown.Parse(text, Pipeline), trimMargins: true);
	}

	/// <summary>按 Vue 的换行、句末标点和长句逗号规则分泡；流式或结构化 Markdown 始终保持单泡。</summary>
	/// <param name="text">消息原文。</param>
	/// <param name="streaming">是否仍在流式接收。</param>
	/// <returns>去除首尾空白后的气泡文本；空白消息返回空集合。</returns>
	public static IReadOnlyList<string> Split(string text, bool streaming = false)
	{
		ArgumentNullException.ThrowIfNull(text);
		string trimmed = Trim(text);
		if (trimmed.Length == 0) return [];
		// JavaScript 的多行锚点也识别 CR、行分隔符和段落分隔符；这里只规范化结构检测副本。
		if (streaming || BlockStructure.IsMatch(trimmed.Replace('\r', '\n').Replace('\u2028', '\n').Replace('\u2029', '\n')))
			return [trimmed];
		string[] lines = Parts(trimmed, @"\r?\n+");
		if (lines.Length > 1) return lines;
		return Parts(trimmed, @"(?<=[。！？!?；;])")
			.SelectMany(sentence => sentence.Length > 80 ? Parts(sentence, @"(?<=[，,、])") : [sentence])
			.ToArray();
	}

	private static string Trim(string text) => TrimWhitespace.Replace(text, "");
	private static string[] Parts(string text, string pattern) => Regex.Split(text, pattern).Select(Trim).Where(part => part.Length > 0).ToArray();
	private static IBrush Brush(string color) => new ImmutableSolidColorBrush(Color.Parse(color));

	private static TextBlock Text(string? text = null, double size = 13, IBrush? foreground = null) => new()
	{
		Text = text,
		FontFamily = BodyFont,
		FontSize = size,
		LineHeight = size * 1.625,
		Foreground = foreground ?? BodyText,
		TextWrapping = TextWrapping.Wrap,
	};

	private static ScrollViewer HorizontalScroll(Control content) => new()
	{
		Content = content,
		HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
		VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
	};

	private static bool TryHttpUrl(string? value, out Uri? uri)
	{
		uri = null;
		return value is not null && !value.Any(char.IsControl)
			&& (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
			&& Uri.TryCreate(value, UriKind.Absolute, out uri)
			&& uri.Scheme is "http" or "https" && uri.Host.Length > 0;
	}

	private sealed class Renderer(string source, Action<string> openUrl)
	{
		public StackPanel Blocks(ContainerBlock blocks, bool trimMargins = false)
		{
			StackPanel panel = new();
			foreach (Block block in blocks)
			{
				Control? child = Block(block);
				if (child is null) continue;
				// StackPanel 不合并相邻外边距，显式沿用网页的块级 margin 合并。
				if (panel.Children.Count > 0)
				{
					Control previous = panel.Children[^1];
					child.Margin = new Thickness(child.Margin.Left, Math.Max(previous.Margin.Bottom, child.Margin.Top), child.Margin.Right, child.Margin.Bottom);
					previous.Margin = new Thickness(previous.Margin.Left, previous.Margin.Top, previous.Margin.Right, 0);
				}
				panel.Children.Add(child);
			}
			if (trimMargins && panel.Children.Count > 0)
			{
				Control first = panel.Children[0];
				first.Margin = new Thickness(first.Margin.Left, 0, first.Margin.Right, first.Margin.Bottom);
				Control last = panel.Children[^1];
				last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
			}
			return panel;
		}

		private Control? Block(Block block)
		{
			switch (block)
			{
				case HeadingBlock heading:
					TextBlock title = Paragraph(heading, 14);
					title.Name = "ChatMarkdownHeading";
					title.FontWeight = FontWeight.Bold;
					title.LineHeight = 19.6;
					title.Margin = new Thickness(0, 8, 0, 4);
					return title;
				case ParagraphBlock paragraph:
					return Paragraph(paragraph);
				case CodeBlock code:
					return Code(code);
				case QuoteBlock quote:
					return new Border
					{
						Name = "ChatMarkdownQuote",
						Margin = new Thickness(0, 8),
						Padding = new Thickness(12, 4, 0, 4),
						BorderThickness = new Thickness(3, 0, 0, 0),
						BorderBrush = QuoteBorder,
						Background = QuoteBackground,
						CornerRadius = new CornerRadius(0, 4, 4, 0),
						Child = Blocks(quote),
					};
				case ListBlock list:
					return List(list);
				case Table table:
					return Table(table);
				case ThematicBreakBlock:
					return new Border { Name = "ChatMarkdownRule", BorderBrush = RuleBrush, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 8) };
				case LinkReferenceDefinitionGroup:
				case LinkReferenceDefinition:
					return null;
				default:
					// 未支持的语法只显示来源文本，不交给 HTML、XAML 或任何脚本引擎。
					return Text(Source(block));
			}
		}

		private TextBlock Paragraph(LeafBlock block, double size = 13, IBrush? foreground = null)
		{
			TextBlock text = Text(size: size, foreground: foreground);
			text.Margin = new Thickness(0, 4);
			if (block.Inline is { } inline) Inlines(inline, text.Inlines!, size);
			else text.Text = Source(block);
			return text;
		}

		private static Border Code(CodeBlock code) => new()
		{
			Name = "ChatMarkdownCode",
			Margin = new Thickness(0, 8),
			Padding = new Thickness(14, 10),
			CornerRadius = new CornerRadius(8),
			BorderThickness = new Thickness(1),
			BorderBrush = RuleBrush,
			Background = CodeBackground,
			BoxShadow = new BoxShadows(new BoxShadow { Blur = 12, Color = Color.FromArgb(102, 0, 0, 0), IsInset = true }),
			Child = HorizontalScroll(new SelectableTextBlock
			{
				// 不 Trim：空行、缩进、制表符和行尾空格都属于代码内容。
				Text = code.Lines.ToString(),
				FontFamily = CodeFont,
				FontSize = 12,
				LineHeight = 19.2,
				FontWeight = FontWeight.Normal,
				Foreground = CodeText,
				TextWrapping = TextWrapping.NoWrap,
			}),
		};

		private StackPanel List(ListBlock list)
		{
			StackPanel panel = new() { Name = "ChatMarkdownList", Margin = new Thickness(0, 4) };
			long number = long.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out long start) ? start : 1;
			foreach (ListItemBlock item in list.OfType<ListItemBlock>())
			{
				Grid row = new() { ColumnDefinitions = new ColumnDefinitions("18,*"), Margin = new Thickness(0, 2) };
				TextBlock marker = Text(list.IsOrdered ? (number++).ToString(CultureInfo.InvariantCulture) + "." : "•");
				marker.TextAlignment = TextAlignment.Right;
				marker.TextWrapping = TextWrapping.NoWrap;
				marker.Margin = new Thickness(0, list.IsLoose ? 4 : 0, 4, 0);
				Control body = Blocks(item, trimMargins: !list.IsLoose);
				Grid.SetColumn(body, 1);
				row.Children.Add(marker);
				row.Children.Add(body);
				panel.Children.Add(row);
			}
			return panel;
		}

		private Control Table(Table table)
		{
			Grid grid = new() { Name = "ChatMarkdownTable", HorizontalAlignment = HorizontalAlignment.Left };
			foreach (TableColumnDefinition _ in table.ColumnDefinitions) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
			int rowIndex = 0;
			foreach (TableRow row in table.OfType<TableRow>())
			{
				grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
				int nextColumn = 0;
				foreach (TableCell cell in row.OfType<TableCell>())
				{
					// 普通管道表格未显式设置 ColumnIndex，按源单元格顺序定位。
					int column = cell.ColumnIndex >= 0 ? cell.ColumnIndex : nextColumn;
					int span = Math.Max(1, cell.ColumnSpan);
					nextColumn = column + span;
					while (grid.ColumnDefinitions.Count < nextColumn) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
					StackPanel contents = new();
					foreach (Block block in cell)
					{
						TextBlock text = block is LeafBlock leaf
							? Paragraph(leaf, 12, row.IsHeader ? TableHeaderText : TableText)
							: Text(Source(block), 12, row.IsHeader ? TableHeaderText : TableText);
						text.Margin = default;
						text.FontWeight = row.IsHeader ? FontWeight.SemiBold : FontWeight.Normal;
						contents.Children.Add(text);
					}
					Border border = new()
					{
						Padding = new Thickness(8, 4),
						BorderThickness = new Thickness(column == 0 ? 1 : 0, rowIndex == 0 ? 1 : 0, 1, 1),
						BorderBrush = RuleBrush,
						Background = row.IsHeader ? TableHeaderBackground : Brushes.Transparent,
						Child = contents,
					};
					Grid.SetRow(border, rowIndex);
					Grid.SetColumn(border, column);
					Grid.SetColumnSpan(border, span);
					grid.Children.Add(border);
				}
				rowIndex++;
			}
			ScrollViewer scroll = HorizontalScroll(grid);
			scroll.Margin = new Thickness(0, 6);
			return scroll;
		}

		private void Inlines(ContainerInline container, InlineCollection target, double size, FontStyle style = FontStyle.Normal)
		{
			foreach (Markdig.Syntax.Inlines.Inline inline in container)
			{
				switch (inline)
				{
					case LiteralInline literal:
						target.Add(new Run(literal.Content.ToString()));
						break;
					case LineBreakInline:
						// Vue 的 breaks: true 将软换行和硬换行都显示为换行。
						target.Add(new LineBreak());
						break;
					case HtmlEntityInline entity:
						target.Add(new Run(entity.Transcoded.ToString()));
						break;
					case EmphasisInline emphasis when emphasis.DelimiterChar is '*' or '_' or '~':
						Span span = new();
						if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
						else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeight.Bold;
						else span.FontStyle = FontStyle.Italic;
						Inlines(emphasis, span.Inlines, size, emphasis.DelimiterChar != '~' && emphasis.DelimiterCount == 1 ? FontStyle.Italic : style);
						target.Add(span);
						break;
					case CodeInline code:
						TextBlock codeText = Text(code.Content, 12, InlineCodeText);
						codeText.FontFamily = CodeFont;
						codeText.FontWeight = FontWeight.SemiBold;
						codeText.FontStyle = style;
						target.Add(new InlineUIContainer(new Border { Child = codeText, Padding = new Thickness(4, 1), CornerRadius = new CornerRadius(4), Background = InlineCodeBackground }));
						break;
					case LinkInline { IsImage: true }:
						// 与 Vue 的图片消毒策略一致，不创建图片控件，也不下载图片地址。
						break;
					case LinkInline link:
						if (TryHttpUrl(link.Url, out Uri? uri))
						{
							TextBlock label = Text(size: size, foreground: LinkText);
							label.FontStyle = style;
							Inlines(link, label.Inlines!, size, style);
							target.Add(Link(label, uri!, link.Title));
						}
						else Inlines(link, target, size, style);
						break;
					case AutolinkInline autoLink:
						target.Add(TryHttpUrl(autoLink.Url, out Uri? autoUri)
							? Link(Text(autoLink.Url, size, LinkText), autoUri!, null)
							: new Run(autoLink.Url));
						break;
					default:
						target.Add(new Run(Source(inline)));
						break;
				}
			}
		}

		private NativeInline Link(TextBlock label, Uri uri, string? title)
		{
			label.FontWeight = FontWeight.SemiBold;
			label.TextDecorations = [new TextDecoration { Location = TextDecorationLocation.Underline, StrokeOffset = 2, StrokeOffsetUnit = TextDecorationUnit.Pixel }];
			HyperlinkButton button = new()
			{
				Name = "ChatMarkdownLink",
				Content = label,
				Padding = default,
				Margin = default,
				MinWidth = 0,
				MinHeight = 0,
				BorderThickness = default,
				Background = Brushes.Transparent,
				Foreground = LinkText,
				Cursor = new Cursor(StandardCursorType.Hand),
			};
			ToolTip.SetTip(button, string.IsNullOrEmpty(title) ? uri.AbsoluteUri : title);
			AutomationProperties.SetName(button, label.Text ?? label.Inlines?.Text ?? uri.AbsoluteUri);
			// NavigateUri 保持 null，禁止控件自行调用系统启动器；鼠标、键盘和无障碍激活统一走宿主。
			button.Click += (_, args) => { args.Handled = true; openUrl(uri.AbsoluteUri); };
			return new InlineUIContainer(button);
		}

		private string Source(MarkdownObject node)
		{
			int start = Math.Clamp(node.Span.Start, 0, source.Length);
			int end = (int)Math.Clamp((long)node.Span.End + 1, start, source.Length);
			return source[start..end];
		}
	}
}
