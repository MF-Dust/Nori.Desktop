using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.Styling;

namespace Nori.Desktop.Windows;

public sealed partial class MemoryWindow
{
	private Window? _editor;
	private Func<bool>? _editorDirty;
	private bool _editorSaving;
	private Action? _discardEditor;
	private Action? _editorLocalize;
	private readonly Dictionary<Window, Action> _confirmationLocalizers = [];
	private long _detailRequest;

	internal async Task OpenEditorAsync(long? id)
	{
		if (_editor is not null) { _editor.Activate(); return; }
		long request = ++_detailRequest;
		JsonElement detail = id.HasValue ? await _service.ExecuteAsync("memory_get", new { id = id.Value }, _lifetime.Token) : default;
		if (request != _detailRequest || !IsVisible) return;
		JsonElement item = P(detail, "item");
		var dialog = Dialog(id.HasValue ? L("detail.title") + " #" + id : L("add.title"), 620, 650);
		List<Action> translations = [];
		_editorLocalize = () => { foreach (Action translate in translations) translate(); };
		translations.Add(() => dialog.Title = id.HasValue ? L("detail.title") + " #" + id : L("add.title"));
		TextBlock Translated(Func<string> value, double size = 13)
		{
			TextBlock text = Text(value(), size);
			translations.Add(() => text.Text = value());
			return text;
		}
		dialog.Name = "MemoryEditor";
		_editor = dialog;
		var content = Input(S(item, "content"), true); content.Name = "MemoryEditorContent";
		content.MinHeight = 108; content.FontSize = 14;
		var canonical = Input(S(item, "canonicalSummary", S(item, "content")), true); canonical.Name = "MemoryEditorCanonical";
		var persona = Input(S(item, "personaSummary", S(item, "content")), true); persona.Name = "MemoryEditorPersona";
		var tags = Input(S(item, "tags")); tags.Name = "MemoryEditorTags";
		tags.Width = 250;
		if (!id.HasValue)
		{
			content.PlaceholderText = L("add.contentPlaceholder");
			tags.PlaceholderText = L("add.tagsPlaceholder");
			translations.Add(() => { content.PlaceholderText = L("add.contentPlaceholder"); tags.PlaceholderText = L("add.tagsPlaceholder"); });
		}
		int selectedKind = Array.IndexOf(Kinds, S(item, "kind", "general"));
		var kind = Choice(Kinds, Kind, Math.Max(0, selectedKind)); kind.Name = "MemoryEditorKind";
		kind.Width = 180;
		var importance = new NumericUpDown { Minimum = id.HasValue ? 0 : 0.1m, Maximum = 1, Increment = id.HasValue ? 0.05m : 0.1m, Value = (decimal)N(item, "importance", 0.8), Name = "MemoryEditorImportance", FormatString = "0.00" };
		var confidence = new NumericUpDown { Minimum = 0, Maximum = 1, Increment = 0.05m, Value = (decimal)N(item, "confidence", 0.8), Name = "MemoryEditorConfidence", FormatString = "0.00" };
		importance.Width = 112; confidence.Width = 112;
		string Draft() => JsonSerializer.Serialize(new { content = content.Text?.Trim(), canonical = canonical.Text?.Trim(), persona = persona.Text?.Trim(), tags = tags.Text?.Trim(), kind = Selected(kind), importance = importance.Value, confidence = confidence.Value });
		string baseline = Draft();
		_editorDirty = () => Draft() != baseline;
		var error = Text(""); error.Name = "MemoryEditorError";
		string? lastSaveError = null;
		translations.Add(() => { if (lastSaveError is not null) error.Text = L(id.HasValue ? "toast.saveFailed" : "toast.addFailed") + ": " + lastSaveError; });
		SetBrush(error, TextBlock.ForegroundProperty, "SettingsErrorBrush");
		var form = Stack();
		if (id.HasValue)
		{
			form.Children.Add(Card("", Translated(() => $"{Status(S(item, "status", "active"))} · {Kind(S(item, "kind", "general"))} · {Source(S(item, "source"))}"),
				Translated(() => Expired(item) ? L("detail.isExpired") : S(item, "expiresAt").Length > 0 ? L("detail.expiresAt") + ": " + Date(S(item, "expiresAt")) : L("detail.neverExpires"))));
		}
		var writing = Stack(Field("detail.content", content));
		if (id.HasValue) { writing.Children.Add(Field("detail.canonical", canonical)); writing.Children.Add(Field("detail.persona", persona)); }
		form.Children.Add(Card("", writing));
		var properties = new StackPanel { Spacing = 0 };
		properties.Children.Add(SettingLine("detail.kind", kind));
		properties.Children.Add(SettingLine("detail.tags", tags));
		var importancePercent = Text($"{importance.Value:P0}", 12);
		var confidencePercent = Text($"{confidence.Value:P0}", 12);
		importance.ValueChanged += (_, _) => importancePercent.Text = $"{importance.Value:P0}";
		confidence.ValueChanged += (_, _) => confidencePercent.Text = $"{confidence.Value:P0}";
		SetBrush(importancePercent, TextBlock.ForegroundProperty, "SettingsSecondaryBrush");
		SetBrush(confidencePercent, TextBlock.ForegroundProperty, "SettingsSecondaryBrush");
		properties.Children.Add(SettingLine("detail.importance", Row(importance, importancePercent)));
		if (id.HasValue) properties.Children.Add(SettingLine("detail.confidence", Row(confidence, confidencePercent)));
		form.Children.Add(Card("", properties));
		if (id.HasValue)
		{
			var sources = Stack();
			JsonElement[] sourceItems = Items(P(detail, "sources")).ToArray();
			sources.Children.Add(Text(sourceItems.Length.ToString(), 12));
			foreach (JsonElement source in sourceItems)
				sources.Children.Add(Card("", Text($"{S(source, "role")} · #{N(source, "sequence")} · {Date(S(source, "messageTime"))}", 12, true), Text(S(source, "content"))));
			if (sourceItems.Length == 0) sources.Children.Add(Translated(() => L("detail.noSources")));
			form.Children.Add(Card("detail.sourceMessages", sources));
			var timestamps = Stack();
			foreach (string key in new[] { "createdAt", "updatedAt", "lastAccessedAt", "lastReinforcedAt", "expiresAt" })
				timestamps.Children.Add(Translated(() => L("detail." + key) + ": " + Date(S(item, key, key == "lastAccessedAt" ? L("detail.neverAccessed") : key == "lastReinforcedAt" ? L("detail.neverReinforced") : "—")), 12));
			foreach (string key in new[] { "accessCount", "reinforcementCount", "ttlDays" }) timestamps.Children.Add(Translated(() => L("detail." + key) + ": " + S(item, key, key == "ttlDays" ? "—" : "0"), 12));
			form.Children.Add(Card("detail.timestamps", timestamps));
			var atoms = Stack();
			JsonElement[] atomItems = Items(P(detail, "atoms")).ToArray();
			var trace = new Expander { Content = atoms, HorizontalAlignment = HorizontalAlignment.Stretch };
			void TranslateTrace()
			{
				trace.Header = $"{L("detail.advancedSection")} ({atomItems.Length})";
				atoms.Children.Clear();
				if (N(item, "supersededBy") != 0) atoms.Children.Add(Text(L("detail.supersededBy") + ": #" + N(item, "supersededBy")));
				foreach (JsonElement atom in atomItems) atoms.Children.Add(AtomCard(atom));
				if (atomItems.Length == 0) atoms.Children.Add(Text(L("detail.noAtoms")));
			}
			TranslateTrace(); translations.Add(TranslateTrace);
			form.Children.Add(trace);
		}
		form.Children.Add(error);
		bool allowClose = false;
		_discardEditor = () => { allowClose = true; dialog.Close(); };
		bool confirmClosing = false;
		bool CanSave() => !_editorSaving && !string.IsNullOrWhiteSpace(content.Text) && (!id.HasValue || Draft() != baseline);
		var save = Button(id.HasValue ? "detail.save" : "add.submit", async () =>
		{
			if (!CanSave()) return;
			_editorSaving = true;
			string savedDraft = Draft();
			form.IsEnabled = false;
			try
			{
				object args = id.HasValue
					? new { id = id.Value, content = content.Text!.Trim(), canonicalSummary = canonical.Text?.Trim(), personaSummary = persona.Text?.Trim(), tags = tags.Text?.Trim(), kind = Selected(kind), importance = importance.Value, confidence = confidence.Value }
					: new { content = content.Text!.Trim(), tags = tags.Text?.Trim(), kind = Selected(kind), importance = importance.Value };
				JsonElement result = await _service.ExecuteAsync(id.HasValue ? "memory_update" : "memory_add", args, _lifetime.Token);
				if (result.ValueKind == JsonValueKind.False) throw new InvalidOperationException(L("toast.saveFailed"));
				baseline = savedDraft;
				allowClose = true; _editorSaving = false; dialog.Close();
				await RefreshAsync();
				if (id.HasValue) await OpenEditorAsync(id);
				Success();
			}
			catch (Exception ex)
			{
				lastSaveError = ex.Message;
				error.Text = L(id.HasValue ? "toast.saveFailed" : "toast.addFailed") + ": " + ex.Message;
				_status.Text = error.Text;
				SetBrush(_status, TextBlock.ForegroundProperty, "SettingsErrorBrush");
			}
			finally { _editorSaving = false; form.IsEnabled = true; }
		}, enabled: CanSave);
		save.Name = "MemoryEditorSave"; save.IsEnabled = CanSave();
		save.Classes.Add("accent");
		void UpdateSave() => save.IsEnabled = CanSave();
		content.TextChanged += (_, _) => UpdateSave(); canonical.TextChanged += (_, _) => UpdateSave(); persona.TextChanged += (_, _) => UpdateSave(); tags.TextChanged += (_, _) => UpdateSave();
		kind.SelectionChanged += (_, _) => UpdateSave(); importance.ValueChanged += (_, _) => UpdateSave(); confidence.ValueChanged += (_, _) => UpdateSave();
		var buttons = Row(Button("common.cancel", () => { dialog.Close(); return Task.CompletedTask; }), save);
		if (id.HasValue)
		{
			string operation = S(item, "status") == "archived" ? "restore" : "archive";
			buttons.Children.Insert(0, Button(operation == "restore" ? "archive.restore" : "list.archiveThis", async () =>
			{
				if (await ChangeMemoryAsync(id.Value, operation, dialog)) { allowClose = true; dialog.Close(); }
			}));
			buttons.Children.Insert(1, Button("list.deleteThis", async () => { if (await ChangeMemoryAsync(id.Value, "delete", dialog)) { allowClose = true; dialog.Close(); } }, true));
		}
		foreach (Control button in buttons.Children) button.Margin = new Thickness(0, 0, 8, 8);
		buttons.HorizontalAlignment = HorizontalAlignment.Right;
		var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(20) };
		grid.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
		var footer = new Border { Child = buttons, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14, 0, 0), Margin = new Thickness(0, 14, 0, 0) };
		SetBrush(footer, Border.BorderBrushProperty, "SettingsBorderBrush");
		Grid.SetRow(footer, 1); grid.Children.Add(footer); dialog.Content = grid;
		DisableSaveSurface(grid);
		dialog.Closing += async (_, args) =>
		{
			if (_editorSaving) { args.Cancel = true; return; }
			if (allowClose || Draft() == baseline) return;
			args.Cancel = true;
			if (confirmClosing) return;
			confirmClosing = true;
			try { if (await ConfirmAsync("detail.unsavedTitle", "detail.unsavedDesc", dialog)) { allowClose = true; dialog.Close(); } }
			finally { confirmClosing = false; }
		};
		dialog.Closed += (_, _) => { _editor = null; _editorDirty = null; _discardEditor = null; _editorLocalize = null; };
		_ = dialog.ShowDialog(this);
		Success();
	}

	private Window Dialog(string title, double width, double height)
	{
		var dialog = new Window
		{
			Title = title, Width = width, Height = height, MinWidth = 380, MinHeight = 240,
			MaxHeight = Math.Max(360, Height - 30), WindowStartupLocation = WindowStartupLocation.CenterOwner,
			RequestedThemeVariant = ThemeVariant.Dark, ShowInTaskbar = false,
		};
		dialog.Styles.Add(new StyleInclude(new Uri("avares://Nori.Desktop/")) { Source = new Uri("avares://Nori.Desktop/Settings/SettingsTheme.axaml") });
		return dialog;
	}

	private async Task<bool> ConfirmAsync(string titleKey, string descriptionKey, Window? owner = null)
	{
		var dialog = Dialog(L(titleKey), 440, 250); dialog.Name = "MemoryConfirmation";
		string actionKey = titleKey switch
		{
			"detail.unsavedTitle" => "detail.discardChanges",
			"list.clearAll" => "list.clearConfirm",
			"transfer.confirmModalTitle" => "transfer.confirmCommit",
			"detail.restoreConfirmTitle" => "archive.restore",
			"detail.archiveConfirmTitle" => "list.archiveThis",
			"list.deleteThis" => "list.delete",
			_ => titleKey,
		};
		var confirm = new Button { Content = L(actionKey), Name = "MemoryConfirmAccept", MinHeight = 34 };
		confirm.Classes.Add(titleKey is "list.clearAll" or "list.deleteThis" ? "danger" : "accent");
		var cancel = new Button { Content = L(titleKey == "detail.unsavedTitle" ? "detail.keepEditing" : "common.cancel"), Name = "MemoryConfirmCancel", MinHeight = 34 };
		confirm.Click += (_, _) => dialog.Close(true); cancel.Click += (_, _) => dialog.Close(false);
		var title = Text(L(titleKey), 20, true);
		var description = Text(L(descriptionKey));
		_confirmationLocalizers[dialog] = () =>
		{
			dialog.Title = L(titleKey); title.Text = L(titleKey); description.Text = L(descriptionKey);
			confirm.Content = L(actionKey); cancel.Content = L(titleKey == "detail.unsavedTitle" ? "detail.keepEditing" : "common.cancel");
		};
		dialog.Closed += (_, _) => _confirmationLocalizers.Remove(dialog);
		dialog.Content = new Border { Padding = new Thickness(24), Child = Stack(title, description, Row(cancel, confirm)) };
		return await dialog.ShowDialog<bool>(owner ?? this);
	}

	internal async Task<bool> ChangeMemoryAsync(long id, string operation, Window? owner = null)
	{
		string title = operation == "delete" ? "list.deleteThis" : "detail." + operation + "ConfirmTitle";
		string description = operation == "delete" ? "list.deleteQuestion" : "detail." + operation + "ConfirmDesc";
		if (!await ConfirmAsync(title, description, owner)) return false;
		JsonElement result = await _service.ExecuteAsync("memory_" + operation, operation == "delete" ? new { id, confirmToken = "DELETE_MEMORY" } : (object)new { id }, _lifetime.Token);
		if (result.ValueKind == JsonValueKind.False) throw new InvalidOperationException(L("toast." + operation + "Failed"));
		await RefreshAsync(); Success(); return true;
	}

	private async Task ClearAllAsync()
	{
		if (!await ConfirmAsync("list.clearAll", "list.clearQuestion")) return;
		await _service.ExecuteAsync("memory_clear", new { confirmToken = "CLEAR_PERSONAL_MEMORY" }, _lifetime.Token);
		await RefreshAsync(); Success();
	}
}
