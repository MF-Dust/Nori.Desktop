using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Desktop.Bridge;
using Nori.Desktop.Tray;

namespace Nori.Desktop.QuickChat;

/// <summary>托盘与原生设置共用配置键和快照通知，不维护第二份开关。</summary>
internal static class QuickChatSettings
{
	internal static bool IsEnabled(AppServices services) => services.Config.GetQuickChatEnabled();

	internal static async Task SetEnabledAsync(AppServices services, bool enabled)
	{
		await Task.Run(() => services.Config.Set(ConfigStore.KeyQuickChatEnabled, new ConfigValue.Boolean(enabled)), services.ShutdownToken).ConfigureAwait(false);
		services.Runtime?.InvalidateSnapshot("general");
		await Dispatcher.UIThread.InvokeAsync(TrayMenu.Refresh);
	}
}
