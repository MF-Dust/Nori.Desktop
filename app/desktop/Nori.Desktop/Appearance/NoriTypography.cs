using Avalonia.Media;

namespace Nori.Desktop.Appearance;

/// <summary>
/// 与 Nori.Web 一致的字体栈：拉丁字形使用内嵌的 Plus Jakarta Sans (界面) 与 Nunito (对话气泡)，
/// 中日文字形逐字回退到已安装的 Noto Sans SC / JP，再回退到平台默认中文字体。
/// </summary>
internal static class NoriTypography
{
	private const string Embedded = "avares://Nori.Desktop/Assets/Fonts#";
	private static readonly string CjkFallback = $"Noto Sans SC, Noto Sans JP, Microsoft YaHei UI, PingFang SC, {FontFamily.Default.Name}";

	internal static readonly FontFamily System = new($"{Embedded}Plus Jakarta Sans, {CjkFallback}");
	internal static readonly FontFamily Conversation = new($"{Embedded}Nunito, {CjkFallback}");
}
