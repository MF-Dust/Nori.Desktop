namespace Nori.Core.Vision;

/// <summary>一张已经编码好、可直接交给模型的截图。</summary>
public sealed record CapturedScreen
{
	/// <summary>编码后的字节。</summary>
	public required byte[] Bytes { get; init; }

	/// <summary>媒体类型，例如 `image/jpeg`。</summary>
	public required string MimeType { get; init; }

	/// <summary>编码后的像素宽高，可能已被缩小。</summary>
	public required int Width { get; init; }

	/// <summary>编码后的像素高度。</summary>
	public required int Height { get; init; }

	/// <summary>实际截到的窗口标题。</summary>
	public required string Window { get; init; }
}

/// <summary>
/// 截屏结果。
///
/// 失败用 <see cref="Error"/> 表达而不是抛异常：截不到窗口是常态（窗口已关闭、被最小化、
/// 受保护内容），调用方要把原因原样交给模型让它换一个窗口，而不是中断整轮。
/// </summary>
public sealed record ScreenCaptureResult
{
	/// <summary>成功时的截图；失败为 null。</summary>
	public CapturedScreen? Screen { get; init; }

	/// <summary>失败原因；成功为 null。</summary>
	public string? Error { get; init; }

	/// <summary>成功结果。</summary>
	public static ScreenCaptureResult Ok(CapturedScreen screen) => new() {Screen = screen};

	/// <summary>失败结果。</summary>
	public static ScreenCaptureResult Fail(string error) => new() {Error = error};
}

/// <summary>
/// 屏幕内容读取。
///
/// **只截当前前台窗口**，既不截整屏也不截指定的后台窗口。三条理由，从强到弱：
///
/// 1. 后台窗口用户此刻看不见，截它比截他正看着的东西更具侵入性；
/// 2. 整屏会把密码管理器、聊天窗口一并收进来，而用户授权时无从预料那一刻屏幕上有什么；
/// 3. 底层的 `WindowsWindowService.ValidateTarget` 本来就要求目标是前台窗口。
///
/// 因此没有「选哪个窗口」这个参数 —— 一个注定失败的参数只会让模型反复重试。
/// </summary>
public interface IScreenCapture
{
	/// <summary>本平台是否支持截屏。</summary>
	bool IsAvailable { get; }

	/// <summary>截取当前前台窗口。</summary>
	ScreenCaptureResult Capture();
}

/// <summary>
/// 把截图交给视觉模型，问一个问题，拿回一段文字。
///
/// 结果是文字而不是图片：图片若进了对话历史，之后每一轮都要重新发一遍，上下文成本会一直
/// 累积；而这条链路上的调用不写入历史，问完即弃。
/// </summary>
public interface IVisionAnalyzer
{
	/// <summary>当前模型配置是否可用。</summary>
	bool IsConfigured { get; }

	/// <summary>就这张截图回答一个问题。</summary>
	Task<string> AnalyzeAsync(string question, CapturedScreen screen, CancellationToken cancellationToken);
}
