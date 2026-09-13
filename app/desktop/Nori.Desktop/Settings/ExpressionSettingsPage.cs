using System.Text.Json;
using Nori.Core.Expression;
using Nori.Desktop.Expression;

namespace Nori.Desktop.Settings;

/// <summary>
/// 情绪表达设置页：她的情绪通过哪些方式表现出来。
///
/// **每条通道一个开关，不是一个总开关。** 改整个系统强调色和让托盘换个颜色，打扰程度差着
/// 量级；合成一个开关的结果是用户为了躲开最烦的那条而把全部关掉。
///
/// 每条通道另有一行只读的可用性：开关是「要不要」，那一行是「能不能」。混在一起时用户打开了
/// 开关却没反应，只能怀疑是坏了 —— 而真实原因可能是没装 OpenRGB 或缺音景素材。
/// </summary>
public sealed class ExpressionSettingsPage : SettingsPageBase
{
	/// <summary>创建情绪表达设置页。</summary>
	public ExpressionSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(
			service,
			"expression",
			"core",
			new("情绪表达", "Expression"),
			new(
				"她的情绪通过哪些方式表现出来。每一项可以单独开关。",
				"How her mood shows up. Each channel can be switched on its own."),
			lifetimeToken)
	{
		AddChannel(
			AddSection(new("她自己", "On her")),
			TrayIconChannel.ChannelKey,
			new("托盘图标", "Tray icon"),
			new("托盘上的小圆点随情绪变色。", "The tray dot takes on her mood colour."));

		AddChannel(
			AddSection(new("她自己", "On her")),
			SpeechBorderChannel.ChannelKey,
			new("对话气泡描边", "Speech bubble border"),
			new("她说话时气泡的描边随情绪变色。", "Her speech bubble border takes on her mood colour."));

		SettingsSectionViewModel around = AddSection(new("你周围", "Around you"));
		AddChannel(
			around,
			RgbLightingChannel.ChannelKey,
			new("灯效设备", "Lighting devices"),
			new(
				"键盘、机箱等灯效随情绪变色。需要安装并运行 OpenRGB。",
				"Keyboard and case lighting follow her mood. Requires OpenRGB installed and running."));

		AddChannel(
			around,
			AmbientSoundChannel.ChannelKey,
			new("环境音", "Ambient sound"),
			new(
				"随情绪切换背景音景。需要你自己把音频文件放进数据目录的 soundscapes 文件夹。",
				"Switches the background soundscape. Put your own audio files in the soundscapes folder first."));

		SettingsSectionViewModel desktop = AddSection(new("整个桌面", "Your whole desktop"));
		AddChannel(
			desktop,
			AccentColorChannel.ChannelKey,
			new("系统强调色", "System accent colour"),
			new(
				"Windows 的强调色随情绪变化，你看到的每个窗口都会受影响。关掉时会还原成你原来的颜色。",
				"Windows accent colour follows her mood, affecting every window. Restores your original on switch-off."));

		AddChannel(
			desktop,
			WallpaperChannel.ChannelKey,
			new("桌面壁纸", "Desktop wallpaper"),
			new(
				"壁纸换成随情绪变化的渐变图。关掉时会还原成你原来那张。",
				"Replaces the wallpaper with a mood gradient. Restores your original on switch-off."));
	}

	/// <summary>
	/// 加一条通道：一个开关加一行可用性。
	///
	/// 默认值按侵入等级取 —— 整个桌面那两条默认关。用户没表过态时不该被改掉桌面的颜色。
	/// </summary>
	private void AddChannel(
		SettingsSectionViewModel section, string key, SettingsText label, SettingsText description)
	{
		AddField(
			section,
			key,
			label,
			description,
			SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, DefaultOf(key), "expression", key, "enabled"),
			DefaultOf(key),
			(value, token) => ExecuteAsync(
				"settings_update_expression",
				new {channel = key, enabled = Convert.ToBoolean(value)},
				token));

		AddField(
			section,
			key + "_availability",
			new("　可用性", "　Availability"),
			new("", ""),
			SettingsEditorKind.Text,
			snapshot => AvailabilityText(snapshot, key),
			"",
			(_, _) => Task.FromResult(default(JsonElement)),
			readOnly: true);
	}

	/// <summary>整个桌面那一档默认关，其余默认开。与 <c>AppRuntime</c> 的判据一致。</summary>
	private static bool DefaultOf(string key) =>
		key is not (AccentColorChannel.ChannelKey or WallpaperChannel.ChannelKey);

	private string AvailabilityText(JsonElement snapshot, string key)
	{
		if (SettingsSnapshotReader.Boolean(snapshot, false, "expression", key, "available"))
		{
			return IsEnglish ? "Ready" : "可用";
		}

		// 每条通道说清自己为什么不可用，而不是笼统的「不可用」——「开了没反应」最难排查。
		return key switch
		{
			RgbLightingChannel.ChannelKey => IsEnglish
				? "Unavailable — OpenRGB is not running, or no controllable devices were found"
				: "不可用 —— OpenRGB 没在运行，或没有检测到可控设备",
			AmbientSoundChannel.ChannelKey => IsEnglish
				? "Unavailable — no audio files in the soundscapes folder yet"
				: "不可用 —— soundscapes 文件夹里还没有音频文件",
			SpeechBorderChannel.ChannelKey => IsEnglish
				? "Unavailable — the companion window is not open"
				: "不可用 —— 伴侣视窗没有打开",
			TrayIconChannel.ChannelKey => IsEnglish
				? "Unavailable — the tray icon could not be installed"
				: "不可用 —— 托盘图标没能装上",
			_ => IsEnglish ? "Unavailable on this platform" : "当前平台不支持",
		};
	}
}
