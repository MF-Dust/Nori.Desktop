using System.Text.Json.Nodes;
using Nori.Core.Vision;
using static Nori.Core.Tools.ToolProperty;
using static Nori.Core.Tools.ToolRegistration;

namespace Nori.Core.Tools;

/// <summary>
/// 读屏工具。
///
/// 链路是「截一个窗口 → 交给视觉模型问一个问题 → 拿回文字」。返回文字而不是图片，是因为
/// 本轮的工具结果会进对话历史：图片一旦进去，之后每一轮都要重新发一遍，上下文成本持续累积，
/// 而绝大多数后续轮次并不需要再看那张图。
///
/// 代价是模型看到的是一段描述而不是像素，想看别的细节要再截一次。对「这个报错什么意思」
/// 「这个界面怎么操作」这类用户主动发问的场景，这个代价可以接受。
///
/// **不做实时。** 抓画面是毫秒级而模型往返是秒级，这个差距不是工程能优化掉的。本工具只服务
/// 于用户问起的那一刻。
/// </summary>
public static class ScreenTools
{
	/// <summary>本组工具名。</summary>
	public const string ReadScreenName = "readScreen";

	/// <summary>问题留空时的默认问法。</summary>
	private const string DefaultQuestion = "描述这个窗口里现在显示的内容，重点说清文字信息和当前状态。";

	/// <summary>
	/// 注册本组工具，注册前先注销。
	///
	/// 平台不支持截屏、或模型配置不可用时整组不注册：暴露一件必定失败的工具会让模型反复重试。
	/// 用户是否授权由调用方判断，此处只看能力是否具备。
	/// </summary>
	public static void RegisterAll(ToolRegistry registry, IScreenCapture? capture, IVisionAnalyzer? analyzer)
	{
		ArgumentNullException.ThrowIfNull(registry);

		registry.Unregister(ReadScreenName);
		if (capture is not {IsAvailable: true} || analyzer is not {IsConfigured: true}) return;

		Register(
			registry,
			ReadScreenName,
			"看一眼用户当前正在看的窗口，回答关于画面内容的问题。只能看前台窗口，看不了后台窗口"
				+ "或整个屏幕。每次调用都会请求用户确认，不要反复调用。",
			"confirm",
			Schema(Text("question", "想从画面里知道什么；省略则给出整体描述", required: false)),
			(args, context) => ReadAsync(capture, analyzer, args, context.CancellationToken));
	}

	private static async Task<object?> ReadAsync(
		IScreenCapture capture,
		IVisionAnalyzer analyzer,
		JsonNode? args,
		CancellationToken cancellationToken)
	{
		string question = args?["question"]?.GetValue<string>()?.Trim() is {Length: > 0} asked
			? asked
			: DefaultQuestion;

		ScreenCaptureResult result = capture.Capture();
		if (result.Screen is not {} screen) throw new InvalidOperationException(result.Error ?? "截屏失败");

		string answer = await analyzer.AnalyzeAsync(question, screen, cancellationToken).ConfigureAwait(false);
		return new
		{
			window = screen.Window,
			question,
			answer,
			width = screen.Width,
			height = screen.Height,
		};
	}

}
