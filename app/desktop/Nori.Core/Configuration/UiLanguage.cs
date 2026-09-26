namespace Nori.Core.Configuration;

/// <summary>界面语言判断。语言码以 <c>en</c> 开头即视为英文，不区分大小写；空值视为非英文。</summary>
public static class UiLanguage
{
	/// <summary>配置里还没有语言键时，与现有窗口读取一致的缺省值。</summary>
	public const string DefaultLanguage = "zh-CN";

	public static bool IsEnglish(string? language) =>
		language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;

	/// <summary>从配置读取界面语言后判断。缺省 <see cref="DefaultLanguage"/>。</summary>
	public static bool IsEnglish(ConfigStore config)
	{
		ArgumentNullException.ThrowIfNull(config);
		return IsEnglish(config.GetStringOr(ConfigStore.KeyLanguage, DefaultLanguage));
	}
}
