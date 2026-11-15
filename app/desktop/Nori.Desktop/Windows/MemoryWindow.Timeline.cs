using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.Memory;
using Nori.Desktop.Appearance;

namespace Nori.Desktop.Windows;

public sealed partial class MemoryWindow
{
	private TimelineState? _timeline;
	private Grid? _timelineRoot;
	private CancellationTokenSource? _timelineQuery;
	private CancellationTokenSource? _timelineDetailQuery;

	private Control BuildTimelinePage()
	{
		var search = Input();
		search.Name = "MemoryTimelineSearch";
		search.MaxLength = MemoryTimelineQuery.MaxKeywordLength;
		var from = Input();
		from.Name = "MemoryTimelineFrom";
		from.Width = 148;
		from.MaxLength = 10;
		var to = Input();
		to.Name = "MemoryTimelineTo";
		to.Width = 148;
		to.MaxLength = 10;
		var kind = Choice(["", .. Kinds], value => value.Length == 0 ? L("list.allKinds") : Kind(value));
		kind.Name = "MemoryTimelineKind";
		var activity = Choice(["", "active", "archived"], value => value switch
		{
			"active" => L("timeline.active"),
			"archived" => L("timeline.archived"),
			_ => L("timeline.allActivity"),
		});
		activity.Name = "MemoryTimelineActivity";
		var error = Text("", NoriMetrics.FontXs);
		error.Name = "MemoryTimelineError";
		SetBrush(error, TextBlock.ForegroundProperty, "SettingsErrorBrush");
		var count = Text("", NoriMetrics.FontLg, true);
		count.Name = "MemoryTimelineCount";
		var pageLabel = Text("1 / 1");
		pageLabel.Name = "MemoryTimelinePage";
		// 时间线本身已分页；当前页行数有界，用普通堆叠面板保证行内按钮可聚焦，避免嵌套滚动里虚拟化不生成容器。
		var rows = new ListBox
		{
			Name = "MemoryTimelineList",
			ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel()),
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0),
		};
		ScrollViewer.SetHorizontalScrollBarVisibility(rows, ScrollBarVisibility.Disabled);
		ScrollViewer.SetVerticalScrollBarVisibility(rows, ScrollBarVisibility.Auto);
		rows.ItemTemplate = new FuncDataTemplate<TimelineRow>((row, _) => row is null ? new Border() : TimelineRowView(row));
		rows.KeyDown += (_, args) =>
		{
			if (args.Key != Key.Enter || rows.SelectedItem is not TimelineRow selected || selected.Header || selected.Id <= 0) return;
			args.Handled = true;
			_ = OpenTimelineDetailAsync(selected.Id);
		};
		var previous = Button("list.previous", async () => { if (_timeline is null) return; _timeline.Page--; await RefreshPageAsync(); }, enabled: () => _timeline?.Page > 0);
		var next = Button("list.next", async () => { if (_timeline is null) return; _timeline.Page++; await RefreshPageAsync(); }, enabled: () => _timeline is { } state && (state.Page + 1) * MemoryTimelineQuery.DefaultLimit < state.Total);
		previous.Name = "MemoryTimelinePrevious";
		next.Name = "MemoryTimelineNext";
		_timeline = new TimelineState
		{
			Search = search, From = from, To = to, Kind = kind, Activity = activity,
			Count = count, PageLabel = pageLabel, Error = error, Previous = previous, Next = next, Rows = rows,
		};
		_localize.Add(() =>
		{
			search.PlaceholderText = L("timeline.search");
			from.PlaceholderText = "yyyy-MM-dd";
			to.PlaceholderText = "yyyy-MM-dd";
			AutomationProperties.SetName(search, L("timeline.search"));
			AutomationProperties.SetName(from, L("timeline.from"));
			AutomationProperties.SetName(to, L("timeline.to"));
			AutomationProperties.SetName(kind, L("list.kind"));
			AutomationProperties.SetName(activity, L("list.status"));
			AutomationProperties.SetName(rows, L("tabs.timeline"));
		});
		search.TextChanged += async (_, _) => await DelayTimelineRefreshAsync();
		from.TextChanged += async (_, _) => await DelayTimelineRefreshAsync();
		to.TextChanged += async (_, _) => await DelayTimelineRefreshAsync();
		async void FilterChanged(object? sender, SelectionChangedEventArgs args)
		{
			if (_timeline is null) return;
			_timeline.Page = 0;
			if (_section == "timeline") await RefreshPageAsync();
		}
		kind.SelectionChanged += FilterChanged;
		activity.SelectionChanged += FilterChanged;
		search.KeyDown += async (_, args) =>
		{
			if (args.Key != Key.Enter || _timeline is null) return;
			_timeline.SearchDelay?.Cancel();
			_timeline.Page = 0;
			if (_section == "timeline") await RefreshPageAsync();
		};
		var notice = Label("timeline.notice", NoriMetrics.FontXs);
		notice.Name = "MemoryTimelineNotice";
		var hint = Label("timeline.dateHint", NoriMetrics.FontXs);
		SetBrush(hint, TextBlock.ForegroundProperty, "SettingsSecondaryBrush");
		var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
		root.Name = "MemoryTimelineRoot";
		var filters = Stack(notice, hint, search, Row(Field("timeline.from", from), Field("timeline.to", to)), Row(kind, activity), error, count);
		root.Children.Add(filters);
		Grid.SetRow(rows, 1);
		rows.Margin = new Thickness(0, 4, 0, 4);
		root.Children.Add(rows);
		var pager = Row(previous, pageLabel, next);
		Grid.SetRow(pager, 2);
		root.Children.Add(pager);
		_timelineRoot = root;
		return root;
	}

	private async Task DelayTimelineRefreshAsync()
	{
		if (_timeline is not { } state) return;
		state.SearchDelay?.Cancel();
		using var delay = new CancellationTokenSource();
		state.SearchDelay = delay;
		_pageRequest++;
		_timelineQuery?.Cancel();
		try
		{
			await Task.Delay(300, delay.Token);
			state.Page = 0;
			if (_section == "timeline") await RefreshPageAsync();
		}
		catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
		finally { if (ReferenceEquals(state.SearchDelay, delay)) state.SearchDelay = null; }
	}

	private async Task<bool> RefreshTimelineAsync(long request)
	{
		if (_timeline is not { } state) return false;
		bool Stale() => request != _pageRequest || _section != "timeline" || _prepared || _preparing;
		MemoryTimelineQuery? query = null;
		bool invalid = false;
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			if (Stale() || !IsVisible) return;
			if (BuildTimelineQuery() is not MemoryTimelineQuery built)
			{
				_timelineQuery?.Cancel();
				state.Total = 0;
				state.Rows.ItemsSource = Array.Empty<TimelineRow>();
				invalid = true;
				return;
			}
			query = built;
		});
		if (invalid || query is null || Stale()) return false;
		CancellationToken token = RestartTimelineQuery();
		MemoryTimelinePage page = await _service.QueryTimelineAsync(query, TimeZoneInfo.Local, token).ConfigureAwait(false);
		if (token.IsCancellationRequested || Stale()) return false;
		return await Dispatcher.UIThread.InvokeAsync(async () =>
		{
			if (token.IsCancellationRequested || Stale() || !IsVisible) return false;
			int size = Math.Max(1, page.Limit);
			if (state.Page > 0 && state.Page * size >= page.Total)
			{
				state.Page = Math.Max(0, (page.Total - 1) / Math.Max(1, size));
				await RefreshPageAsync();
				return false;
			}
			state.Total = page.Total;
			state.Count.Text = $"{L("timeline.page")} ({page.Total})";
			state.PageLabel.Text = $"{state.Page + 1} / {Math.Max(1, (page.Total + size - 1) / size)}";
			state.Previous.IsEnabled = state.Page > 0;
			state.Next.IsEnabled = (state.Page + 1) * size < page.Total;
			state.Error.Text = "";
			state.Rows.ItemsSource = BuildTimelineRows(page);
			FitTimeline();
			return true;
		});
	}

	private MemoryTimelineQuery? BuildTimelineQuery()
	{
		if (_timeline is not { } state) return null;
		if (!TryTimelineDate(state.From.Text, out DateOnly? from) || !TryTimelineDate(state.To.Text, out DateOnly? to))
		{
			state.Error.Text = L("timeline.dateInvalid");
			return null;
		}
		if (from is { } start && to is { } end && start > end)
		{
			state.Error.Text = L("timeline.rangeInvalid");
			return null;
		}
		string activity = Selected(state.Activity);
		return new MemoryTimelineQuery
		{
			Keyword = state.Search.Text,
			Kind = EmptyToNull(Selected(state.Kind)),
			Activity = activity == "archived" ? MemoryTimelineActivity.Archived : activity == "active" ? MemoryTimelineActivity.Active : MemoryTimelineActivity.All,
			From = from,
			To = to,
			Limit = MemoryTimelineQuery.DefaultLimit,
			Offset = state.Page * MemoryTimelineQuery.DefaultLimit,
		};
	}

	private List<TimelineRow> BuildTimelineRows(MemoryTimelinePage page)
	{
		List<TimelineRow> rows = [];
		string? previous = null;
		foreach (MemoryTimelineEntry entry in page.Items)
		{
			string header = entry.Created.Known && entry.Created.Date is { } date ? FormatTimelineDate(date) : L("timeline.unknownDate");
			if (!string.Equals(previous, header, StringComparison.Ordinal))
			{
				rows.Add(new TimelineRow { Header = true, Text = header });
				previous = header;
			}
			MemoryItem item = entry.Item;
			string summary = TimelineText(item.CanonicalSummary);
			if (summary.Length == 0) summary = TimelineText(item.Content);
			if (summary.Length == 0) summary = L("timeline.noSummary");
			if (summary.Length > 180) summary = summary[..180] + "…";
			string status = item.Status ?? "";
			string kindLabel = string.IsNullOrWhiteSpace(item.Kind) ? L("timeline.unknownValue") : Kind(item.Kind);
			string statusLabel = string.IsNullOrWhiteSpace(status) ? L("timeline.unknownValue") : Status(status);
			rows.Add(new TimelineRow
			{
				Id = item.Id,
				Text = summary,
				Meta = $"{kindLabel} · {statusLabel} · #{item.Id}",
				Status = status,
				Name = $"{summary} · {header} · {statusLabel}",
			});
		}
		if (rows.Count == 0) rows.Add(new TimelineRow { Header = true, Text = L("timeline.empty") });
		return rows;
	}

	private Control TimelineRowView(TimelineRow row)
	{
		if (row.Header)
		{
			TextBlock header = Text(row.Text, NoriMetrics.FontSm, true);
			header.Margin = new Thickness(2, 8, 0, 2);
			SetBrush(header, TextBlock.ForegroundProperty, "SettingsAccentBrush");
			AutomationProperties.SetName(header, row.Text);
			return header;
		}
		var summary = Text(row.Text, NoriMetrics.FontBase);
		summary.TextWrapping = TextWrapping.Wrap;
		summary.MaxHeight = 42;
		summary.TextTrimming = TextTrimming.CharacterEllipsis;
		var meta = Secondary(row.Meta);
		var open = new Button { Content = L("timeline.detail"), MinHeight = 28, Padding = new Thickness(10, 4), Tag = row.Id };
		open.Name = "MemoryTimelineOpen_" + row.Id.ToString(CultureInfo.InvariantCulture);
		string openLabel = L("timeline.detail");
		AutomationProperties.SetName(open, openLabel + " " + row.Name);
		open.Click += async (_, _) => await OpenTimelineDetailAsync(row.Id);
		string operation = row.Status == "archived" ? "restore" : "archive";
		string archiveLabel = L(operation == "restore" ? "archive.restore" : "list.archiveThis");
		var archive = new Button
		{
			Content = archiveLabel,
			MinHeight = 28,
			Padding = new Thickness(10, 4),
			Tag = row.Id,
		};
		archive.Name = "MemoryTimelineArchive_" + row.Id.ToString(CultureInfo.InvariantCulture);
		AutomationProperties.SetName(archive, archiveLabel + " " + row.Name);
		archive.Click += async (_, _) => await ChangeMemoryAsync(row.Id, operation);
		var actions = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
		actions.Children.Add(open);
		actions.Children.Add(archive);
		var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
		body.Children.Add(Stack(summary, meta));
		Grid.SetColumn(actions, 1);
		body.Children.Add(actions);
		var card = new Border
		{
			Child = body,
			Padding = new Thickness(12, 8),
			Margin = new Thickness(0, 0, 0, 6),
			CornerRadius = new CornerRadius(NoriMetrics.RadiusMd),
			BorderThickness = new Thickness(1),
		};
		SetBrush(card, Border.BackgroundProperty, "SettingsCardBrush");
		SetBrush(card, Border.BorderBrushProperty, "SettingsBorderBrush");
		AutomationProperties.SetName(card, row.Name);
		return card;
	}

	internal async Task OpenTimelineDetailAsync(long id)
	{
		long request = ++_detailRequest;
		CancellationToken token = RestartTimelineDetailQuery();
		MemoryTimelineDetail detail = await _service.GetTimelineDetailAsync(id, TimeZoneInfo.Local, token).ConfigureAwait(false);
		if (request != _detailRequest || token.IsCancellationRequested || _section != "timeline") return;
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			if (request != _detailRequest || !IsVisible || token.IsCancellationRequested || _section != "timeline") return;
			OpenTimelineDetailDialog(id, detail);
		});
	}

	private void OpenTimelineDetailDialog(long id, MemoryTimelineDetail detail)
	{
		MemoryItem item = detail.Item;
		var dialog = Dialog(L("detail.title") + " #" + id, 560, 620);
		dialog.Name = "MemoryTimelineDetail";
		List<Action> translations = [];
		void Translate(TextBlock target, Func<string> value) => translations.Add(() => target.Text = value());
		TextBlock Line(Func<string> value, double size = NoriMetrics.FontBase, bool bold = false)
		{
			TextBlock text = Text(value(), size, bold);
			Translate(text, value);
			return text;
		}
		var body = Stack(Line(() => L("timeline.notice"), NoriMetrics.FontXs));
		SetBrush((TextBlock)body.Children[0], TextBlock.ForegroundProperty, "SettingsSecondaryBrush");
		string content = TimelineText(item.Content);
		body.Children.Add(Card("detail.content", Text(content.Length == 0 ? L("timeline.noContent") : content, NoriMetrics.FontMd)));
		string summary = TimelineText(item.CanonicalSummary);
		body.Children.Add(Card("detail.canonical", Text(summary.Length == 0 ? L("timeline.noSummary") : summary)));
		body.Children.Add(Card("",
			Line(() => L("detail.createdAt") + ": " + FormatStamp(detail.Created), NoriMetrics.FontXs),
			Line(() => L("detail.updatedAt") + ": " + FormatStamp(detail.Updated), NoriMetrics.FontXs),
			Line(() => L("detail.kind") + ": " + (string.IsNullOrWhiteSpace(item.Kind) ? L("timeline.unknownValue") : Kind(item.Kind)), NoriMetrics.FontXs),
			Line(() => L("detail.status") + ": " + (string.IsNullOrWhiteSpace(item.Status) ? L("timeline.unknownValue") : Status(item.Status)), NoriMetrics.FontXs),
			Line(() => L("detail.source") + ": " + (string.IsNullOrWhiteSpace(item.Source) ? L("timeline.unknownValue") : Source(item.Source)), NoriMetrics.FontXs)));
		var sources = Stack();
		if (detail.Sources.Count == 0) sources.Children.Add(Line(() => L("timeline.noSources")));
		foreach (MemorySource source in detail.Sources)
		{
			MemoryCreatedStamp when = MemoryTimelineTime.Classify(source.MessageTime, TimeZoneInfo.Local);
			sources.Children.Add(Card("",
				Text($"{source.Role} · #{source.Sequence} · {(when.Known ? FormatStamp(when) : L("timeline.unknownValue"))}", NoriMetrics.FontXs, true),
				Text(string.IsNullOrWhiteSpace(source.Content) ? L("timeline.noContent") : source.Content)));
		}
		body.Children.Add(Card("detail.sources", sources));
		var related = Stack();
		if (detail.Related.Count == 0) related.Children.Add(Line(() => L("timeline.noRelated")));
		foreach (MemoryTimelineRelation relation in detail.Related)
		{
			string link = relation.Link == "supersedes" ? L("timeline.supersedes") : L("timeline.supersededBy");
			if (!relation.Resolved)
			{
				related.Children.Add(Text($"#{relation.Id} · {link} · {L("timeline.missingRelated")}"));
				continue;
			}
			string relatedText = TimelineText(relation.CanonicalSummary);
			if (relatedText.Length == 0) relatedText = TimelineText(relation.Content);
			if (relatedText.Length == 0) relatedText = L("timeline.noContent");
			MemoryCreatedStamp relatedCreated = MemoryTimelineTime.Classify(relation.CreatedAt, TimeZoneInfo.Local);
			related.Children.Add(Card("",
				Text($"#{relation.Id} · {link}", NoriMetrics.FontXs, true),
				Text(relatedText),
				Secondary($"{(string.IsNullOrWhiteSpace(relation.Kind) ? L("timeline.unknownValue") : Kind(relation.Kind))} · {(string.IsNullOrWhiteSpace(relation.Status) ? L("timeline.unknownValue") : Status(relation.Status))} · {(relatedCreated.Known ? FormatStamp(relatedCreated) : L("timeline.unknownValue"))}")));
		}
		if (detail.RelatedTruncated) related.Children.Add(Line(() => L("timeline.relatedTruncated"), NoriMetrics.FontXs));
		body.Children.Add(Card("timeline.related", related));
		string operation = item.Status == "archived" ? "restore" : "archive";
		var buttons = Row(
			Button(operation == "restore" ? "archive.restore" : "list.archiveThis", async () =>
			{
				if (await ChangeMemoryAsync(id, operation, dialog)) dialog.Close();
			}),
			Button("timeline.edit", async () =>
			{
				dialog.Close();
				await OpenEditorAsync(id);
			}),
			Button("common.close", () => { dialog.Close(); return Task.CompletedTask; }));
		foreach (Control button in buttons.Children) button.Margin = new Thickness(0, 0, 8, 8);
		buttons.HorizontalAlignment = HorizontalAlignment.Right;
		var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(20) };
		grid.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
		var footer = new Border { Child = buttons, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14, 0, 0), Margin = new Thickness(0, 14, 0, 0) };
		SetBrush(footer, Border.BorderBrushProperty, "SettingsBorderBrush");
		Grid.SetRow(footer, 1);
		grid.Children.Add(footer);
		dialog.Content = grid;
		NativeWindowChrome.Attach(dialog, () => UiLanguage.IsEnglish(_language));
		_ = dialog.ShowDialog(this);
	}


	private void FitTimeline()
	{
		if (_timelineRoot is null || _section != "timeline" || _scroll.Viewport.Height <= 1) return;
		_timelineRoot.Height = Math.Max(160, _scroll.Viewport.Height);
	}

	private void CancelTimelineQuery()
	{
		_timelineQuery?.Cancel();
		_timelineDetailQuery?.Cancel();
	}

	private CancellationToken RestartTimelineQuery()
	{
		_timelineQuery?.Cancel();
		if (_lifetime.IsCancellationRequested) return _lifetime.Token;
		_timelineQuery = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
		return _timelineQuery.Token;
	}

	/// <summary>测试用：返回当前时间线页条数，未加载则为 -1。</summary>
	internal int TimelineTotalForTests => _timeline?.Total ?? -1;

	private CancellationToken RestartTimelineDetailQuery()
	{
		_timelineDetailQuery?.Cancel();
		if (_lifetime.IsCancellationRequested) return _lifetime.Token;
		_timelineDetailQuery = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
		return _timelineDetailQuery.Token;
	}

	private static bool TryTimelineDate(string? text, out DateOnly? date)
	{
		date = null;
		string value = text?.Trim() ?? "";
		if (value.Length == 0) return true;
		if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed)) return false;
		date = parsed;
		return true;
	}

	private string FormatTimelineDate(DateOnly date) => UiLanguage.IsEnglish(_language)
		? date.ToString("MMMM d, yyyy", CultureInfo.GetCultureInfo("en-US"))
		: string.Create(CultureInfo.InvariantCulture, $"{date.Year}年{date.Month}月{date.Day}日");

	private string FormatStamp(MemoryCreatedStamp stamp)
	{
		if (!stamp.Known || stamp.Date is not { } date) return L("timeline.unknownValue");
		if (!stamp.HasTime || stamp.Instant is null) return FormatTimelineDate(date) + T("（只有日期）", " (date only)");
		DateTimeOffset local = TimeZoneInfo.ConvertTime(stamp.Instant.Value, TimeZoneInfo.Local);
		return local.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
	}

	private static string TimelineText(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

	private sealed class TimelineState
	{
		public int Page;
		public int Total;
		public required TextBox Search;
		public required TextBox From;
		public required TextBox To;
		public required ComboBox Kind;
		public required ComboBox Activity;
		public required TextBlock Count;
		public required TextBlock PageLabel;
		public required TextBlock Error;
		public required Button Previous;
		public required Button Next;
		public required ListBox Rows;
		public CancellationTokenSource? SearchDelay;
	}

	private sealed class TimelineRow
	{
		public bool Header { get; init; }
		public long Id { get; init; }
		public string Text { get; init; } = "";
		public string Meta { get; init; } = "";
		public string Status { get; init; } = "";
		public string Name { get; init; } = "";
	}
}
