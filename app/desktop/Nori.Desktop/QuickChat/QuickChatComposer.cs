using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Nori.Desktop.QuickChat;

/// <summary>单行 QuickChat 输入框；IME 预编辑期间的回车交给输入法处理。</summary>
internal sealed class QuickChatComposer : TextBox
{
	private TextPresenter? _presenter;
	internal event Action? SendRequested;
	internal event Action? EscapeRequested;
	protected override Type StyleKeyOverride => typeof(TextBox);

	protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
	{
		base.OnApplyTemplate(e);
		_presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
	}

	internal static bool ShouldSend(Key key, KeyModifiers modifiers, bool composing) =>
		key == Key.Enter && modifiers == KeyModifiers.None && !composing;

	protected override void OnKeyDown(KeyEventArgs e)
	{
		if (!e.Handled && ShouldSend(e.Key, e.KeyModifiers, !string.IsNullOrEmpty(_presenter?.PreeditText)))
		{
			e.Handled = true;
			SendRequested?.Invoke();
			return;
		}
		if (!e.Handled && e.Key == Key.Escape)
		{
			e.Handled = true;
			EscapeRequested?.Invoke();
			return;
		}
		base.OnKeyDown(e);
	}
}
