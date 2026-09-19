using Avalonia.Media;
using Nori.Desktop.Appearance;

namespace Nori.Desktop.Tests;

public partial class BridgeCommandsTests
{
	[NativeChatVisualFact]
	public async Task NativeTypographyPrefersNotoAndFallsBackToPlatformDefault()
	{
		await VisualUiSession.Value.Dispatch(() =>
		{
			Assert.Equal(NoriTypography.System, NoriTypography.Conversation);
			Assert.Equal("Noto Sans SC", NoriTypography.System.FamilyNames.PrimaryFamilyName);
			Assert.Contains(FontFamily.Default.Name, NoriTypography.System.FamilyNames);
			// 用不存在的优先字体模拟未安装 Noto 的机器，实际解析结果必须等同平台默认。
			FontFamily missingPreferred = new($"Nori Missing Font 732EADE1, {FontFamily.Default.Name}");
			GlyphTypeface fallback = new Typeface(missingPreferred).GlyphTypeface;
			GlyphTypeface platformDefault = new Typeface(FontFamily.Default).GlyphTypeface;
			Assert.Equal(platformDefault.FamilyName, fallback.FamilyName);
			GlyphTypeface preferred = new Typeface(NoriTypography.System).GlyphTypeface;
			Assert.NotEqual((ushort)0, preferred.CharacterToGlyphMap.GetGlyph('N'));
			return Task.CompletedTask;
		}, CancellationToken.None);
	}
}
