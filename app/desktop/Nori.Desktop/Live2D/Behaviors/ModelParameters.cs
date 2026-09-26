using Live2DCSharpSDK.Framework.Model;

namespace Nori.Desktop.Live2D.Behaviors;

/// <summary>
/// 模型参数基准值
///
/// 对应前端 stores/model-parameters.ts
/// </summary>
public sealed class ModelParameters
{
	public int AngleXIndex { get; private set; } = -1;
	public int AngleYIndex { get; private set; } = -1;
	public int AngleZIndex { get; private set; } = -1;
	public int BodyAngleXIndex { get; private set; } = -1;
	public int EyeBallXIndex { get; private set; } = -1;
	public int EyeBallYIndex { get; private set; } = -1;
	public int LeftEyeOpenIndex { get; private set; } = -1;
	public int RightEyeOpenIndex { get; private set; } = -1;
	public int MouthOpenIndex { get; private set; } = -1;

	/// <summary>当前索引是否属于已绑定且尚未解绑的模型。</summary>
	public bool IsBound { get; private set; }

	public float AngleX { get; set; }
	public float AngleY { get; set; }
	public float AngleZ { get; set; }
	public float LeftEyeOpen { get; set; } = 1.0f;
	public float RightEyeOpen { get; set; } = 1.0f;
	public float LeftEyeSmile { get; set; }
	public float RightEyeSmile { get; set; }
	public float LeftEyebrowLR { get; set; }
	public float RightEyebrowLR { get; set; }
	public float LeftEyebrowY { get; set; }
	public float RightEyebrowY { get; set; }
	public float LeftEyebrowAngle { get; set; }
	public float RightEyebrowAngle { get; set; }
	public float LeftEyebrowForm { get; set; }
	public float RightEyebrowForm { get; set; }
	public float MouthOpen { get; set; }
	public float MouthForm { get; set; }
	public float Cheek { get; set; }
	public float BodyAngleX { get; set; }
	public float BodyAngleY { get; set; }
	public float BodyAngleZ { get; set; }
	public float Breath { get; set; }

	/// <summary>按当前 Cubism 模型解析固定行为和视线动画使用的索引。</summary>
	public void BindModel(CubismModel model)
	{
		AngleXIndex = model.GetParameterIndex("ParamAngleX");
		AngleYIndex = model.GetParameterIndex("ParamAngleY");
		AngleZIndex = model.GetParameterIndex("ParamAngleZ");
		BodyAngleXIndex = model.GetParameterIndex("ParamBodyAngleX");
		EyeBallXIndex = model.GetParameterIndex("ParamEyeBallX");
		EyeBallYIndex = model.GetParameterIndex("ParamEyeBallY");
		LeftEyeOpenIndex = model.GetParameterIndex("ParamEyeLOpen");
		RightEyeOpenIndex = model.GetParameterIndex("ParamEyeROpen");
		MouthOpenIndex = model.GetParameterIndex("ParamMouthOpenY");
		IsBound = true;
	}

	/// <summary>清除模型解绑后不再有效的固定参数索引。</summary>
	public void UnbindModel()
	{
		IsBound = false;
		AngleXIndex = -1;
		AngleYIndex = -1;
		AngleZIndex = -1;
		BodyAngleXIndex = -1;
		EyeBallXIndex = -1;
		EyeBallYIndex = -1;
		LeftEyeOpenIndex = -1;
		RightEyeOpenIndex = -1;
		MouthOpenIndex = -1;
	}

	public void Reset()
	{
		AngleX = 0;
		AngleY = 0;
		AngleZ = 0;
		LeftEyeOpen = 1.0f;
		RightEyeOpen = 1.0f;
		LeftEyeSmile = 0;
		RightEyeSmile = 0;
		LeftEyebrowLR = 0;
		RightEyebrowLR = 0;
		LeftEyebrowY = 0;
		RightEyebrowY = 0;
		LeftEyebrowAngle = 0;
		RightEyebrowAngle = 0;
		LeftEyebrowForm = 0;
		RightEyebrowForm = 0;
		MouthOpen = 0;
		MouthForm = 0;
		Cheek = 0;
		BodyAngleX = 0;
		BodyAngleY = 0;
		BodyAngleZ = 0;
		Breath = 0;
	}
}
