using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Nori.Desktop.Appearance;

/// <summary>
/// 应用级外观：强调色共享画刷、界面缩放与对话气泡资源。
///
/// 每个 Application 一份 —— 无界面测试的每个用例都会新建 App，画刷不能跨会话共用。
/// 强调色画刷是注册进 Application.Resources 的可变实例：换色时就地改颜色，样式里的
/// StaticResource 别名、DynamicResource 与 ChatPalette 引用的都是同一批画刷，已打开的
/// 窗口不重建也会变色。因此窗口级主题文件不得再合并一份令牌字典，否则资源查找会停在
/// 那份不会变色的画刷上。
/// </summary>
internal sealed class NoriAppearance
{
	/// <summary>普通窗口框架绑定的界面缩放变换；100% 时为 null。</summary>
	internal const string UiScaleTransformKey = "NoriUiScaleTransform";

	/// <summary>对话气泡字体。</summary>
	internal const string BubbleFontFamilyKey = "NoriBubbleFontFamily";

	/// <summary>对话气泡字号倍率，标准档为 1。</summary>
	internal const string BubbleFontScaleKey = "NoriBubbleFontScale";

	/// <summary>大号气泡相对标准档的倍率。</summary>
	internal const double LargeBubbleScale = NoriMetrics.FontLg / NoriMetrics.FontBase;

	private static readonly ConditionalWeakTable<Application, NoriAppearance> Instances = new();

	private readonly Application _application;
	private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);
	private AppearancePreferences? _preferences;

	private NoriAppearance(Application application)
	{
		Dispatcher.UIThread.VerifyAccess();
		_application = application;
		foreach (string key in NoriAccentPresets.Keys)
		{
			SolidColorBrush brush = new(NoriAccentPresets.Color(NoriAccentPresets.Default, key));
			_brushes[key] = brush;
			application.Resources[ResourceName(key) + "Brush"] = brush;
		}
		Apply(AppearancePreferences.Default);
	}

	/// <summary>当前应用的外观；没有 Application，或首次访问不在 UI 线程时为 null。</summary>
	internal static NoriAppearance? Current
	{
		get
		{
			if (Application.Current is not { } application) return null;
			if (Instances.TryGetValue(application, out NoriAppearance? appearance)) return appearance;
			return Dispatcher.UIThread.CheckAccess() ? Install(application) : null;
		}
	}

	/// <summary>
	/// 为应用安装外观资源。必须在 UI 线程、在加载任何引用令牌的样式之前调用，
	/// 否则样式里的 StaticResource 别名会先解析到别的画刷上。
	/// </summary>
	internal static NoriAppearance Install(Application application)
	{
		ArgumentNullException.ThrowIfNull(application);
		return Instances.GetValue(application, static app => new NoriAppearance(app));
	}

	/// <summary>偏好变化并完成应用后在 UI 线程触发；首次安装不触发。</summary>
	internal event Action? Changed;

	/// <summary>当前生效的偏好。</summary>
	internal AppearancePreferences Preferences => _preferences ?? AppearancePreferences.Default;

	/// <summary>界面缩放倍率。</summary>
	internal double UiScale => Preferences.Scale;

	/// <summary>品牌强调色 (nori-teal)。</summary>
	internal IBrush Teal => _brushes["nori-teal"];

	/// <summary>高亮强调色 (nori-teal-bright)。</summary>
	internal IBrush TealBright => _brushes["nori-teal-bright"];

	/// <summary>柔和强调色 (nori-teal-soft)。</summary>
	internal IBrush TealSoft => _brushes["nori-teal-soft"];

	/// <summary>强调色悬停与按下 (nori-teal-pressed)。</summary>
	internal IBrush TealPressed => _brushes["nori-teal-pressed"];

	/// <summary>强调色填充上的文字 (on-teal)。</summary>
	internal IBrush OnTeal => _brushes["on-teal"];

	/// <summary>强调色 10% 叠在玻璃上的选中底色 (bg-selection)。</summary>
	internal IBrush Selection => _brushes["bg-selection"];

	/// <summary>当前预设下某个强调令牌的颜色，键见 <see cref="NoriAccentPresets.Keys"/>。</summary>
	internal Color Color(string key) => _brushes[key].Color;

	/// <summary>画刷是否为本外观持有、会随强调色就地变色的实例。</summary>
	internal bool Owns(IBrush? brush) => brush is SolidColorBrush solid && _brushes.ContainsValue(solid);

	/// <summary>应用偏好，只改动变化的部分；必须在 UI 线程调用。</summary>
	internal void Apply(AppearancePreferences preferences)
	{
		Dispatcher.UIThread.VerifyAccess();
		ArgumentNullException.ThrowIfNull(preferences);
		AppearancePreferences? previous = _preferences;
		if (previous == preferences) return;
		IResourceDictionary resources = _application.Resources;
		if (previous?.Accent != preferences.Accent) ApplyAccent(resources, preferences.Accent);
		if (previous?.UiScale != preferences.UiScale)
		{
			resources[UiScaleTransformKey] = preferences.UiScale == AppearancePreferences.DefaultUiScale
				? null
				: new ImmutableTransform(Matrix.CreateScale(preferences.Scale, preferences.Scale));
		}
		if (previous?.BubbleFont != preferences.BubbleFont) resources[BubbleFontFamilyKey] = BubbleFontFamily(preferences.BubbleFont);
		if (previous?.BubbleSize != preferences.BubbleSize)
			resources[BubbleFontScaleKey] = preferences.BubbleSize == AppearancePreferences.BubbleSizeLarge ? LargeBubbleScale : 1d;
		MotionPreference.SetMode(preferences.Motion);
		_preferences = preferences;
		if (previous is not null) Changed?.Invoke();
	}

	private void ApplyAccent(IResourceDictionary resources, string accent)
	{
		string id = NoriAccentPresets.Contains(accent) ? accent : NoriAccentPresets.Default;
		foreach ((string key, SolidColorBrush brush) in _brushes)
		{
			Color color = NoriAccentPresets.Color(id, key);
			brush.Color = color;
			resources[ResourceName(key) + "Color"] = color;
		}
		// Fluent 模板经 SystemAccentColor* 派生的画刷：应用资源优先于样式资源，覆盖 NoriControls 的静态别名。
		resources["SystemAccentColor"] = Color("nori-teal");
		for (int level = 1; level <= 3; level++)
		{
			resources["SystemAccentColorDark" + level] = Color("nori-teal-pressed");
			resources["SystemAccentColorLight" + level] = Color("nori-teal-soft");
		}
	}

	private static FontFamily BubbleFontFamily(string font) => font switch
	{
		AppearancePreferences.BubbleFontInterface => NoriTypography.System,
		AppearancePreferences.BubbleFontSystem => NoriTypography.SystemUi,
		_ => NoriTypography.Conversation,
	};

	/// <summary>令牌键对应的资源名前缀，与 sync-design-tokens.mjs 的生成规则一致：nori-teal-bright → NoriNoriTealBright。</summary>
	private static string ResourceName(string key) =>
		"Nori" + string.Concat(key.Split('-', '.').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
