namespace Nori.Live2D;

/// <summary>
/// 判断一个动作是不是「全程闭着眼」，并在待机随机挑选时避开这类动作。
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
/// <c>ParamEyeROpen</c> 的每一个点（含贝塞尔控制点）都不超过 <see cref="ClosedThreshold"/>
/// 就算闭眼动作。阈值与 <c>AutoBlinkBehavior</c> 里判断「眼睛已经闭上」的那个取同一个数。
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
	/// </summary>
	public static bool KeepsEyesClosed(MotionClip clip)
	{
		ArgumentNullException.ThrowIfNull(clip);
		bool sawEyeCurve = false;
		foreach (MotionCurve curve in clip.Curves)
		{
			if (curve.Target != "Parameter" || (curve.Id != EyeLeft && curve.Id != EyeRight)) continue;
			sawEyeCurve = true;
			if (curve.MaxValue > ClosedThreshold) return false;
		}
		return sawEyeCurve;
	}

	/// <summary>
	/// 从 <paramref name="count"/> 个候选里随机挑一个下标，<b>排除全程闭眼的那些</b>。
	///
	/// 睡觉动作不是不能播，但那该由宿主按「用户离开了多久」决定，不能是启动时的一次
	/// 掷骰子。整组都闭眼时照常随机 —— 那是模型作者的安排，不该在这里推翻。
	/// </summary>
	public static int PickIndex(IReadOnlyList<bool> eyesClosed, Random random)
	{
		ArgumentNullException.ThrowIfNull(eyesClosed);
		ArgumentNullException.ThrowIfNull(random);
		if (eyesClosed.Count == 0) throw new ArgumentException("候选不能为空。", nameof(eyesClosed));
		List<int> open = [];
		for (int index = 0; index < eyesClosed.Count; index++)
		{
			if (!eyesClosed[index]) open.Add(index);
		}
		return open.Count == 0 ? random.Next(eyesClosed.Count) : open[random.Next(open.Count)];
	}
}
