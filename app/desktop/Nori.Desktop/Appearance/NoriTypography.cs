using Avalonia.Media;

namespace Nori.Desktop.Appearance;

/// <summary>
/// 与 Nori.Web 一致的字体栈：拉丁字形使用内嵌的 Plus Jakarta Sans (界面) 与 Nunito (对话气泡)，
/// CJK 字形优先使用已安装的 Noto Sans SC，不存在时回退到系统默认字体。
/// </summary>
internal static class NoriTypography
{
	private const string Embedded = "avares://Nori.Desktop/Assets/Fonts#";
	private static readonly string CjkFallback = $"Noto Sans SC, {FontFamily.Default.Name}";

	internal static readonly FontFamily System = new($"{Embedded}Plus Jakarta Sans, {CjkFallback}");
	internal static readonly FontFamily Conversation = new($"{Embedded}Nunito, {CjkFallback}");
	// 扫描芯片沿用参考项目像素字形；只转换 WOFF2 容器，不改变字形。
	internal static readonly FontFamily Scan = new($"{Embedded}Fusion Pixel 12px Prop zh_hans, {CjkFallback}");

	/// <summary>
	/// 系统界面字体，供对话气泡的「系统字体」使用。
	///
	/// Skia 的默认字体在部分 Linux 桌面上解析为等宽字体 (如 Adwaita Mono)，且不认 sans-serif
	/// 这类通用别名，所以先列出各平台常见的界面无衬线字体；中文不列，交给系统按字符回退，
	/// 这样会用到用户在系统里配置的中文字体。
	/// </summary>
	internal static readonly FontFamily SystemUi = new(
		OperatingSystem.IsWindows() ? $"Segoe UI, {FontFamily.Default.Name}"
		: OperatingSystem.IsMacOS() ? $"Helvetica Neue, {FontFamily.Default.Name}"
		: $"Noto Sans, Cantarell, Ubuntu, DejaVu Sans, Liberation Sans, {FontFamily.Default.Name}");
}
