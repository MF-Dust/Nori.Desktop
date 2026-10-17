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
	internal static readonly IBrush TealPressed = NoriThemeTokens.Brush("nori-teal-pressed");
	internal static readonly IBrush OnTeal = NoriThemeTokens.Brush("on-teal");
	/// <summary>危险文字色；填充与描边用 <see cref="DangerFill"/>。</summary>
	internal static readonly IBrush Danger = NoriThemeTokens.Brush("danger-text");
	internal static readonly IBrush DangerFill = NoriThemeTokens.Brush("danger");
	internal static readonly IBrush Disabled = NoriThemeTokens.Brush("text-disabled");
	internal static readonly IBrush UserBackground = NoriThemeTokens.Brush("chat-user-bg");
	internal static readonly IBrush UserText = NoriThemeTokens.Brush("chat-user-text");
	internal static readonly IBrush AssistantBackground = NoriThemeTokens.Brush("chat-ai-bg");
	internal static readonly IBrush AssistantText = NoriThemeTokens.Brush("chat-ai-text");
	internal static readonly IBrush Line = NoriThemeTokens.Brush("line-subtle");
	internal static readonly IBrush AssistantBorder = NoriThemeTokens.Brush("chat-ai-border");
	internal static readonly IBrush UserBorder = NoriThemeTokens.Brush("chat-user-border");
	/// <summary>web conversation-panel：135° 薄荷渐变 (#d2e8e9 → #caf5f1 → #d2e8e9)。</summary>
	internal static readonly IBrush AssistantGradient = Gradient(("chat-ai-bg", 0), ("chat-ai-bg-end", 0.5), ("chat-ai-bg", 1));
	/// <summary>web conversation-panel：135° 深灰渐变 (#3d3d47 → #4a4a55)。</summary>
	internal static readonly IBrush UserGradient = Gradient(("chat-user-bg", 0), ("chat-user-bg-end", 1));
	internal static readonly BoxShadows AssistantShadow = BoxShadows.Parse("0 4 24 0 " + NoriThemeTokens.Value("chat-ai-glow") + ", 0 2 8 0 #0F000000");
	internal static readonly BoxShadows UserShadow = BoxShadows.Parse("0 4 16 0 #44000000, 0 2 6 0 #1F000000");
	internal static readonly IBrush Overlay = NoriThemeTokens.Brush("overlay-4");
	internal static readonly IBrush Scrim = NoriThemeTokens.Brush("scrim");

	private static IBrush Gradient(params (string Token, double Offset)[] stops)
	{
		LinearGradientBrush brush = new()
		{
			StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
			EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
		};
		foreach ((string token, double offset) in stops) brush.GradientStops.Add(new GradientStop(NoriThemeTokens.Color(token), offset));
		return brush.ToImmutable();
	}
}
