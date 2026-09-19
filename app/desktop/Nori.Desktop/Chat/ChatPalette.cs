using Avalonia.Media;
using Nori.Desktop.Appearance;

namespace Nori.Desktop.Chat;

/// <summary>原生界面与网页共用生成的不可变主题画刷。</summary>
internal static class ChatPalette
{
	internal static readonly IBrush Background = NoriThemeTokens.Brush("bg-base");
	internal static readonly IBrush Deep = NoriThemeTokens.Brush("bg-deep");
	internal static readonly IBrush Panel = NoriThemeTokens.Brush("bg-panel");
	internal static readonly IBrush Primary = NoriThemeTokens.Brush("text-primary");
	internal static readonly IBrush Body = NoriThemeTokens.Brush("text-body");
	internal static readonly IBrush Muted = NoriThemeTokens.Brush("text-muted");
	internal static readonly IBrush Faint = NoriThemeTokens.Brush("text-faint");
	internal static readonly IBrush Accent = NoriThemeTokens.Brush("nori-teal-bright");
	internal static readonly IBrush Teal = NoriThemeTokens.Brush("nori-teal");
	internal static readonly IBrush OnTeal = NoriThemeTokens.Brush("on-teal");
	internal static readonly IBrush Danger = NoriThemeTokens.Brush("danger-text");
	internal static readonly IBrush UserBackground = NoriThemeTokens.Brush("chat-user-bg");
	internal static readonly IBrush UserText = NoriThemeTokens.Brush("chat-user-text");
	internal static readonly IBrush AssistantBackground = NoriThemeTokens.Brush("chat-ai-bg");
	internal static readonly IBrush AssistantText = NoriThemeTokens.Brush("chat-ai-text");
	internal static readonly IBrush Line = NoriThemeTokens.Brush("line-subtle");
	internal static readonly IBrush AssistantBorder = NoriThemeTokens.Brush("chat-ai-border");
	internal static readonly IBrush Overlay = NoriThemeTokens.Brush("overlay-4");
	internal static readonly IBrush Scrim = NoriThemeTokens.Brush("scrim");
}
