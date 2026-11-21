using Avalonia.Controls;
using Nori.Desktop.Bridge;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Models;

/// <summary>原生模型窗口的宿主服务。</summary>
public sealed class ModelService(AppServices services, Window owner)
	: NativeBridgeServiceBase(services, new WindowContext(owner, WindowLabels.Models))
{
	protected override string StateNotificationFailureMessage => "模型窗口状态通知失败";

	protected override bool IsStateChangingCommand(string command) => IsMutatingCommand(command);

	protected override bool IsBackgroundCommand(string command) => command is "model_get_meta";

	internal static bool IsMutatingCommand(string command) => command is
		"model_select" or "model_import_local" or "model_download_remote"
		or "model_set_display" or "model_set_behavior" or "model_set_interactions";
}
