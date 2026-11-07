using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Nori.Core.Mcp;
using Nori.Core.Skills;
using Nori.Desktop.Settings;

namespace Nori.Desktop.Settings.Pages;

/// <summary>技能与 MCP 的导入预览、确认和导出。</summary>
internal static class NativeCatalogImportDialogs
{
	/// <summary>粘贴或选择技能文件，确认后才写入。</summary>
	public static async Task ShowSkillImportAsync(Window owner, SkillsSettingsViewModel viewModel)
	{
		TextBox paste = new() {AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxLines = 10};
		TextBlock fileLabel = new() {TextWrapping = TextWrapping.Wrap, Foreground = SettingsBrushes.Resolve(owner, "SettingsSecondaryBrush")};
		TextBlock preview = new() {TextWrapping = TextWrapping.Wrap, IsVisible = false};
		TextBlock feedback = new() {TextWrapping = TextWrapping.Wrap, IsVisible = false};
		ToggleSwitch enabled = new() {Content = NativeSettingsResources.Get("common.enable"), IsChecked = true};
		string text = "";
		string? token = null;
		StackPanel body = new() {Spacing = 12, Margin = new Thickness(24)};
		body.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("skills.importHint"), TextWrapping = TextWrapping.Wrap});
		body.Children.Add(paste);
		StackPanel fileRow = new() {Orientation = Orientation.Horizontal, Spacing = 8};
		Button pick = new() {Content = NativeSettingsResources.Get("skills.pickFile")};
		fileRow.Children.Add(pick);
		fileRow.Children.Add(fileLabel);
		body.Children.Add(fileRow);
		body.Children.Add(preview);
		body.Children.Add(enabled);
		body.Children.Add(feedback);
		StackPanel actions = new() {Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8};
		Button cancel = new() {Content = NativeSettingsResources.Get("common.cancel")};
		Button review = new() {Content = NativeSettingsResources.Get("skills.preview")};
		Button confirm = new() {Content = NativeSettingsResources.Get("common.confirm"), IsEnabled = false};
		confirm.Classes.Add("accent");
		actions.Children.Add(cancel);
		actions.Children.Add(review);
		actions.Children.Add(confirm);
		body.Children.Add(actions);
		Window dialog = NativeSettingsDialogs.CreateWindow(NativeSettingsResources.Get("skills.import"), body, Math.Min(640, Math.Max(420, owner.Bounds.Width - 48)));
		pick.Click += async (_, _) =>
		{
			string? loaded = await ReadFileAsync(dialog, ["*.md", "*.json", "*.txt"], SkillLimits.MaxRemoteDocumentCharacters).ConfigureAwait(true);
			if (loaded is null) return;
			text = loaded;
			paste.Text = loaded.Length > 4000 ? loaded[..4000] : loaded;
			fileLabel.Text = NativeSettingsResources.Get("skills.pickFile");
			token = null;
			confirm.IsEnabled = false;
		};
		review.Click += async (_, _) =>
		{
			try
			{
				text = string.IsNullOrWhiteSpace(paste.Text) ? text : paste.Text ?? "";
				SkillImportPreviewItem item = await viewModel.PreviewLocalAsync(text).ConfigureAwait(true);
				token = item.Token;
				preview.IsVisible = true;
				preview.Text = $"{item.Name}\n{item.Description}\n{item.Category}\n{string.Join(", ", item.Tools)}";
				confirm.IsEnabled = token.Length > 0;
				feedback.IsVisible = false;
			}
			catch (Exception exception)
			{
				ShowError(feedback, dialog, exception);
				confirm.IsEnabled = false;
			}
		};
		confirm.Click += async (_, _) =>
		{
			if (string.IsNullOrWhiteSpace(token)) return;
			try
			{
				await viewModel.CommitLocalAsync(token, enabled.IsChecked == true).ConfigureAwait(true);
				dialog.Close();
			}
			catch (Exception exception)
			{
				ShowError(feedback, dialog, exception);
			}
		};
		cancel.Click += (_, _) => dialog.Close();
		await dialog.ShowDialog(owner).ConfigureAwait(true);
	}

	/// <summary>导出技能 JSON，可复制或保存为文件。</summary>
	public static async Task ShowSkillExportAsync(Window owner, SkillsSettingsViewModel viewModel, SkillItem skill)
	{
		string json = await viewModel.ExportJsonAsync(skill).ConfigureAwait(true);
		TextBox box = new() {Text = json, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxLines = 12};
		TextBlock feedback = new() {TextWrapping = TextWrapping.Wrap, IsVisible = false};
		StackPanel body = new() {Spacing = 12, Margin = new Thickness(24)};
		body.Children.Add(box);
		body.Children.Add(feedback);
		StackPanel actions = new() {Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8};
		Button copy = new() {Content = NativeSettingsResources.Get("skills.exportCopy")};
		Button save = new() {Content = NativeSettingsResources.Get("skills.exportFile")};
		Button close = new() {Content = NativeSettingsResources.Get("common.close")};
		actions.Children.Add(copy);
		actions.Children.Add(save);
		actions.Children.Add(close);
		body.Children.Add(actions);
		Window dialog = NativeSettingsDialogs.CreateWindow(skill.Name, body, Math.Min(640, Math.Max(420, owner.Bounds.Width - 48)));
		copy.Click += async (_, _) =>
		{
			try
			{
				await viewModel.CopyTextAsync(json).ConfigureAwait(true);
				feedback.IsVisible = true;
				feedback.Foreground = SettingsBrushes.Resolve(dialog, "SettingsSuccessBrush");
				feedback.Text = NativeSettingsResources.Get("common.copy");
			}
			catch (Exception exception)
			{
				ShowError(feedback, dialog, exception);
			}
		};
		save.Click += async (_, _) =>
		{
			try
			{
				await WriteFileAsync(dialog, skill.Id + ".json", json).ConfigureAwait(true);
			}
			catch (Exception exception)
			{
				ShowError(feedback, dialog, exception);
			}
		};
		close.Click += (_, _) => dialog.Close();
		await dialog.ShowDialog(owner).ConfigureAwait(true);
	}

	/// <summary>粘贴、选择文件或从网址预览 MCP 配置。确认后才写入。</summary>
	public static async Task ShowMcpImportAsync(Window owner, McpSettingsViewModel viewModel)
	{
		bool safeMode = viewModel.SafeMode;
		TextBox paste = new() {AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxLines = 8};
		TextBox url = new() {PlaceholderText = NativeSettingsResources.Get("mcp.url"), IsEnabled = !safeMode};
		TextBlock preview = new() {TextWrapping = TextWrapping.Wrap, IsVisible = false};
		TextBlock feedback = new() {TextWrapping = TextWrapping.Wrap, IsVisible = false};
		string? token = null;
		StackPanel body = new() {Spacing = 12, Margin = new Thickness(24)};
		if (safeMode) body.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("mcp.safeMode"), Foreground = SettingsBrushes.Resolve(owner, "SettingsWarningBrush"), TextWrapping = TextWrapping.Wrap});
		body.Children.Add(new TextBlock {Text = NativeSettingsResources.Get("mcp.importHint"), TextWrapping = TextWrapping.Wrap});
		body.Children.Add(paste);
		StackPanel fileRow = new() {Orientation = Orientation.Horizontal, Spacing = 8};
		Button pick = new() {Content = NativeSettingsResources.Get("skills.pickFile")};
		Button reviewFile = new() {Content = NativeSettingsResources.Get("skills.preview")};
		fileRow.Children.Add(pick);
		fileRow.Children.Add(reviewFile);
		body.Children.Add(fileRow);
		body.Children.Add(url);
		Button reviewUrl = new() {Content = NativeSettingsResources.Get("mcp.import"), IsEnabled = !safeMode};
		body.Children.Add(reviewUrl);
		body.Children.Add(preview);
		body.Children.Add(feedback);
		StackPanel actions = new() {Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8};
		Button cancel = new() {Content = NativeSettingsResources.Get("common.cancel")};
		Button confirm = new() {Content = NativeSettingsResources.Get("common.confirm"), IsEnabled = false};
		confirm.Classes.Add("accent");
		actions.Children.Add(cancel);
		actions.Children.Add(confirm);
		body.Children.Add(actions);
		Window dialog = NativeSettingsDialogs.CreateWindow(NativeSettingsResources.Get("mcp.import"), body, Math.Min(640, Math.Max(420, owner.Bounds.Width - 48)));
		string fileText = "";
		pick.Click += async (_, _) =>
		{
			string? loaded = await ReadFileAsync(dialog, ["*.json"], McpConfigImporter.MaxDocumentCharacters).ConfigureAwait(true);
			if (loaded is null) return;
			fileText = loaded;
			paste.Text = loaded.Length > 4000 ? loaded[..4000] : loaded;
		};
		reviewFile.Click += async (_, _) =>
		{
			try
			{
				string text = string.IsNullOrWhiteSpace(paste.Text) ? fileText : paste.Text ?? "";
				await ShowPreviewAsync(viewModel.PreviewTextAsync(text), preview, feedback, confirm, value => token = value).ConfigureAwait(true);
			}
			catch (Exception exception)
			{
				ShowError(feedback, dialog, exception);
			}
		};
		reviewUrl.Click += async (_, _) =>
		{
			try
			{
				await ShowPreviewAsync(viewModel.PreviewUrlAsync(url.Text ?? ""), preview, feedback, confirm, value => token = value).ConfigureAwait(true);
			}
			catch (Exception exception)
			{
				ShowError(feedback, dialog, exception);
			}
		};
		confirm.Click += async (_, _) =>
		{
			if (string.IsNullOrWhiteSpace(token)) return;
			try
			{
				await viewModel.CommitImportAsync(token).ConfigureAwait(true);
				dialog.Close();
			}
			catch (Exception exception)
			{
				ShowError(feedback, dialog, exception);
			}
		};
		cancel.Click += (_, _) => dialog.Close();
		await dialog.ShowDialog(owner).ConfigureAwait(true);
	}

	private static async Task ShowPreviewAsync(
		Task<McpImportPreviewItem> loading,
		TextBlock preview,
		TextBlock feedback,
		Button confirm,
		Action<string> setToken)
	{
		McpImportPreviewItem item = await loading.ConfigureAwait(true);
		setToken(item.Token);
		preview.IsVisible = true;
		preview.Text = string.Join("\n\n", item.Servers.Select(server =>
			$"{server.Name} · {server.Transport}\n{server.Command ?? server.Url}\n{NativeSettingsResources.Get("mcp.argumentCount")}: {server.ArgumentCount}\n{NativeSettingsResources.Get("mcp.envNames")}: {string.Join(", ", server.EnvironmentNames)}\n{NativeSettingsResources.Get("mcp.headerNames")}: {string.Join(", ", server.HeaderNames)}"));
		confirm.IsEnabled = item.Token.Length > 0 && item.Servers.Count > 0;
		feedback.IsVisible = false;
	}

	private static async Task<string?> ReadFileAsync(Window owner, string[] patterns, int maxCharacters)
	{
		IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType("Text") {Patterns = patterns}],
		});
		if (files.Count == 0) return null;
		await using Stream stream = await files[0].OpenReadAsync();
		using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		char[] buffer = new char[maxCharacters + 1];
		int read = await reader.ReadBlockAsync(buffer);
		if (read > maxCharacters) throw new InvalidOperationException("文件超过长度上限");
		return new string(buffer, 0, read);
	}

	private static async Task WriteFileAsync(Window owner, string suggestedName, string content)
	{
		IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			SuggestedFileName = suggestedName,
			DefaultExtension = "json",
			FileTypeChoices = [new FilePickerFileType("JSON") {Patterns = ["*.json"]}],
			ShowOverwritePrompt = true,
		});
		if (file is null) return;
		await using Stream stream = await file.OpenWriteAsync();
		if (stream.CanSeek) stream.SetLength(0);
		await using StreamWriter writer = new(stream, Encoding.UTF8);
		await writer.WriteAsync(content);
	}

	private static void ShowError(TextBlock feedback, Window dialog, Exception exception)
	{
		feedback.IsVisible = true;
		feedback.Foreground = SettingsBrushes.Resolve(dialog, "SettingsErrorBrush");
		feedback.Text = SettingsErrorText.Resolve(exception);
	}
}
