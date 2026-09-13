using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Nori.Desktop.Memory;

namespace Nori.Desktop.Windows;

public sealed partial class MemoryWindow
{
	private readonly Dictionary<string, StackPanel> _results = [];
	private readonly Dictionary<string, ListState> _lists = [];
	private TextBox _debugQuery = null!;
	private UniformGrid? _overviewStats;

	internal string CurrentSection => _section;
	internal Control PageContent => _pages[_section];
	internal void ApplySnapshotForTesting(JsonElement snapshot)
	{
		_snapshot = snapshot;
		_applying = true;
		try { foreach (var bind in _snapshotBindings) bind(P(snapshot, "memory")); }
		finally { _applying = false; }
	}

	private void BuildPages()
	{
		var overview = Stack();
		_snapshotBindings.Add(memory =>
		{
			overview.Children.Clear();
			var stats = new UniformGrid { Columns = _scroll.Viewport.Width >= 600 ? 4 : 2 };
			_overviewStats = stats;
			foreach (var (key, label) in new[] { ("active", "overview.active"), ("atoms", "overview.atoms"), ("archived", "overview.archived"), ("knowledgeChunks", "overview.knowledge") })
			{
				var value = Text(N(memory, key).ToString("0"), 30, true);
				SetBrush(value, TextBlock.ForegroundProperty, "SettingsAccentBrush");
				var tile = new Border { Child = Stack(Secondary(L(label)), value), Padding = new Thickness(14), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 8, 8) };
				SetBrush(tile, Border.BackgroundProperty, "SettingsInputBrush"); stats.Children.Add(tile);
			}
			overview.Children.Add(stats);
			overview.Children.Add(Secondary($"{L("overview.index")}: {S(memory, "indexState")} · {N(memory, "indexProcessed")} / {N(memory, "indexTotal")}"));
		});
		_pages["overview"] = Stack(Card("overview.title", overview), Card("",
			SettingToggle("enabled", "header.title", true), SettingToggle("reflectionEnabled", "overview.reflection", true),
			SettingToggle("decayEnabled", "overview.decay", true), SettingToggle("archiveEnabled", "overview.archive", true)));
		_pages["memories"] = BuildListPage("memories");
		_pages["archive"] = BuildListPage("archive");
		_results["atoms"] = Stack();
		_pages["atoms"] = Stack(Row(Button("list.retryLoad", RefreshPageAsync)), _results["atoms"]);
		_results["knowledge"] = Stack();
		_pages["knowledge"] = Stack(Card("knowledge.title", Row(
			Button("knowledge.open", async () => { await _service.ExecuteAsync("memory_knowledge_open", null, _lifetime.Token); Success(); }),
			Button("knowledge.reindex", async () => { await _service.ExecuteAsync("memory_knowledge_reindex", null, _lifetime.Token); await RefreshAsync(); Success(); })),
			_results["knowledge"]));
		_debugQuery = Input(); _debugQuery.Name = "MemoryDebugQuery";
		_localize.Add(() => _debugQuery.PlaceholderText = L("debugger.placeholder"));
		_results["debugger"] = Stack(Label("debugger.empty"));
		var queryRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
		queryRow.Children.Add(_debugQuery);
		var runDebug = Button("debugger.run", RunDebuggerAsync); runDebug.Classes.Add("accent"); Grid.SetColumn(runDebug, 1); queryRow.Children.Add(runDebug);
		_pages["debugger"] = Stack(Card("", queryRow), _results["debugger"]);
		_debugQuery.KeyDown += async (_, args) =>
		{
			if (args.Key != Avalonia.Input.Key.Enter || _debugRunning) return;
			Task task = RunActionAsync(RunDebuggerAsync); _operations.Add(task);
			try { await task; } finally { _operations.Remove(task); }
		};
		_pages["transfer"] = BuildTransferPage();
		var reflection = new StackPanel { Spacing = 0 };
		var retrieval = new StackPanel { Spacing = 0 };
		var retention = new StackPanel { Spacing = 0 };
		foreach (var field in new (string Key, decimal Min, decimal Max, decimal Step, decimal Default)[]
		{
			("reflectionRounds", 1, 32, 1, 8), ("reflectionMinChars", 100, 20000, 100, 2500),
			("recallTopK", 1, 20, 1, 6), ("keywordTopK", 1, 100, 1, 20), ("vectorTopK", 1, 100, 1, 20),
			("rrfK", 1, 500, 1, 60), ("minSimilarity", 0, 1, 0.01m, 0.25m), ("archiveThreshold", 0, 1, 0.01m, 0.15m),
		})
		{
			var control = SettingNumber(field.Key, field.Min, field.Max, field.Step, field.Default);
			(field.Key.StartsWith("reflection", StringComparison.Ordinal) ? reflection : field.Key == "archiveThreshold" ? retention : retrieval).Children.Add(control);
		}
		_pages["advanced"] = Stack(Card("embedding.vectorRebuild", Label("embedding.vectorRebuildDesc"),
			Button("embedding.reembed", async () =>
			{
				_status.Text = L("embedding.reembedRunning");
				JsonElement count = await _service.ExecuteAsync("memory_reembed_all", null, _lifetime.Token);
				await RefreshAsync(); Success(L("embedding.reembedDonePrefix") + count + L("embedding.reembedDoneSuffix"));
			})), Card("overview.reflection", reflection), Card("debugger.title", retrieval), Card("overview.archive", retention), Card("knowledge.title",
			SettingToggle("knowledgeEnabled", "advanced.knowledge", true), SettingToggle("knowledgeWatch", "advanced.watch", true), SettingToggle("debugRetrieval", "advanced.debug", false)));
		foreach (var pair in _pages) pair.Value.Name = "MemoryPage_" + pair.Key;
	}

	private MemorySettingDraft Draft(string key, object initial, TextBlock state)
	{
		MemorySettingDraft? draft = null;
		draft = new MemorySettingDraft(initial, async value =>
		{
			object payloadValue = value;
			if (key is "reflectionRounds" or "reflectionMinChars" or "recallTopK" or "keywordTopK" or "vectorTopK" or "rrfK")
			{
				decimal number = Convert.ToDecimal(value);
				if (decimal.Truncate(number) != number) throw new InvalidOperationException(T("请输入整数", "Enter a whole number"));
				payloadValue = decimal.ToInt32(number);
			}
			_stateRevision++;
			await _service.ExecuteAsync("memory_update_settings", new { settings = new Dictionary<string, object> { [key] = payloadValue } }, _lifetime.Token);
			_stateRevision++;
			QueueRefresh();
		}, () =>
		{
			if (draft is null) return;
			state.Text = draft.Error.Length > 0 ? L("toast.saveFailed") + ": " + draft.Error : draft.Saving ? L("detail.saving") : draft.Dirty ? T("待保存", "Unsaved") : T("已自动保存", "Saved automatically");
			SetBrush(state, TextBlock.ForegroundProperty, draft.Error.Length > 0 ? "SettingsErrorBrush" : "SettingsSecondaryBrush");
		});
		_drafts[key] = draft;
		return draft;
	}

	private Control SettingToggle(string key, string label, bool initial)
	{
		var status = Text("", 12);
		var toggle = new ToggleSwitch { IsChecked = initial, Name = "MemorySetting_" + key, HorizontalAlignment = HorizontalAlignment.Right };
		_localize.Add(() => { toggle.OnContent = L("overview.enabled"); toggle.OffContent = L("overview.disabled"); });
		MemorySettingDraft draft = Draft(key, initial, status);
		var retry = Button("detail.retry", () => RetryDraftAsync(draft));
		retry.IsVisible = false;
		status.IsVisible = false;
		status.PropertyChanged += (_, args) => { if (args.Property == TextBlock.TextProperty) { retry.IsVisible = draft.Error.Length > 0; status.IsVisible = draft.Dirty || draft.Saving || draft.Error.Length > 0; } };
		toggle.IsCheckedChanged += (_, _) => { if (!_applying) draft.Set(toggle.IsChecked == true); };
		_snapshotBindings.Add(memory =>
		{
			if (P(memory, key).ValueKind == JsonValueKind.Undefined) return;
			draft.AcceptSnapshot(B(memory, key));
			if (!toggle.IsKeyboardFocusWithin) toggle.IsChecked = (bool)draft.Value;
		});
		return SettingLine(label, toggle, SettingHint(key), status, retry);
	}

	private Control SettingNumber(string key, decimal min, decimal max, decimal step, decimal initial)
	{
		var status = Text("", 12);
		var input = new NumericUpDown { Minimum = min, Maximum = max, Increment = step, Value = initial, Name = "MemorySetting_" + key, Width = 128, HorizontalAlignment = HorizontalAlignment.Right, FormatString = step < 1 ? "0.00" : "0.################" };
		MemorySettingDraft draft = Draft(key, initial, status);
		var retry = Button("detail.retry", () => RetryDraftAsync(draft)); retry.IsVisible = false;
		status.IsVisible = false;
		status.PropertyChanged += (_, args) => { if (args.Property == TextBlock.TextProperty) { retry.IsVisible = draft.Error.Length > 0; status.IsVisible = draft.Dirty || draft.Saving || draft.Error.Length > 0; } };
		input.ValueChanged += (_, _) =>
		{
			if (_applying || !input.Value.HasValue) return;
			// 原样保留草稿，整数字段在提交前校验并转换 JSON 编码。
			draft.Set(input.Value.Value);
		};
		input.LostFocus += async (_, _) => { if (!input.IsKeyboardFocusWithin) await draft.FlushAsync(); };
		_snapshotBindings.Add(memory =>
		{
			if (P(memory, key).ValueKind == JsonValueKind.Undefined) return;
			draft.AcceptSnapshot((decimal)N(memory, key));
			if (!input.IsKeyboardFocusWithin) input.Value = Convert.ToDecimal(draft.Value);
		});
		return SettingLine("advanced." + key, input, SettingHint(key), status, retry);
	}

	private async Task RetryDraftAsync(MemorySettingDraft draft)
	{
		if (!await draft.FlushAsync()) throw new InvalidOperationException(L("toast.saveFailed") + ": " + draft.Error);
		Success();
	}

	private sealed class ListState
	{
		public int Page;
		public int Total;
		public required TextBox Search;
		public required ComboBox Kind;
		public required ComboBox Status;
		public required StackPanel Results;
		public required TextBlock Count;
		public required TextBlock PageLabel;
		public required Button Previous;
		public required Button Next;
		public CancellationTokenSource? SearchDelay;
	}

	private ComboBox Choice(string[] values, Func<string, string> translate, int selected = 0)
	{
		var combo = new ComboBox { MinWidth = 145, HorizontalAlignment = HorizontalAlignment.Stretch };
		foreach (string value in values)
		{
			var item = new ComboBoxItem { Tag = value, Content = translate(value) };
			var reference = new WeakReference<ComboBoxItem>(item);
			_localize.Add(() => { if (reference.TryGetTarget(out ComboBoxItem? target)) target.Content = translate(value); });
			combo.Items.Add(item);
		}
		combo.SelectedIndex = selected;
		return combo;
	}
	private static string Selected(ComboBox choice) => (choice.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

	private Control BuildListPage(string section)
	{
		var search = Input(); search.Name = "MemorySearch_" + section;
		_localize.Add(() => search.PlaceholderText = L("list.searchPlaceholder"));
		var kind = Choice(["", .. Kinds], value => value.Length == 0 ? L("list.allKinds") : Kind(value));
		var status = Choice(["", "active", "dormant", "expired"], value => value.Length == 0 ? L("list.allStatuses") : Status(value));
		kind.Name = "MemoryKindFilter_" + section; status.Name = "MemoryStatusFilter_" + section;
		var results = Stack();
		var count = Text("", 16, true);
		var pageLabel = Text("1 / 1");
		var previous = Button("list.previous", async () => { _lists[section].Page--; await RefreshPageAsync(); }, enabled: () => _lists[section].Page > 0);
		var next = Button("list.next", async () => { _lists[section].Page++; await RefreshPageAsync(); }, enabled: () => (_lists[section].Page + 1) * 20 < _lists[section].Total);
		previous.Name = "MemoryPrevious_" + section; next.Name = "MemoryNext_" + section;
		_lists[section] = new ListState { Search = search, Kind = kind, Status = status, Results = results, Count = count, PageLabel = pageLabel, Previous = previous, Next = next };
		search.TextChanged += async (_, _) =>
		{
			ListState state = _lists[section]; state.SearchDelay?.Cancel();
			using var delay = new CancellationTokenSource(); state.SearchDelay = delay;
			_pageRequest++;
			try { await Task.Delay(300, delay.Token); state.Page = 0; if (_section == section) await RefreshPageAsync(); }
			catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
			finally { if (ReferenceEquals(state.SearchDelay, delay)) state.SearchDelay = null; }
		};
		async void FilterChanged(object? sender, SelectionChangedEventArgs args) { _lists[section].Page = 0; if (_section == section) await RefreshPageAsync(); }
		kind.SelectionChanged += FilterChanged; status.SelectionChanged += FilterChanged;
		var filters = section == "memories" ? Row(kind, status) : Row(kind);
		var clearSearch = new Button { MinHeight = 32, Name = "MemoryClearSearch_" + section };
		_localize.Add(() => clearSearch.Content = T("清空搜索", "Clear search"));
		clearSearch.Click += (_, _) => search.Text = "";
		var actions = Row(Button("list.retryLoad", RefreshPageAsync), clearSearch);
		if (section == "memories") actions.Children.Add(Button("list.clearAll", ClearAllAsync, true));
		var body = Stack();
		var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
		count.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(count);
		if (section == "memories")
		{
			Button add = Button("add.title", () => OpenEditorAsync(null)); add.Name = "MemoryAdd";
			add.Classes.Add("accent");
			Grid.SetColumn(add, 1); heading.Children.Add(add);
		}
		body.Children.Add(Card("", heading, search, filters, actions));
		body.Children.Add(results);
		body.Children.Add(Row(previous, pageLabel, next));
		return body;
	}

	private async Task RefreshPageAsync()
	{
		if (!IsVisible || _prepared || _preparing) return;
		string section = _section;
		long request = ++_pageRequest;
		bool Current() => request == _pageRequest && _section == section && IsVisible;
		try
		{
			if (_lists.TryGetValue(section, out ListState? state))
			{
				JsonElement data = await _service.ExecuteAsync("memory_list_page", new { query = state.Search.Text?.Trim(), kind = EmptyToNull(Selected(state.Kind)), status = section == "archive" ? "archived" : EmptyToNull(Selected(state.Status)), limit = 20, offset = state.Page * 20 }, _lifetime.Token);
				if (!Current()) return;
				state.Total = (int)N(data, "total");
				if (state.Page > 0 && state.Page * 20 >= state.Total) { state.Page = Math.Max(0, (state.Total - 1) / 20); await RefreshPageAsync(); return; }
				state.Count.Text = $"{L("list.title")} ({state.Total})";
				state.PageLabel.Text = $"{state.Page + 1} / {Math.Max(1, (state.Total + 19) / 20)}";
				state.Previous.IsEnabled = state.Page > 0; state.Next.IsEnabled = (state.Page + 1) * 20 < state.Total;
				state.Results.Children.Clear();
				foreach (JsonElement item in Items(P(data, "items"))) state.Results.Children.Add(MemoryCard(item));
				if (state.Results.Children.Count == 0) state.Results.Children.Add(Text(L(!string.IsNullOrWhiteSpace(state.Search.Text) ? "list.emptySearch" : section == "archive" ? "list.emptyArchive" : "list.empty")));
			}
			else if (section == "atoms")
			{
				JsonElement data = await _service.ExecuteAsync("memory_atom_list", new { status = "active", limit = 100, offset = 0 }, _lifetime.Token);
				if (!Current()) return;
				_results[section].Children.Clear();
				foreach (JsonElement atom in Items(data)) _results[section].Children.Add(AtomCard(atom));
				if (_results[section].Children.Count == 0) _results[section].Children.Add(Text(L("atoms.empty")));
			}
			else if (section == "knowledge")
			{
				JsonElement data = await _service.ExecuteAsync("memory_knowledge_status", null, _lifetime.Token);
				if (!Current()) return;
				StackPanel body = _results[section]; body.Children.Clear();
				body.Children.Add(InfoLine("knowledge.path", S(P(_snapshot, "memory"), "knowledgePath", L("detail.empty"))));
				body.Children.Add(InfoLine("knowledge.chunks", N(data, "total").ToString("0")));
				body.Children.Add(InfoLine("knowledge.status", $"{S(data, "state")} · {N(data, "processed")} / {N(data, "total")}"));
				body.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, N(data, "total")), Value = N(data, "processed"), Height = 4 });
				if (S(data, "lastError").Length > 0)
				{
					var error = Text(S(data, "lastError")); SetBrush(error, TextBlock.ForegroundProperty, "SettingsErrorBrush"); body.Children.Add(error);
				}
			}
			if (Current()) Success();
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) { if (Current()) ShowError(ex); }
	}

	private Control MemoryCard(JsonElement item)
	{
		long id = (long)N(item, "id");
		string status = S(item, "status", "active");
		var metadata = Row(Badge(Kind(S(item, "kind", "general")), true), Badge(Status(status)), Badge(Source(S(item, "source"))), Badge($"{L("detail.importance")} {N(item, "importance"):P0}"));
		if (Expired(item) && status != "expired") metadata.Children.Add(Badge(L("detail.isExpired")));
		var body = Stack(metadata, Text(S(item, "content"), 14));
		if (S(item, "tags").Length > 0) body.Children.Add(Secondary(S(item, "tags")));
		return Card("", body,
			Secondary($"#{id} · {L("detail.createdAt")}: {Date(S(item, "createdAt"))}\n{L("detail.lastAccessedAt")}: {Date(S(item, "lastAccessedAt", L("detail.neverAccessed")))}"),
			Row(Button("detail.title", () => OpenEditorAsync(id)),
				Button(status == "archived" ? "archive.restore" : "list.archiveThis", () => ChangeMemoryAsync(id, status == "archived" ? "restore" : "archive")),
				Button("list.deleteThis", () => ChangeMemoryAsync(id, "delete"), true)));
	}

	private Control AtomCard(JsonElement atom) => Card("", Row(Badge(S(atom, "atomType"), true), Badge(Status(S(atom, "status"))), Badge($"#{N(atom, "id")}")),
		Text(S(atom, "content"), 14), Secondary($"{L("detail.importance")}: {N(atom, "importance"):P0} · {L("detail.confidence")}: {N(atom, "confidence"):P0}"),
		Secondary($"{L("atoms.parent")}: #{N(atom, "parentMemoryId")} · {Date(S(atom, "createdAt"))}"),
		Secondary($"{L("detail.decayType")}: {S(atom, "decayType")} · {S(atom, "entities")}"));

	private static bool Expired(JsonElement item) => S(item, "status") == "expired" || DateTimeOffset.TryParse(S(item, "expiresAt"), out var date) && date < DateTimeOffset.UtcNow;

	internal async Task RunDebuggerAsync()
	{
		if (_debugRunning) return;
		string query = _debugQuery.Text?.Trim() ?? "";
		if (query.Length == 0) return;
		_debugRunning = true;
		try
		{
		long request = ++_debugRequest;
		JsonElement result = await _service.ExecuteAsync("memory_recall_debug", new { query }, _lifetime.Token);
		if (request != _debugRequest || _debugQuery.Text?.Trim() != query || !IsVisible) return;
		RenderDebuggerResult(result, query);
		}
		finally { _debugRunning = false; }
	}

	internal void RenderDebuggerResult(JsonElement result, string query)
	{
		StackPanel body = _results["debugger"]; body.Children.Clear();
		JsonElement trace = P(result, "trace");
		body.Children.Add(Card("debugger.query", Text(S(trace, "expandedQuery", query)), Secondary(T("原始查询", "Original query") + ": " + S(trace, "query", query))));
		var channels = new UniformGrid { Columns = _scroll.Viewport.Width >= 620 ? 2 : 1 };
		channels.SizeChanged += (_, args) => channels.Columns = args.NewSize.Width >= 620 ? 2 : 1;
		foreach (var (key, label) in new[] { ("keywordHits", "keyword"), ("vectorHits", "vector"), ("atomHits", "atoms"), ("rrfHits", "rrf") })
		{
			var hits = Stack();
			foreach (JsonElement hit in Items(P(trace, key)))
			{
				var scoreRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
				scoreRow.Children.Add(Text($"#{N(hit, "memoryId")}"));
				var score = Secondary($"{N(hit, "score"):F4} · {T("排名", "Rank")} {N(hit, "rank")}"); Grid.SetColumn(score, 1); scoreRow.Children.Add(score); hits.Children.Add(scoreRow);
			}
			if (hits.Children.Count == 0) hits.Children.Add(Secondary(L("detail.empty")));
			var card = Card("debugger." + label, hits); card.Margin = new Thickness(0, 0, 8, 8); channels.Children.Add(card);
		}
		body.Children.Add(channels);
		body.Children.Add(Card("debugger.filtered", Text(string.Join(", ", Items(P(trace, "filteredIds")).Select(value => value.ToString())))));
		var injected = Stack();
		injected.Children.Add(Text(T("注入编号", "Injected IDs") + ": " + string.Join(", ", Items(P(trace, "injectedIds")).Select(value => value.ToString())), 12));
		foreach (JsonElement item in Items(P(result, "personal")))
		{
			long id = (long)N(item, "id");
			injected.Children.Add(Card("", Text($"#{id} · {S(item, "personaSummary", S(item, "content"))}"), Text(S(item, "canonicalSummary"), 12), Button("detail.title", () => OpenEditorAsync(id))));
		}
		if (!Items(P(result, "personal")).Any()) injected.Children.Add(Text(L("detail.empty")));
		body.Children.Add(Card("debugger.injected", injected));
		var atoms = Stack(); foreach (JsonElement atom in Items(P(result, "atoms"))) atoms.Children.Add(AtomCard(atom));
		body.Children.Add(Card("detail.atoms", atoms));
		var knowledge = Stack();
		foreach (JsonElement item in Items(P(result, "knowledge"))) knowledge.Children.Add(Stack(Text($"#{N(item, "id")} · {S(item, "heading")} · {S(item, "subheading")} · {S(item, "awareness")} · {S(item, "knowledgeType")} · {N(item, "score"):F4}", 12, true), Text(S(item, "content"))));
		body.Children.Add(Card("debugger.knowledge", knowledge));
		var echoes = Stack(); foreach (JsonElement item in Items(P(result, "echoes"))) echoes.Children.Add(Text($"{S(item, "content")} · {N(item, "score"):F4}"));
		body.Children.Add(Card("debugger.echoes", echoes)); Success();
	}
	private long _debugRequest;
	private bool _debugRunning;
	private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;
}
