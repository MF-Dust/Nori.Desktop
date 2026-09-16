using Live2DCSharpSDK.App;
using Live2DCSharpSDK.Framework.Motion;

namespace Nori.Desktop.Tests;

/// <summary>
/// 待机组里的睡觉动作。
///
/// 两个内置形象都把睡觉动作放进了 <c>Idle</c> 组，而待机是随机挑的，挑中的动作又带
/// <c>Loop</c> —— 播起来不结束，就不会再挑一次。实机表现是她一直闭着眼，而且自动眨眼
/// 看到眼睛已经闭上会主动让路，于是永远睁不开。
///
/// 这一族钉的是判据本身：按曲线取值认，不按文件名认。段编码取自真实的 motion3 文件，
/// 换一种写法（比如只看最大值）会在贝塞尔那一段上失效。
/// </summary>
public sealed class MotionEyeStateTests
{
	private static CubismMotionObj Motion(params CubismMotionObj.Curve[] curves) => new()
	{
		Meta = new CubismMotionObj.MetaObj {Duration = 6, Loop = true},
		Curves = [.. curves],
		UserData = [],
	};

	private static CubismMotionObj.Curve Curve(string id, params float[] segments) => new()
	{
		Target = "Parameter",
		Id = id,
		Segments = [.. segments],
	};

	/// <summary>
	/// ARG Nori 的 sleep_Loop：一段贝塞尔，四个点的取值全是 0。
	///
	/// 数字照抄真实文件 —— 段里既有时间也有取值，只看数值大小会把时间（2 / 4 / 6）
	/// 当成睁眼。
	/// </summary>
	[Fact]
	public void 全程零值的眼部曲线判成闭眼()
	{
		CubismMotionObj motion = Motion(
			Curve("ParamEyeLOpen", 0, 0, 1, 2, 0, 4, 0, 6, 0),
			Curve("ParamEyeROpen", 0, 0, 1, 2, 0, 4, 0, 6, 0));

		Assert.True(MotionEyeState.KeepsEyesClosed(motion));
	}

	/// <summary>ARG Nori 的 01_Idle_Loop：取值在 0.9 上下，不能判成闭眼。</summary>
	[Fact]
	public void 正常待机曲线不判成闭眼()
	{
		CubismMotionObj motion = Motion(
			Curve("ParamEyeLOpen", 0, 1, 1, 0.144f, 1, 0.289f, 0.908f, 0.433f, 0.908f));

		Assert.False(MotionEyeState.KeepsEyesClosed(motion));
	}

	/// <summary>中途睁开也不算：判据是**全程**闭着。</summary>
	[Fact]
	public void 中途睁眼的不算闭眼动作()
	{
		CubismMotionObj motion = Motion(
			Curve("ParamEyeLOpen", 0, 0, 0, 1, 0, 0, 2, 1));

		Assert.False(MotionEyeState.KeepsEyesClosed(motion));
	}

	/// <summary>不管眼睛的动作不该被摘出去 —— 它的眼睛交给别的层决定。</summary>
	[Fact]
	public void 没有眼部曲线时不判成闭眼()
	{
		CubismMotionObj motion = Motion(Curve("ParamMouthOpenY", 0, 0, 0, 1, 1));

		Assert.False(MotionEyeState.KeepsEyesClosed(motion));
	}

	/// <summary>
	/// 段类型认不出时不下结论。
	///
	/// 判据不成立就不该把一个动作从候选里摘掉：摘错了的后果是那个动作再也不会播，
	/// 而且不会有任何报错。
	/// </summary>
	[Fact]
	public void 段编码异常时不判成闭眼()
	{
		CubismMotionObj motion = Motion(Curve("ParamEyeLOpen", 0, 0, 9, 1, 0));

		Assert.False(MotionEyeState.KeepsEyesClosed(motion));
	}

	/// <summary>两只眼睛都要闭着。只闭一只是眨眼或挤眼，不是睡觉。</summary>
	[Fact]
	public void 只闭一只眼不算闭眼动作()
	{
		CubismMotionObj motion = Motion(
			Curve("ParamEyeLOpen", 0, 0, 1, 2, 0, 4, 0, 6, 0),
			Curve("ParamEyeROpen", 0, 1, 1, 2, 1, 4, 1, 6, 1));

		Assert.False(MotionEyeState.KeepsEyesClosed(motion));
	}
}
