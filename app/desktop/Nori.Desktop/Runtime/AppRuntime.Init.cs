using Nori.Core.Configuration;
using Nori.Core.Live2D;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Runtime;

/// <summary>
/// 初始化窗口交接给主界面的那一步。
///
/// 提到这里是因为现在有**两条路**会走它：原生初始化窗口直接调，而
/// <c>init_enter_main</c> 这条桥命令仍然留着（WebView 版本尚未从代码里移除，
/// 且外部调用方可能仍在用）。同一段判定写两份必然漂。
/// </summary>
public sealed partial class AppRuntime
{
	/// <summary>
	/// 打开主界面，按配置决定要不要把伴侣一起召出来，然后收起初始化窗口。
	///
	/// **必须在 UI 线程上调。** 三个窗口的显隐是一组，拆开会出现「主界面已经开了、
	/// 初始化窗口还压在上面」这种中间态。
	/// </summary>
	public void EnterMainFromInit()
	{
		string? modelId = SupportedModelIds.Normalize(
			Services.Config.GetStringOr(ConfigStore.KeySelectedModel, ""));
		bool modelReady = modelId is not null && IsModelInstalled(modelId);
		bool autoSummon = Services.Config.GetBoolOr("pet_auto_summon", true);

		Services.Windows.Show(WindowLabels.Main);
		// 安全模式下不召伴侣：那一档的语义是「什么都不做」。
		if (modelReady && autoSummon && !Services.SafeMode) Services.Windows.Show(WindowLabels.Pet);
		else Services.Windows.Hide(WindowLabels.Pet);
		Services.Windows.Hide(WindowLabels.Init);
	}

	/// <summary>原生初始化窗口用的异步壳：它在 UI 线程上，直接同步走完即可。</summary>
	public Task EnterMainFromInitAsync()
	{
		EnterMainFromInit();
		return Task.CompletedTask;
	}
}
