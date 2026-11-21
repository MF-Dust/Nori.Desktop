namespace Nori.Live2D;

/// <summary>在动作结果上叠加周期正弦呼吸；每个模型独立计时。</summary>
public sealed class BreathPlayer
{
	private readonly NativeModel _model;
	private readonly (int Index, float Offset, float Amplitude, float Period, float Weight)[] _waves;
	private double _elapsed;

	public BreathPlayer(NativeModel model, params (string Id, float Offset, float Amplitude, float Period, float Weight)[] waves)
	{
		_model = model;
		_waves = waves.Select(wave =>
		{
			if (!float.IsFinite(wave.Offset) || !float.IsFinite(wave.Amplitude)
				|| !float.IsFinite(wave.Period) || wave.Period <= 0
				|| !float.IsFinite(wave.Weight) || wave.Weight < 0 || wave.Weight > 1)
				throw new ArgumentException("呼吸参数必须有限，周期大于零且权重位于 0 到 1", nameof(waves));
			return (model.GetParameterIndex(wave.Id), wave.Offset, wave.Amplitude, wave.Period, wave.Weight);
		}).ToArray();
	}

	public void Update(float deltaSeconds)
	{
		if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0)
			throw new ArgumentOutOfRangeException(nameof(deltaSeconds), "呼吸时间增量必须是有限非负数");
		_elapsed += deltaSeconds;
		foreach (var wave in _waves)
		{
			double phase = (_elapsed % wave.Period) / wave.Period * Math.Tau;
			_model.AddParameterValue(wave.Index, wave.Offset + wave.Amplitude * (float)Math.Sin(phase), wave.Weight);
		}
	}
}
