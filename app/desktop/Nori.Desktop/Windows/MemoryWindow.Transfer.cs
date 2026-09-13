using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nori.Core.Memory;

namespace Nori.Desktop.Windows;

public sealed partial class MemoryWindow
{
	private static readonly int TransferMaxBytes = new MemoryTransferLimits().MaxBytes;
	private readonly List<Button> _transferActions = [];
	private StackPanel _exportDetails = null!;
	private StackPanel _importDetails = null!;
	private TextBlock _transferError = null!;
	private TextBlock _importFileLabel = null!;
	private Button _previewButton = null!;
	private Button _commitButton = null!;
	private Button _cancelImportButton = null!;
	private Button _saveExportButton = null!;
	private Button _copyExportButton = null!;
	private ComboBox _conflictChoice = null!;
	private JsonElement _exportData;
	private JsonElement _importPreview;
	private string _importContent = "";
	private string _importFileName = "";
	private long _importFileSize;
	private long _transferVersion;
	private bool _transferBusy;
	private bool _importCommitting;
	internal JsonElement ImportPreview => _importPreview;
	internal string ImportDraftContent => _importContent;
	internal string TransferError => _transferError.Text ?? "";

	private Control BuildTransferPage()
	{
		_exportDetails = Stack();
		_importDetails = Stack();
		_transferError = Text("");
		SetBrush(_transferError, TextBlock.ForegroundProperty, "SettingsErrorBrush");
		_importFileLabel = Text("");
		_localize.Add(UpdateImportFileLabel);
		_conflictChoice = Choice(["skip", "overwrite", "create_copy"], value => L(value switch
		{
			"overwrite" => "transfer.strategyOverwrite",
			"create_copy" => "transfer.strategyCreateCopy",
			_ => "transfer.strategySkip",
		}));
		_conflictChoice.Name = "MemoryImportStrategy";
		_previewButton = TransferButton("transfer.previewBtn", PreviewImportAsync, "transfer.previewFailed");
		_previewButton.Name = "MemoryImportPreview";
		_commitButton = TransferButton("transfer.confirmImportBtn", CommitImportAsync, "toast.importFailed");
		_commitButton.Name = "MemoryImportCommit";
		_cancelImportButton = new Button { Name = "MemoryImportCancel", MinHeight = 32, Padding = new Thickness(12, 6) };
		_localize.Add(() => _cancelImportButton.Content = L("transfer.cancelPreview"));
		_cancelImportButton.Click += (_, _) => { if (!_importCommitting) ResetImport(); };
		_saveExportButton = TransferButton("transfer.downloadFile", SaveExportAsync, "toast.exportFailed");
		_copyExportButton = TransferButton("transfer.copyJson", CopyExportAsync, "toast.exportFailed");
		var export = Card("transfer.exportTitle", Label("transfer.exportDesc"),
			TransferButton("transfer.exportBtn", ExportMemoriesAsync, "toast.exportFailed"), _exportDetails, Row(_saveExportButton, _copyExportButton));
		var import = Card("transfer.importTitle", Label("transfer.importDesc"), Label("transfer.fileLimitHint"),
			TransferButton("transfer.selectFile", PickImportFileAsync, "transfer.fileReadFailed"), _importFileLabel,
			Row(_previewButton, _cancelImportButton),
			new ScrollViewer { Content = _importDetails, MaxHeight = 380, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
			Field("transfer.strategyTitle", _conflictChoice), _commitButton);
		UpdateTransferActions();
		return Stack(export, import, _transferError);
	}

	private Button TransferButton(string key, Func<Task> action, string errorKey)
	{
		var button = new Button { MinHeight = 32, Padding = new Thickness(12, 6) };
		_localize.Add(() => { button.Content = L(key); Avalonia.Automation.AutomationProperties.SetName(button, L(key)); });
		button.Content = L(key);
		_transferActions.Add(button);
		button.Click += async (_, _) =>
		{
			if (_transferBusy) return;
			_transferBusy = true;
			_transferError.Text = "";
			UpdateTransferActions();
			Task task = ExecuteTransferActionAsync(action, errorKey);
			_operations.Add(task);
			try { await task; }
			finally { _operations.Remove(task); _transferBusy = false; UpdateTransferActions(); }
		};
		return button;
	}

	private async Task ExecuteTransferActionAsync(Func<Task> action, string errorKey)
	{
		try { await action(); }
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		catch (Exception ex)
		{
			_transferError.Text = L(errorKey) + ": " + ex.Message;
			_status.Text = L(errorKey);
			SetBrush(_status, TextBlock.ForegroundProperty, "SettingsErrorBrush");
		}
	}

	private void UpdateTransferActions()
	{
		foreach (Button button in _transferActions) button.IsEnabled = !_transferBusy;
		if (_previewButton is null) return;
		_previewButton.IsEnabled = !_transferBusy && _importContent.Length > 0;
		_commitButton.IsEnabled = !_transferBusy && ValidImportPreview();
		_conflictChoice.IsEnabled = !_transferBusy && ValidImportPreview();
		_cancelImportButton.IsEnabled = !_importCommitting;
		if (_saveExportButton is not null)
		{
			_saveExportButton.IsEnabled = !_transferBusy && S(_exportData, "content").Length > 0;
			_copyExportButton.IsEnabled = _saveExportButton.IsEnabled;
		}
	}

	private bool ValidImportPreview() => B(_importPreview, "valid") && N(_importPreview, "totalCount") > 0 && S(_importPreview, "previewToken").Length > 0;

	private void UpdateImportFileLabel() => _importFileLabel.Text = _importFileName.Length == 0
		? L("transfer.selectFile")
		: $"{L("transfer.fileSelected")}: {_importFileName}\n{L("transfer.fileSize")}: {_importFileSize:N0} B";

	internal void ResetImport()
	{
		_transferVersion++;
		_importContent = "";
		_importFileName = "";
		_importFileSize = 0;
		_importPreview = default;
		_importDetails.Children.Clear();
		_transferError.Text = "";
		_conflictChoice.SelectedIndex = 0;
		UpdateImportFileLabel();
		UpdateTransferActions();
	}

	internal async Task ExportMemoriesAsync()
	{
		_status.Text = L("transfer.exporting");
		_exportData = default;
		_exportDetails.Children.Clear();
		JsonElement data = await _service.ExecuteAsync("memory_export", null, _lifetime.Token);
		if (S(data, "content").Length == 0) throw new InvalidOperationException(L("transfer.exportFailed"));
		_exportData = data;
		_exportDetails.Children.Add(Text(L("transfer.exportStatsTitle"), 15, true));
		_exportDetails.Children.Add(Text(L("transfer.privacyBadge")));
		_exportDetails.Children.Add(Row(
			Text($"{L("transfer.totalExported")}: {N(data, "totalCount")}"),
			Text($"{L("transfer.activeExported")}: {N(data, "activeCount")}"),
			Text($"{L("transfer.archivedExported")}: {N(data, "archivedCount")}")));
		_exportDetails.Children.Add(Text($"{T("格式版本", "Format version")}: {S(data, "version")} · {T("导出时间", "Exported at")}: {Date(S(data, "exportedAt"))}", 12));
		_exportDetails.Children.Add(Stack(Text(L("transfer.sanitizedFieldsTitle"), 12, true), Text(string.Join(" · ", Items(P(data, "sanitizedFields")).Select(field => field.ToString())))));
		_exportDetails.Children.Add(Text(L("transfer.sanitizedNotice")));
		UpdateTransferActions();
		Success(L("transfer.exportSuccess"));
	}

	private async Task SaveExportAsync()
	{
		string content = S(_exportData, "content");
		if (content.Length == 0) throw new InvalidOperationException(L("transfer.exportFailed"));
		IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = L("transfer.downloadFile"), SuggestedFileName = S(_exportData, "fileName", "nori-memory-export.json"),
			DefaultExtension = "json", FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
			ShowOverwritePrompt = true,
		});
		if (file is null) return;
		using (file)
		{
			await Task.Run(async () =>
			{
				await using Stream stream = await file.OpenWriteAsync();
				if (stream.CanSeek) stream.SetLength(0);
				await stream.WriteAsync(Encoding.UTF8.GetBytes(content), _lifetime.Token);
				await stream.FlushAsync(_lifetime.Token);
			}, _lifetime.Token);
		}
		Success(L("transfer.exportSuccess"));
	}

	private async Task CopyExportAsync()
	{
		string content = S(_exportData, "content");
		if (content.Length == 0) throw new InvalidOperationException(L("transfer.exportFailed"));
		await _service.ExecuteAsync("clipboard_write_text", new { text = content }, _lifetime.Token);
		Success(L("transfer.copied"));
	}

	private async Task PickImportFileAsync()
	{
		long version = ++_transferVersion;
		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = L("transfer.selectFile"), AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
		});
		if (files.Count == 0) return;
		using IStorageFile file = files[0];
		if (version != _transferVersion) return;
		ResetImport();
		version = _transferVersion;
		if (!file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(L("transfer.fileInvalidFormat"));
		(string content, long size) loaded;
		try
		{
			loaded = await Task.Run(async () =>
			{
				await using Stream stream = await file.OpenReadAsync();
				return await ReadTransferContentAsync(stream, _lifetime.Token);
			}, _lifetime.Token);
		}
		catch (Exception) when (version != _transferVersion) { return; }
		if (version != _transferVersion) return;
		_importContent = loaded.content;
		_importFileName = file.Name;
		_importFileSize = loaded.size;
		UpdateImportFileLabel();
		Success(L("transfer.fileSelected"));
	}

	/// <summary>使用与文件选择器相同的限量流校验，供无对话框宿主验证复用。</summary>
	internal async Task LoadTransferStreamAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
	{
		ResetImport();
		long version = _transferVersion;
		if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(L("transfer.fileInvalidFormat"));
		(string content, long size) = await ReadTransferContentAsync(stream, cancellationToken);
		if (version != _transferVersion) return;
		_importContent = content; _importFileName = fileName; _importFileSize = size;
		UpdateImportFileLabel(); UpdateTransferActions();
	}

	/// <summary>限量读取真实流，防止文件元数据过时或恶意流突破导入边界。</summary>
	internal static async Task<(string Content, long Size)> ReadTransferContentAsync(Stream stream, CancellationToken cancellationToken)
	{
		using var buffer = new MemoryStream();
		byte[] chunk = new byte[81920];
		while (true)
		{
			int read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, TransferMaxBytes + 1 - (int)buffer.Length)), cancellationToken);
			if (read == 0) break;
			if (buffer.Length + read > TransferMaxBytes) throw new MemoryTransferException(MemoryTransferErrorCategory.PayloadTooLarge);
			buffer.Write(chunk, 0, read);
		}
		string content;
		try
		{
			content = new UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\uFEFF');
			using JsonDocument document = JsonDocument.Parse(content);
		}
		catch (Exception ex) when (ex is DecoderFallbackException or JsonException)
		{
			throw new MemoryTransferException(MemoryTransferErrorCategory.InvalidJson);
		}
		return (content, buffer.Length);
	}

	internal async Task PreviewImportAsync()
	{
		if (_importContent.Length == 0) return;
		long version = ++_transferVersion;
		_importPreview = default;
		_importDetails.Children.Clear();
		_status.Text = L("transfer.previewing");
		JsonElement data;
		try { data = await _service.ExecuteAsync("memory_import_preview", new { fileContent = _importContent, fileName = _importFileName, fileSize = _importFileSize }, _lifetime.Token); }
		catch (Exception) when (version != _transferVersion) { return; }
		if (version != _transferVersion) return;
		_importPreview = data;
		_importDetails.Children.Add(Text(L("transfer.previewSummaryTitle"), 15, true));
		foreach (var (key, label) in new[] { ("totalCount", "totalToImport"), ("newCount", "newItems"), ("duplicateCount", "duplicateItems"), ("conflictCount", "conflictItems"), ("errorCount", "errorItems") })
			_importDetails.Children.Add(Text($"{L("transfer." + label)}: {N(data, key)}"));
		_importDetails.Children.Add(Text(L("transfer.previewNotice")));
		JsonElement[] items = Items(P(data, "items")).ToArray();
		_importDetails.Children.Add(Text($"{L("transfer.previewListTitle")} ({items.Length})", 14, true));
		foreach (JsonElement item in items)
		{
			string conflictKey = S(item, "conflictType") switch { "duplicate" => "duplicateLabel", "conflict" => "conflictLabel", _ => "newLabel" };
			_importDetails.Children.Add(Card("", Text($"#{N(item, "id")} · {L("transfer." + conflictKey)} · {Kind(S(item, "kind"))}", 12, true),
				Text(S(item, "contentSummary")), Text($"{L("detail.importance")}: {N(item, "importance"):P0} · {L("detail.confidence")}: {N(item, "confidence"):P0}", 12),
				Text($"{L("detail.tags")}: {S(item, "tags", L("detail.empty"))}", 12), Text(S(item, "conflictReason"), 12)));
		}
		if (items.Length == 0) _importDetails.Children.Add(Text(L("transfer.previewNoItems")));
		if (!B(data, "valid"))
		{
			string errors = string.Join("\n", Items(P(data, "errors")).Select(error => error.ToString()));
			throw new InvalidOperationException(errors.Length > 0 ? errors : L("transfer.previewFailed"));
		}
		Success(L("transfer.previewSuccess"));
		UpdateTransferActions();
	}

	/// <summary>在原生确认完成后消费当前预览；异常时保留文件草稿用于重新解析。</summary>
	internal async Task CommitConfirmedImportAsync(string strategy)
	{
		if (!ValidImportPreview()) throw new InvalidOperationException(L("transfer.previewFailed"));
		string token = S(_importPreview, "previewToken");
		_importPreview = default;
		JsonElement result = await _service.ExecuteAsync("memory_import_commit", new { previewToken = token, conflictStrategy = strategy }, _lifetime.Token);
		if (!B(result, "success")) throw new InvalidOperationException(S(result, "message", L("transfer.importFailed")));
		ResetImport();
		await RefreshAsync();
		Success($"{L("transfer.importSuccess")} · {T("新增", "Added")}: {N(result, "importedCount")} · {T("更新", "Updated")}: {N(result, "updatedCount")} · {T("跳过", "Skipped")}: {N(result, "skippedCount")}");
	}

	internal Task RunTransferForTestingAsync(Func<Task> action, string errorKey) => ExecuteTransferActionAsync(action, errorKey);

	private async Task CommitImportAsync()
	{
		if (!ValidImportPreview()) return;
		long version = _transferVersion;
		string strategy = Selected(_conflictChoice);
		_importCommitting = true;
		UpdateTransferActions();
		try
		{
			if (!await ConfirmAsync("transfer.confirmModalTitle", "transfer.confirmModalDesc") || version != _transferVersion) return;
			_status.Text = L("transfer.importing");
			await CommitConfirmedImportAsync(strategy);
		}
		finally { _importCommitting = false; UpdateTransferActions(); }
	}
}
