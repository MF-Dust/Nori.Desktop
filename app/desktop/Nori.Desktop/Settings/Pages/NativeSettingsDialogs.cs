using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nori.Desktop.Settings.Pages;

/// <summary>复杂设置页使用的原生确认、提示和编辑对话框。</summary>
internal static class NativeSettingsDialogs
{
	/// <summary>表单字段描述。</summary>
	internal sealed record Field(string Key, string Label, string Value, bool Password = false, bool Multiline = false);

	/// <summary>显示确认窗口。</summary>
	public static async Task<bool> ConfirmAsync(Window owner, string title, string message, bool destructive = false)
	{
		ArgumentNullException.ThrowIfNull(owner);
		StackPanel body = new() {Spacing = 14, Margin = new Avalonia.Thickness(24)};
		body.Children.Add(new TextBlock {Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520});
		StackPanel buttons = new() {Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8};
		Button cancel = new() {Content = NativeSettingsResources.Get("common.cancel"), MinWidth = 88};
		Button confirm = new() {Content = NativeSettingsResources.Get("common.confirm"), MinWidth = 88};
		if (destructive) confirm.Classes.Add("danger");
		buttons.Children.Add(cancel);
		buttons.Children.Add(confirm);
		body.Children.Add(buttons);
		Window dialog = CreateWindow(title, body);
		cancel.Click += (_, _) => dialog.Close(false);
		confirm.Click += (_, _) => dialog.Close(true);
		bool? result = await dialog.ShowDialog<bool?>(owner).ConfigureAwait(true);
		return result == true;
	}

	/// <summary>显示只读消息窗口。</summary>
	public static async Task ShowMessageAsync(Window owner, string title, string message)
	{
		ArgumentNullException.ThrowIfNull(owner);
		StackPanel body = new() {Spacing = 14, Margin = new Avalonia.Thickness(24)};
		ScrollViewer scroll = new()
		{
			Content = new TextBlock {Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 640},
			MaxHeight = 560,
		};
		body.Children.Add(scroll);
		Button close = new() {Content = NativeSettingsResources.Get("common.close"), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 88};
		body.Children.Add(close);
		Window dialog = CreateWindow(title, body, 560);
		close.Click += (_, _) => dialog.Close();
		await dialog.ShowDialog(owner).ConfigureAwait(true);
	}

	/// <summary>显示单行或多行输入窗口。</summary>
	public static async Task<string?> PromptAsync(
		Window owner,
		string title,
		string label,
		string value = "",
		bool password = false,
		bool multiline = false)
	{
		IReadOnlyDictionary<string, string>? result = await FormAsync(
			owner,
			title,
			[new Field("value", label, value, password, multiline)],
			NativeSettingsResources.Get("common.confirm")).ConfigureAwait(true);
		return result is null ? null : result["value"];
	}

	/// <summary>显示多字段编辑窗口。</summary>
	public static async Task<IReadOnlyDictionary<string, string>?> FormAsync(
		Window owner,
		string title,
		IReadOnlyList<Field> fields,
		string confirmText)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(fields);
		Dictionary<string, TextBox> editors = new(StringComparer.Ordinal);
		StackPanel body = new() {Spacing = 12, Margin = new Avalonia.Thickness(24)};
		ScrollViewer fieldScroll = new() {MaxHeight = 590};
		StackPanel fieldPanel = new() {Spacing = 10};
		foreach (Field field in fields)
		{
			StackPanel row = new() {Spacing = 4};
			row.Children.Add(new TextBlock {Text = field.Label, FontWeight = FontWeight.SemiBold});
			TextBox editor = new()
			{
				Text = field.Value,
				PasswordChar = field.Password ? '•' : '\0',
				AcceptsReturn = field.Multiline,
				TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
				MinHeight = field.Multiline ? 96 : 34,
				VerticalContentAlignment = field.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
			};
			if (field.Multiline) editor.MaxLines = 16;
			editors[field.Key] = editor;
			row.Children.Add(editor);
			fieldPanel.Children.Add(row);
		}
		fieldScroll.Content = fieldPanel;
		body.Children.Add(fieldScroll);
		StackPanel buttons = new() {Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8};
		Button cancel = new() {Content = NativeSettingsResources.Get("common.cancel"), MinWidth = 88};
		Button confirm = new() {Content = confirmText, MinWidth = 88};
		buttons.Children.Add(cancel);
		buttons.Children.Add(confirm);
		body.Children.Add(buttons);
		Window dialog = CreateWindow(title, body, 560);
		cancel.Click += (_, _) => dialog.Close();
		confirm.Click += (_, _) => dialog.Close(true);
		bool? accepted = await dialog.ShowDialog<bool?>(owner).ConfigureAwait(true);
		if (accepted != true) return null;
		return editors.ToDictionary(pair => pair.Key, pair => pair.Value.Text ?? string.Empty, StringComparer.Ordinal);
	}

	private static Window CreateWindow(string title, Control content, double width = 500) => new()
	{
		Title = title,
		Width = width,
		MinWidth = 420,
		MaxWidth = 720,
		SizeToContent = SizeToContent.Height,
		CanResize = true,
		WindowStartupLocation = WindowStartupLocation.CenterOwner,
		Content = content,
	};
}
