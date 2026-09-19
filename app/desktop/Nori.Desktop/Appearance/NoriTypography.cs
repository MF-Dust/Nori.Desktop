using Avalonia.Media;

namespace Nori.Desktop.Appearance;

/// <summary>优先使用已安装的 Noto Sans SC，缺失时回退当前平台默认字体。</summary>
internal static class NoriTypography
{
	internal static readonly FontFamily System = new($"Noto Sans SC, {FontFamily.Default.Name}");
	internal static readonly FontFamily Conversation = System;
}
