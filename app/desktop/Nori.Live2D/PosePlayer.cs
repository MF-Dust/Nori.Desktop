namespace Nori.Live2D;

/// <summary>通过同名参数选择互斥部件；输出不透明度及关联部件同步均属于当前模型。</summary>
public sealed class PosePlayer
{
	private readonly NativeModel _model;
	private readonly float _fadeInTime;
	private readonly BoundPart[][] _groups;
	private bool _initialized;

	public PosePlayer(NativeModel model, PoseDefinition definition)
	{
		_model = model;
		_fadeInTime = definition.FadeInTime;
		_groups = definition.Groups.Select(group => group.Select(part => new BoundPart(
			model.GetParameterIndex(part.Id), model.GetPartIndex(part.Id),
			part.Links.Select(id => (model.GetParameterIndex(id), model.GetPartIndex(id))).ToArray())).ToArray()).ToArray();
	}

	public void Update(float deltaSeconds)
	{
		if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0)
			throw new ArgumentOutOfRangeException(nameof(deltaSeconds), "姿势时间增量必须是有限非负数");
		if (!_initialized)
		{
			foreach (BoundPart[] group in _groups)
			{
				for (int i = 0; i < group.Length; i++)
				{
					BoundPart part = group[i];
					_model.SetParameterValue(part.Parameter, i == 0 ? 1 : 0);
					_model.SetPartOpacity(part.Part, i == 0 ? 1 : 0);
					foreach (var link in part.Links) _model.SetParameterValue(link.Parameter, 1);
				}
			}
			_initialized = true;
		}

		foreach (BoundPart[] group in _groups)
		{
			int selected = -1;
			for (int i = 0; i < group.Length; i++)
				if (_model.GetParameterValue(group[i].Parameter) > 0.001f) { selected = i; break; }
			// 没有选中参数时恢复首项，避免整组消失。
			float opacity = selected < 0 || _fadeInTime == 0 ? 1
				: Math.Clamp(_model.GetPartOpacity(group[selected].Part) + deltaSeconds / _fadeInTime, 0, 1);
			if (selected < 0) selected = 0;
			float backgroundLimit = BackgroundOpacityLimit(opacity);
			for (int i = 0; i < group.Length; i++)
			{
				BoundPart part = group[i];
				_model.SetPartOpacity(part.Part, i == selected ? opacity
					: Math.Min(_model.GetPartOpacity(part.Part), backgroundLimit));
			}
		}
		// 所有主部件计算完毕后同步，关联部件不参与组内选择。
		foreach (BoundPart[] group in _groups)
			foreach (BoundPart part in group)
				foreach (var link in part.Links)
					_model.SetPartOpacity(link.Part, _model.GetPartOpacity(part.Part));
	}

	internal static float BackgroundOpacityLimit(float foreground)
	{
		// 两层合成的透光量为 (1-front)*(1-back)。过渡期间将透光量控制在 0.15 以内。
		float remaining = 1 - foreground;
		return remaining <= 0 ? 0 : Math.Max(remaining, 1 - 0.15f / remaining);
	}

	private sealed record BoundPart(int Parameter, int Part, (int Parameter, int Part)[] Links);
}
