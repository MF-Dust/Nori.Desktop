using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Nori.Desktop.Chat;

/// <summary>原生对话对应 src/assets/style/tokens.ts 的深色令牌；助手浅青气泡不随系统主题变暗。</summary>
internal static class ChatPalette
{
	internal static readonly IBrush Background = Brush("#171B22");
	internal static readonly IBrush Deep = Brush("#1c212a");
	internal static readonly IBrush Panel = Brush("#242f3d");
	internal static readonly IBrush Primary = Brush("#ecf8ff");
	internal static readonly IBrush Body = Brush("#cfdde5");
	internal static readonly IBrush Muted = Brush("#9db2c0");
	internal static readonly IBrush Faint = Brush("#8398a8");
	internal static readonly IBrush Accent = Brush("#7de3ff");
	internal static readonly IBrush Teal = Brush("#5eead4");
	internal static readonly IBrush OnTeal = Brush("#03101c");
	internal static readonly IBrush Danger = Brush("#ff6b72");
	internal static readonly IBrush UserBackground = Brush("#454752");
	internal static readonly IBrush UserText = Brush("#ffffff");
	internal static readonly IBrush AssistantBackground = Brush("#b8d7d8");
	internal static readonly IBrush AssistantText = Brush("#111827");
	// 全局共享只使用不可变画刷，不把第一个窗口的调度线程所有权带到其他窗口或测试线程。
	internal static readonly IBrush Line = new ImmutableSolidColorBrush(Color.FromArgb(31, 125, 227, 255));
	internal static readonly IBrush AssistantBorder = new ImmutableSolidColorBrush(Color.FromArgb(128, 155, 196, 197));
	internal static readonly IBrush Overlay = new ImmutableSolidColorBrush(Color.FromArgb(10, 255, 255, 255));
	internal static readonly IBrush Scrim = new ImmutableSolidColorBrush(Color.FromArgb(190, 0, 0, 0));
	private static IBrush Brush(string color) => new ImmutableSolidColorBrush(Color.Parse(color));
}
