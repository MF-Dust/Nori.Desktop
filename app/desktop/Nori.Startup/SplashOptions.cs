namespace Nori.Startup;

/// <summary>启动画面布局与自定义背景的规则；宿主校验、设置页与启动画面进程共用这一份。</summary>
internal static class SplashOptions
{
	/// <summary>默认布局 (Photoshop 风格)。</summary>
	internal const string DefaultTheme = "character";

	/// <summary>背景图片大小上限。</summary>
	internal const long MaxBackgroundBytes = 16L * 1024 * 1024;

	/// <summary>受支持的布局，顺序即设置页顺序。</summary>
	internal static readonly IReadOnlyList<string> Themes =
		[DefaultTheme, "android-studio", "microsoft-365", "office-legacy", "island", "alls", "minimal"];

	/// <summary>背景图片允许的扩展名 (小写)。</summary>
	internal static readonly IReadOnlyList<string> BackgroundExtensions = [".png", ".jpg", ".jpeg", ".webp"];

	/// <summary>是否为受支持的布局。</summary>
	internal static bool IsTheme(string? theme) => theme is not null && Themes.Contains(theme);

	/// <summary>只有 Photoshop 作品区与 Android Studio 背景区显示自定义背景。</summary>
	internal static bool SupportsBackground(string? theme) => theme is DefaultTheme or "android-studio";

	/// <summary>本地绝对路径；拒绝相对路径、网址与 UNC 网络共享。</summary>
	internal static bool IsLocalAbsolutePath(string path) =>
		!path.Replace('\\', '/').StartsWith("//", StringComparison.Ordinal) && Path.IsPathFullyQualified(path);

	/// <summary>扩展名是否为受支持的图片格式。</summary>
	internal static bool HasImageExtension(string path) =>
		BackgroundExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}
