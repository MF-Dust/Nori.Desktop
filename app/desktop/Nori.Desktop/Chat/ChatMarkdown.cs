using Nori.Desktop.Appearance;
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

/// <summary>
/// 生成图片的读取与打开回调。
///
/// 聊天 Markdown 只为 <c>nori-image:</c> 引用创建图片控件，字节由宿主从生成图片目录读取；
/// 其余图片地址仍然不加载。
/// </summary>
/// <param name="Load">按文件名读取图片字节；不存在时返回 null。</param>
/// <param name="Open">用系统默认程序打开图片。</param>
public sealed record ChatMarkdownImages(Func<string, CancellationToken, Task<byte[]?>> Load, Action<string> Open);

/// <summary>将聊天 Markdown 解析为原生控件，并保留 Vue 助手回复的分泡规则。</summary>
public static class ChatMarkdown
{
	/// <summary>生成图片的显示上限 (逻辑像素)。</summary>
	private const double ImageMaxSize = 320;

	/// <summary>最近解码过的图片；流式刷新与重新渲染时不重复读取和解码。</summary>
	private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap> ImageCache = new(StringComparer.Ordinal);
	private static readonly LinkedList<string> ImageCacheOrder = new();
	private const int ImageCacheCapacity = 24;

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

	// 尺寸使用 NoriThemeTokens 的 rem × 10 规则，画刷只供 Markdown 使用，不注册共享主题资源。
	private static readonly FontFamily BodyFont = NoriTypography.Conversation;
	private static readonly FontFamily CodeFont = new("ui-monospace, SFMono-Regular, Menlo, Consolas, monospace");
	private static readonly IBrush BodyText = NoriThemeTokens.Brush("chat-ai-text");
	private static readonly IBrush CodeBackground = NoriThemeTokens.Brush("bg-base");
	private static readonly IBrush CodeText = NoriThemeTokens.Brush("text-primary");
	private static readonly IBrush InlineCodeText = NoriThemeTokens.Brush("chat-ai-text");
	private static readonly IBrush InlineCodeBackground = NoriThemeTokens.Brush("ink-12");
	private static readonly IBrush RuleBrush = NoriThemeTokens.Brush("ink-15");
	private static readonly IBrush QuoteBackground = NoriThemeTokens.Brush("ink-6");
	private static readonly IBrush QuoteBorder = NoriThemeTokens.Brush("chat-markdown-quote");
	private static readonly IBrush TableText = NoriThemeTokens.Brush("chat-ai-text");
	private static readonly IBrush TableHeaderText = NoriThemeTokens.Brush("chat-ai-text");
	private static readonly IBrush TableHeaderBackground = NoriThemeTokens.Brush("ink-8");
	private static readonly IBrush LinkText = NoriThemeTokens.Brush("chat-markdown-link");

	/// <summary>仅生成原生文本、排版和按钮控件；外链交由宿主处理，不执行 HTML 或加载图片。</summary>
	/// <param name="text">消息原文。</param>
	/// <param name="openUrl">宿主的外链打开回调，仅收到经过验证的绝对 HTTP/HTTPS 地址。</param>
	/// <returns>可直接放入助手气泡的原生控件。</returns>
	/// <param name="images">生成图片的读取回调；为 null 时图片引用不显示。</param>
	public static Control Render(string text, Action<string> openUrl, ChatMarkdownImages? images = null)
	{
		ArgumentNullException.ThrowIfNull(text);
		ArgumentNullException.ThrowIfNull(openUrl);
		return new Renderer(text, openUrl, images).Blocks(Markdown.Parse(text, Pipeline), trimMargins: true);
	}

	private static Avalonia.Media.Imaging.Bitmap? CachedImage(string name)
	{
		lock (ImageCache) return ImageCache.GetValueOrDefault(name);
	}

	private static void CacheImage(string name, Avalonia.Media.Imaging.Bitmap bitmap)
	{
		lock (ImageCache)
		{
			if (ImageCache.ContainsKey(name)) return;
			ImageCache[name] = bitmap;
			ImageCacheOrder.AddLast(name);
			// 淘汰的位图可能仍挂在旧控件上，交给 GC 回收而不主动释放。
			while (ImageCacheOrder.Count > ImageCacheCapacity && ImageCacheOrder.First is { } oldest)
			{
				ImageCache.Remove(oldest.Value);
				ImageCacheOrder.RemoveFirst();
			}
		}
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

	private static TextBlock Text(string? text = null, double size = NoriMetrics.FontBase, IBrush? foreground = null) => new()
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

	private sealed class Renderer(string source, Action<string> openUrl, ChatMarkdownImages? images)
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
					TextBlock title = Paragraph(heading, NoriMetrics.FontMd);
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
						CornerRadius = new CornerRadius(0, NoriMetrics.RadiusXs, NoriMetrics.RadiusXs, 0),
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

		private TextBlock Paragraph(LeafBlock block, double size = NoriMetrics.FontBase, IBrush? foreground = null)
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
			CornerRadius = new CornerRadius(NoriMetrics.RadiusSm),
			BorderThickness = new Thickness(1),
			BorderBrush = RuleBrush,
			Background = CodeBackground,
			BoxShadow = new BoxShadows(new BoxShadow { Blur = 12, Color = NoriThemeTokens.Color("shadow-inset"), IsInset = true }),
			Child = HorizontalScroll(new SelectableTextBlock
			{
				// 不 Trim：空行、缩进、制表符和行尾空格都属于代码内容。
				Text = code.Lines.ToString(),
				FontFamily = CodeFont,
				FontSize = NoriMetrics.FontXs,
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
							? Paragraph(leaf, NoriMetrics.FontXs, row.IsHeader ? TableHeaderText : TableText)
							: Text(Source(block), NoriMetrics.FontXs, row.IsHeader ? TableHeaderText : TableText);
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
						TextBlock codeText = Text(code.Content, NoriMetrics.FontXs, InlineCodeText);
						codeText.FontFamily = CodeFont;
						codeText.FontWeight = FontWeight.SemiBold;
						codeText.FontStyle = style;
						target.Add(new InlineUIContainer(new Border { Child = codeText, Padding = new Thickness(4, 1), CornerRadius = new CornerRadius(NoriMetrics.RadiusXs), Background = InlineCodeBackground }));
						break;
					case LinkInline { IsImage: true } image:
						// 只显示本机生成的图片；其余图片与 Vue 的消毒策略一致，不创建控件，也不下载地址。
						if (images is not null && Nori.Core.Chat.Images.GeneratedImageStore.ParseReference(image.Url) is { } name)
							target.Add(new InlineUIContainer(GeneratedImage(name, image.FirstChild is null ? null : Source(image.FirstChild), images)));
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

		/// <summary>生成图片：先占位，后台读取并解码后替换；点击用系统程序打开原图。</summary>
		private static Control GeneratedImage(string name, string? alt, ChatMarkdownImages images)
		{
			Image picture = new()
			{
				Stretch = Stretch.Uniform,
				MaxWidth = ImageMaxSize,
				MaxHeight = ImageMaxSize,
			};
			TextBlock placeholder = Text("图片加载中…", NoriMetrics.FontXs, InlineCodeText);
			Button frame = new()
			{
				Name = "ChatGeneratedImage",
				Padding = new Thickness(0),
				Margin = new Thickness(0, 6),
				MinWidth = 0,
				MinHeight = 0,
				Background = Brushes.Transparent,
				BorderThickness = default,
				Cursor = new Cursor(StandardCursorType.Hand),
				Content = new Border
				{
					CornerRadius = new CornerRadius(NoriMetrics.RadiusSm),
					ClipToBounds = true,
					BorderBrush = RuleBrush,
					BorderThickness = new Thickness(1),
					MinWidth = 120,
					MinHeight = 60,
					Child = placeholder,
				},
			};
			string label = string.IsNullOrWhiteSpace(alt) ? "生成的图片" : alt.Trim();
			ToolTip.SetTip(frame, label);
			AutomationProperties.SetName(frame, label);
			frame.Click += (_, args) => { args.Handled = true; images.Open(name); };

			void Show(Avalonia.Media.Imaging.Bitmap bitmap)
			{
				picture.Source = bitmap;
				if (frame.Content is Border border) border.Child = picture;
			}

			if (CachedImage(name) is { } cached)
			{
				Show(cached);
				return frame;
			}

			_ = LoadAsync();
			return frame;

			async Task LoadAsync()
			{
				try
				{
					byte[]? bytes = await images.Load(name, CancellationToken.None).ConfigureAwait(false);
					if (bytes is null)
					{
						await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => placeholder.Text = "图片已被删除");
						return;
					}
					// 解码放在后台线程，避免大图卡住界面。
					Avalonia.Media.Imaging.Bitmap bitmap = await Task.Run(() =>
					{
						using MemoryStream stream = new(bytes);
						return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, (int)(ImageMaxSize * 2));
					}).ConfigureAwait(false);
					CacheImage(name, bitmap);
					await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Show(bitmap));
				}
				catch (Exception)
				{
					await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => placeholder.Text = "图片无法显示");
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
