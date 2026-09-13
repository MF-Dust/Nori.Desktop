using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Nori.Desktop.Windows;

public sealed partial class MemoryWindow
{
	private TextBlock Secondary(string text, double size = 12)
	{
		TextBlock block = Text(text, size);
		SetBrush(block, TextBlock.ForegroundProperty, "SettingsSecondaryBrush");
		return block;
	}

	private TextBlock LocalText(Func<string> value, double size = 12, bool bold = false)
	{
		TextBlock block = Text(value(), size, bold);
		var reference = new WeakReference<TextBlock>(block);
		_localize.Add(() => { if (reference.TryGetTarget(out TextBlock? target)) target.Text = value(); });
		return block;
	}

	private Border Badge(string text, bool accent = false)
	{
		TextBlock label = Text(text, 11.5, accent);
		SetBrush(label, TextBlock.ForegroundProperty, accent ? "SettingsAccentBrush" : "SettingsSecondaryBrush");
		var badge = new Border { Child = label, Padding = new Thickness(8, 3), CornerRadius = new CornerRadius(5) };
		SetBrush(badge, Border.BackgroundProperty, accent ? "SettingsSelectionBrush" : "SettingsInputBrush");
		return badge;
	}

	private Control InfoLine(string key, string value)
	{
		var row = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), ColumnSpacing = 16, Margin = new Thickness(0, 4) };
		TextBlock label = Label(key, 12);
		SetBrush(label, TextBlock.ForegroundProperty, "SettingsSecondaryBrush");
		TextBlock content = Text(value, 13);
		row.Children.Add(label); Grid.SetColumn(content, 1); row.Children.Add(content);
		return row;
	}

	private Control SettingLine(string key, Control editor, Control? description = null, Control? state = null, Control? retry = null)
	{
		var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
		labels.Children.Add(Label(key, 13, true));
		if (description is not null) labels.Children.Add(description);
		if (state is not null) labels.Children.Add(state);
		var editors = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
		editors.Children.Add(editor);
		if (retry is not null) editors.Children.Add(retry);
		var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 20, Margin = new Thickness(0, 12) };
		row.Children.Add(labels); Grid.SetColumn(editors, 1); row.Children.Add(editors);
		var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
		SetBrush(border, Border.BorderBrushProperty, "SettingsBorderBrush");
		return border;
	}

	private Control SettingHint(string key)
	{
		var hint = LocalText(() => key switch
		{
			"enabled" => T("让 Nori 记住日常对话中的重要内容。", "Keep meaningful details from everyday conversations."),
			"reflectionEnabled" => T("从对话中整理偏好、事实和约定。", "Reflect on conversations to retain preferences, facts and plans."),
			"decayEnabled" => T("随时间降低不常使用的记忆权重。", "Reduce the weight of memories used less often."),
			"archiveEnabled" => T("将低权重记忆移入归档，之后仍可恢复。", "Archive low-weight memories while keeping them recoverable."),
			"reflectionRounds" => T("积累多少轮对话后开始整理。", "Conversation rounds before reflection starts."),
			"reflectionMinChars" => T("达到此字符数后触发整理。", "Minimum accumulated text for reflection."),
			"recallTopK" => T("每次对话最多注入的个人记忆数。", "Maximum personal memories included per conversation."),
			"keywordTopK" => T("关键词检索保留的候选数量。", "Number of candidates from keyword search."),
			"vectorTopK" => T("语义检索保留的候选数量。", "Number of candidates from semantic search."),
			"rrfK" => T("调节多路检索结果的排名融合。", "Control ranking fusion across retrieval methods."),
			"minSimilarity" => T("低于此相似度的结果不会参与召回。", "Exclude results below this similarity threshold."),
			"archiveThreshold" => T("记忆权重低于此值时可自动归档。", "Allow automatic archiving below this memory weight."),
			"knowledgeEnabled" => T("将 Memory.md 知识用于对话。", "Use knowledge from Memory.md in conversations."),
			"knowledgeWatch" => T("文件变化时自动更新知识索引。", "Update the knowledge index when files change."),
			"debugRetrieval" => T("保留完整检索诊断信息以便排查。", "Keep detailed retrieval diagnostics for troubleshooting."),
			_ => "",
		});
		SetBrush(hint, TextBlock.ForegroundProperty, "SettingsSecondaryBrush");
		return hint;
	}
}
