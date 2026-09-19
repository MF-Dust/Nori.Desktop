using Avalonia.Media;
using Avalonia.Media.Immutable;
using Nori.Desktop.Appearance;

namespace Nori.Desktop.QuickChat;

/// <summary>局部聊天材质复用统一令牌，透明度保留原有浮层形态。</summary>
internal static class QuickChatPalette
{
	internal static readonly IBrush Dark = new ImmutableSolidColorBrush(Tint("chat-ai-text", 255));
	internal static readonly IBrush White = new ImmutableSolidColorBrush(Tint("chat-white", 255));
	internal static readonly IBrush Mint = new ImmutableSolidColorBrush(Tint("chat-ai-bg", 255));
	internal static readonly IBrush MintLight = new ImmutableSolidColorBrush(Tint("chat-ai-bg-end", 255));
	internal static readonly IBrush Player = new ImmutableSolidColorBrush(Tint("chat-user-bg", 240));
	internal static readonly IBrush PlayerEnd = new ImmutableSolidColorBrush(Tint("chat-user-bg-end", 245));
	internal static readonly IBrush PlayerText = new ImmutableSolidColorBrush(Tint("chat-user-text", 255));
	internal static readonly IBrush Error = new ImmutableSolidColorBrush(Tint("chat-user-bg", 240));
	internal static readonly IBrush Composer = new ImmutableSolidColorBrush(Tint("chat-composer", 217));
	internal static readonly IBrush ComposerFocused = new ImmutableSolidColorBrush(Tint("chat-white", 240));
	internal static readonly IBrush ComposerBorder = new ImmutableSolidColorBrush(Tint("chat-ai-bg", 32));
	internal static readonly IBrush ComposerFocusedBorder = new ImmutableSolidColorBrush(Tint("chat-ai-bg", 128));
	internal static readonly IBrush Placeholder = new ImmutableSolidColorBrush(Tint("chat-ai-bg", 128));
	internal static readonly IBrush FocusedPlaceholder = new ImmutableSolidColorBrush(Tint("chat-focus-placeholder", 255));
	internal static readonly IBrush DisabledArrow = new ImmutableSolidColorBrush(Tint("chat-composer", 96));
	internal static readonly IBrush BubbleBorder = new ImmutableSolidColorBrush(Tint("chat-white", 112));
	internal static readonly IBrush PlayerBorder = new ImmutableSolidColorBrush(Tint("text-faint", 48));
	internal static readonly IBrush Approval = new ImmutableSolidColorBrush(Tint("chat-white", 242));
	internal static readonly IBrush ApprovalText = new ImmutableSolidColorBrush(Tint("chat-user-bg", 255));
	internal static readonly IBrush ApprovalDanger = new ImmutableSolidColorBrush(Tint("chat-approval-danger", 255));

	internal static Color Tint(string key, byte alpha = 255)
	{
		Color color = NoriThemeTokens.Color(key);
		return Color.FromArgb(alpha, color.R, color.G, color.B);
	}
}
