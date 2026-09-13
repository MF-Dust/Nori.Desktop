using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Nori.Desktop.Chat;

/// <summary>先检查原生 IME 预编辑状态，再处理 Enter；Shift+Enter 仍由 TextBox 插入换行。</summary>
internal sealed class ChatComposer : TextBox
{
	private TextPresenter? _presenter;
	internal event Action? SendRequested;
	protected override Type StyleKeyOverride => typeof(TextBox);
	protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
	{
		base.OnApplyTemplate(e); _presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
	}
	internal static bool ShouldSend(Key key, KeyModifiers modifiers, bool composing) => key == Key.Enter && !modifiers.HasFlag(KeyModifiers.Shift) && !composing;
	protected override void OnKeyDown(KeyEventArgs e)
	{
		if (!e.Handled && ShouldSend(e.Key, e.KeyModifiers, !string.IsNullOrEmpty(_presenter?.PreeditText)))
		{
			e.Handled = true; SendRequested?.Invoke(); return;
		}
		base.OnKeyDown(e);
	}
}
