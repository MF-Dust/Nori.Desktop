using Live2DCSharpSDK.Framework.Motion;

namespace Live2DCSharpSDK.App;

/// <summary>
/// 判断一个动作是不是「全程闭着眼」。
///
/// ── 为什么需要它 ──────────────────────────────────────────────────────────
/// 两个内置形象都把睡觉动作放进了 <c>Idle</c> 组：ARG Nori 是 <c>sleep_Loop</c>，
/// Nori 是 <c>00_Sleep</c> 与 <c>00_IdleCameraEyeClosed</c>。而待机动作是**随机挑**的，
/// 挑中睡觉动作的概率因此是 1/2 到 1/4；这些动作又都带 <c>Loop: true</c>，播完不会结束，
/// 也就不会再挑一次 —— 表现为她坐在桌面上一直闭着眼，而自动眨眼看到眼睛已经闭上会
/// 主动让路（那是给「表情故意闭眼」留的），于是永远睁不开。
///
/// ── 判据 ──────────────────────────────────────────────────────────────────
/// 不按文件名认（不同模型的命名没有约定），按曲线取值：动作里 <c>ParamEyeLOpen</c> /
/// <c>ParamEyeROpen</c> 的每一个关键点都不超过 <see cref="ClosedThreshold"/> 就算闭眼动作。
/// 阈值与 <c>AutoBlinkBehavior</c> 里判断「眼睛已经闭上」的那个取同一个数。
/// </summary>
public static class MotionEyeState
{
	/// <summary>眼睛开合参数小于等于这个值即视为闭着。</summary>
	public const float ClosedThreshold = 0.15f;

	private const string EyeLeft = "ParamEyeLOpen";
	private const string EyeRight = "ParamEyeROpen";

	/// <summary>
	/// 这个动作是不是从头到尾闭着眼。
	///
	/// 没有眼部曲线时返回 false —— 那说明这个动作不管眼睛，交给别的层去决定。
	/// 曲线数据不合规时也返回 false：判据不成立时不该把一个动作从候选里摘掉。
	/// </summary>
	public static bool KeepsEyesClosed(CubismMotionObj? data)
	{
		if (data?.Curves is null) return false;

		bool sawEyeCurve = false;
		foreach (CubismMotionObj.Curve curve in data.Curves)
		{
			if (curve.Target != "Parameter") continue;
			if (curve.Id != EyeLeft && curve.Id != EyeRight) continue;

			sawEyeCurve = true;
			if (!AllValuesClosed(curve.Segments)) return false;
		}
		return sawEyeCurve;
	}

	/// <summary>
	/// 曲线上每个关键点的取值都闭着眼吗。
	///
	/// motion3 的段编码：开头是第一个点的 (时间, 取值)，之后每段先一个类型码，再跟这一段
	/// 的点 —— 贝塞尔三个点（六个数），线性 / 阶梯 / 反向阶梯各一个点（两个数）。
	/// 取值都在每个点的第二个数上。编码与 <see cref="CubismMotion"/> 的解析一致，
	/// 那里是这份数据唯一的其它读者。
	/// </summary>
	private static bool AllValuesClosed(IReadOnlyList<float>? segments)
	{
		if (segments is null || segments.Count < 2) return false;
		if (segments[1] > ClosedThreshold) return false;

		int position = 2;
		while (position < segments.Count)
		{
			int points = (int)segments[position] switch
			{
				0 or 2 or 3 => 1,   // 线性 / 阶梯 / 反向阶梯
				1 => 3,             // 贝塞尔
				_ => -1,
			};
			// 认不出的类型码说明这份数据不是我们理解的形状，不据此下结论。
			if (points < 0) return false;

			position++;
			for (int index = 0; index < points; index++)
			{
				if (position + 1 >= segments.Count) return false;
				if (segments[position + 1] > ClosedThreshold) return false;
				position += 2;
			}
		}
		return true;
	}
}
