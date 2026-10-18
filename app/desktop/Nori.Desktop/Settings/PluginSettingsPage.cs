using Nori.Desktop.Windows;

namespace Nori.Desktop.Settings;

/// <summary>只保存宿主导航元数据；插件工厂和控件仍由统一页面控制器持有。</summary>
internal sealed class PluginSettingsPage(SettingsService service, PluginPageController controller, PluginPageInfo info, CancellationToken lifetimeToken)
	: SettingsPageBase(service, NavigationKey(info), NavigationGroup, new(info.Title, info.Title), new(info.PluginId, info.PluginId), lifetimeToken)
{
	internal const string NavigationGroup = "plugin-pages";
	internal PluginPageInfo Info { get; } = info;

	internal static string NavigationKey(PluginPageInfo info) => $"plugin:{info.PluginId}:{info.Id}";

	internal PluginPageHost Open() => controller.OpenSettingsPage(Info);
}
