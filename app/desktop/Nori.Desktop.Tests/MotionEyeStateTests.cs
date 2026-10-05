using System.Text;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

/// <summary>
/// 待机组里的睡觉动作。
///
/// 两个内置形象都把睡觉动作放进了 <c>Idle</c> 组，而待机是随机挑的，挑中的动作又带
/// <c>Loop</c> —— 播起来不结束，就不会再挑一次。实机表现是她一直闭着眼，而且自动眨眼
/// 看到眼睛已经闭上会主动让路，于是永远睁不开。
///
/// 这一族钉的是判据本身：按曲线取值认，不按文件名认；贝塞尔控制点也算在内。
/// </summary>
public sealed class MotionEyeStateTests
{
	private static MotionClip Motion(params string[] curves) => MotionClip.Parse(Encoding.UTF8.GetBytes(
		$$"""{"Version":3,"Meta":{"Duration":6,"Loop":true},"Curves":[{{string.Join(",", curves)}}]} """));

	private static string Curve(string id, string segments) =>
		$$"""{"Target":"Parameter","Id":"{{id}}","Segments":[{{segments}}]} """;

	/// <summary>
	/// ARG Nori 的 sleep_Loop：一段贝塞尔，四个点的取值全是 0。
	///
	/// 数字照抄真实文件 —— 段里既有时间也有取值，只看数值大小会把时间（2 / 4 / 6）
	/// 当成睁眼。
	/// </summary>
	[Fact]
	public void 全程零值的眼部曲线判成闭眼()
	{
		MotionClip motion = Motion(
			Curve("ParamEyeLOpen", "0,0,1,2,0,4,0,6,0"),
			Curve("ParamEyeROpen", "0,0,1,2,0,4,0,6,0"));

		Assert.True(MotionEyeState.KeepsEyesClosed(motion));
	}

	/// <summary>ARG Nori 的 01_Idle_Loop：取值在 0.9 上下，不能判成闭眼。</summary>
	[Fact]
	public void 正常待机曲线不判成闭眼()
	{
		MotionClip motion = Motion(Curve("ParamEyeLOpen", "0,1,1,0.144,1,0.289,0.908,0.433,0.908"));

		Assert.False(MotionEyeState.KeepsEyesClosed(motion));
	}

	/// <summary>中途睁开也不算：判据是**全程**闭着。</summary>
	[Fact]
	public void 中途睁眼的不算闭眼动作()
	{
		MotionClip motion = Motion(Curve("ParamEyeLOpen", "0,0,0,1,0,0,2,1"));

		Assert.False(MotionEyeState.KeepsEyesClosed(motion));
	}

	/// <summary>不管眼睛的动作不该被摘出去 —— 它的眼睛交给别的层决定。</summary>
	[Fact]
	public void 没有眼部曲线时不判成闭眼()
	{
		MotionClip motion = Motion(Curve("ParamMouthOpenY", "0,0,0,1,1"));

		Assert.False(MotionEyeState.KeepsEyesClosed(motion));
	}

	/// <summary>两只眼睛都要闭着。只闭一只是眨眼或挤眼，不是睡觉。</summary>
	[Fact]
	public void 只闭一只眼不算闭眼动作()
	{
		MotionClip motion = Motion(
			Curve("ParamEyeLOpen", "0,0,1,2,0,4,0,6,0"),
			Curve("ParamEyeROpen", "0,1,1,2,1,4,1,6,1"));

		Assert.False(MotionEyeState.KeepsEyesClosed(motion));
	}

	/// <summary>候选里有睁眼的动作时，闭眼的永远挑不到。</summary>
	[Fact]
	public void 挑选时排除闭眼动作()
	{
		bool[] closed = [true, false, true, false];
		Random random = new(7);
		for (int run = 0; run < 200; run++)
			Assert.Contains(MotionEyeState.PickIndex(closed, random), new[] {1, 3});
	}

	/// <summary>整组都闭眼是模型作者的安排：照常随机，不挑空也不报错。</summary>
	[Fact]
	public void 整组都闭眼时照常随机()
	{
		bool[] closed = [true, true, true];
		Random random = new(7);
		int[] picked = [.. Enumerable.Range(0, 200).Select(_ => MotionEyeState.PickIndex(closed, random))];
		Assert.All(picked, index => Assert.InRange(index, 0, 2));
		Assert.True(picked.Distinct().Count() > 1);
	}
}
