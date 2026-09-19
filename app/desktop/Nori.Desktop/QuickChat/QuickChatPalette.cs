using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Nori.Desktop.QuickChat;

/// <summary>与 Nori.Web 03e4880a 对话浮层一致的不可变色板。</summary>
internal static class QuickChatPalette
{
	internal static readonly IBrush Dark = Brush("#56565F");
	internal static readonly IBrush White = Brush("#FEFEFE");
	internal static readonly IBrush Mint = Brush("#D2E8E9");
	internal static readonly IBrush MintLight = Brush("#CAF5F1");
	internal static readonly IBrush Player = Brush("#F03D3D47");
	internal static readonly IBrush PlayerEnd = Brush("#F54A4A55");
	internal static readonly IBrush PlayerText = Brush("#E8E8EC");
	internal static readonly IBrush Error = Brush("#F03D3D47");
	internal static readonly IBrush Composer = Brush("#D956565F");
	internal static readonly IBrush ComposerFocused = Brush("#F0FEFEFE");
	internal static readonly IBrush ComposerBorder = Brush("#20D2E8E9");
	internal static readonly IBrush ComposerFocusedBorder = Brush("#80D2E8E9");
	internal static readonly IBrush Placeholder = Brush("#80D2E8E9");
	internal static readonly IBrush FocusedPlaceholder = Brush("#990E7490");
	internal static readonly IBrush DisabledArrow = Brush("#6056565F");
	internal static readonly IBrush BubbleBorder = Brush("#70FEFEFE");
	internal static readonly IBrush PlayerBorder = Brush("#308B8B99");
	internal static readonly IBrush Approval = Brush("#F2FEFEFE");
	internal static readonly IBrush ApprovalText = Brush("#3D3D47");
	internal static readonly IBrush ApprovalDanger = Brush("#B01521");

	private static IBrush Brush(string value) => new ImmutableSolidColorBrush(Color.Parse(value));
}
