using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nori.Core.Configuration;
using Nori.Desktop.Appearance;
using Nori.Desktop.Chat;

namespace Nori.Desktop.Settings;

/// <summary>对话气泡预览。字体和字号绑定个性化设置，会随设置实时变化。</summary>
internal sealed class BubblePreviewEditor : StackPanel, ISettingsFieldEditor
{
	private readonly SettingsFieldViewModel _field;
	private readonly TextBlock _assistant;
	private readonly TextBlock _user;

	/// <summary>创建气泡预览。</summary>
	public BubblePreviewEditor(SettingsFieldViewModel field)
	{
		_field = field;
		Spacing = NoriMetrics.Spacing2;
		HorizontalAlignment = HorizontalAlignment.Stretch;
		IsEnabled = true;
		_assistant = BubbleTypography.Apply(new TextBlock
		{
			Foreground = ChatPalette.AssistantText,
			TextWrapping = TextWrapping.Wrap,
		}, NoriMetrics.FontBase, 20.8);
		_user = BubbleTypography.Apply(new TextBlock
		{
			Foreground = ChatPalette.UserText,
			TextWrapping = TextWrapping.Wrap,
		}, NoriMetrics.FontBase, 20.8);
		Children.Add(Bubble(false, _assistant));
		Children.Add(Bubble(true, _user));
		ApplyLanguage();
	}

	/// <inheritdoc />
	public void Refresh(string? propertyName)
	{
		if (propertyName is null or nameof(SettingsFieldViewModel.Label)) ApplyLanguage();
	}

	private static Border Bubble(bool user, TextBlock text) => new()
	{
		MaxWidth = 320,
		Padding = new Thickness(NoriMetrics.Spacing3, NoriMetrics.Spacing2),
		CornerRadius = user
			? new CornerRadius(NoriMetrics.RadiusXl, NoriMetrics.RadiusXl, NoriMetrics.RadiusXs, NoriMetrics.RadiusXl)
			: new CornerRadius(NoriMetrics.RadiusXl, NoriMetrics.RadiusXl, NoriMetrics.RadiusXl, NoriMetrics.RadiusXs),
		Background = user ? ChatPalette.UserGradient : ChatPalette.AssistantGradient,
		BorderBrush = user ? ChatPalette.UserBorder : ChatPalette.AssistantBorder,
		BorderThickness = new Thickness(1),
		BoxShadow = user ? ChatPalette.UserShadow : ChatPalette.AssistantShadow,
		HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
		Child = text,
	};

	private void ApplyLanguage()
	{
		if (English())
		{
			_assistant.Text = "Good morning! Your 9:30 meeting is coming up — don't forget the notes.";
			_user.Text = "Got it, thanks Nori :)";
			return;
		}
		_assistant.Text = "早上好！今天 9:30 有个会议，记得带上 Notes 哦～";
		_user.Text = "收到，谢谢 Nori :)";
	}

	private bool English() => UiLanguage.IsEnglish(_field.Language);
}
