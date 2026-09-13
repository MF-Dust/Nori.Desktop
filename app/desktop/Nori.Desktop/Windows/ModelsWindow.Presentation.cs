using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.Desktop.Settings;

namespace Nori.Desktop.Windows;

public sealed partial class ModelsWindow
{
	private string T(string chinese, string english) => _language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? english : chinese;
	private void Localize(Action action) { (_buildingAdjust ? _adjustLocalize : _localize).Add(action); action(); }
	private static TextBlock Text(string value, double size = 13, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
	private TextBlock Local(Func<string> value, double size = 13, bool bold = false)
	{
		var text = Text(value(), size, bold); Localize(() => text.Text = value()); return text;
	}
	private TextBlock Secondary(Func<string> value)
	{
		TextBlock text = Local(value, 12); Brush(text, TextBlock.ForegroundProperty, "SettingsSecondaryBrush"); return text;
	}
	private static StackPanel Stack(params Control[] children)
	{
		var stack = new StackPanel { Spacing = 12 };
		foreach (Control child in children) stack.Children.Add(child);
		return stack;
	}
	private static WrapPanel Row(params Control[] children)
	{
		var row = new WrapPanel();
		foreach (Control child in children) { child.Margin = new Thickness(0, 0, 8, 6); row.Children.Add(child); }
		return row;
	}
	private static ScrollViewer Scroller(Control child)
	{
		child.MaxWidth = 820;
		return new ScrollViewer { Content = child, Padding = new Thickness(0, 0, 8, 0), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
	}
	private Border Card(Func<string>? title, params Control[] children)
	{
		var body = Stack();
		if (title is not null) body.Children.Add(Local(title, 15, true));
		foreach (Control child in children) body.Children.Add(child);
		var border = new Border { Child = body, Padding = new Thickness(20, 16), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
		Brush(border, Border.BackgroundProperty, "SettingsCardBrush"); Brush(border, Border.BorderBrushProperty, "SettingsBorderBrush"); return border;
	}
	private Button ActionButton(Func<string> label, Func<Task> action, string? name = null, bool danger = false)
	{
		var button = new Button { MinHeight = 32, Padding = new Thickness(12, 6), Name = name };
		Localize(() => { button.Content = label(); AutomationProperties.SetName(button, label()); });
		if (danger) button.Classes.Add("danger");
		button.Click += async (_, _) =>
		{
			button.IsEnabled = false;
			try { await action(); }
			catch (OperationCanceledException) { }
			catch (Exception ex) { ShowError(ex); }
			finally { button.IsEnabled = true; ApplyBindings(); }
		};
		return button;
	}
	private Control Field(Func<string> label, Control editor)
	{
		Localize(() => AutomationProperties.SetName(editor, label()));
		StackPanel field = Stack(Local(label, 13, true), editor);
		field.Spacing = 6;
		return field;
	}
	private Control SettingLine(Func<string> label, Func<string> hint, Control editor)
	{
		Localize(() => AutomationProperties.SetName(editor, label()));
		TextBlock description = Secondary(hint);
		Bind(() => description.Text = hint());
		var labels = Stack(Local(label, 13, true), description); labels.Spacing = 4;
		var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16, Margin = new Thickness(0, 10) };
		editor.VerticalAlignment = VerticalAlignment.Center;
		row.Children.Add(labels); Grid.SetColumn(editor, 1); row.Children.Add(editor);
		var line = new Border { Child = row, BorderThickness = new Thickness(0, 1, 0, 0) }; Brush(line, Border.BorderBrushProperty, "SettingsBorderBrush"); return line;
	}
	private void Brush(Control control, AvaloniaProperty property, string key) => control.SetValue(property, SettingsBrushes.Resolve(this, key));
	private void ShowError(Exception ex)
	{
		_status.Text = T("操作失败：", "Operation failed: ") + ex.Message; Brush(_status, TextBlock.ForegroundProperty, "SettingsErrorBrush");
	}
	private void Success(string message)
	{
		_status.Text = message; Brush(_status, TextBlock.ForegroundProperty, "SettingsAccentBrush");
	}
	private async Task<JsonElement> MutateAsync(string command, object args)
	{
		_revision++;
		Task<JsonElement> task = _service.ExecuteAsync(command, args, _lifetime.Token); _operations.Add(task);
		try { return await task; }
		finally { _operations.Remove(task); _revision++; QueueRefresh(); }
	}
	private static JsonElement P(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : default;
	private static string S(JsonElement element, string key, string fallback = "") => P(element, key).ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? fallback : P(element, key).ToString();
	private static double N(JsonElement element, string key, double fallback = 0) => P(element, key).ValueKind == JsonValueKind.Number && P(element, key).TryGetDouble(out double value) ? value : fallback;
	private static bool B(JsonElement element, string key) => P(element, key).ValueKind == JsonValueKind.True;
	private static IEnumerable<JsonElement> Items(JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
	private static string[] Strings(JsonElement element) => Items(element).Select(value => value.GetString() ?? "").ToArray();
	private static string ModelName(string id) => Models.FirstOrDefault(model => model.Id == id).Name ?? id;
	private static bool Installed(JsonElement snapshot, string id) => Items(P(P(snapshot, "models"), "items")).Any(item => S(item, "id") == id && B(item, "installed"));
	private string DisplayNumber(double value, string format = "0.##") => value.ToString(format, CultureInfo.CurrentCulture);
}
