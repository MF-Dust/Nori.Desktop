using Live2DCSharpSDK.Framework.Model;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Live2D.Behaviors;

/// <summary>
/// 表情混合与衰减行为
///
/// 对应前端 plugins/expression.ts
/// - 支持 Add / Multiply / Overwrite 三种混合
/// - 每帧基于当前模型值应用非中性表情
/// </summary>
public sealed class ExpressionBehavior(ExpressionStore store) : IBehaviorPlugin
{
	private readonly ExpressionStore _store = store;

	/// <summary>
	/// 将后台准备好的表情定义同步应用到当前模型 (GL/渲染线程调用, 无 I/O)
	///
	/// entry 用当前 Cubism 模型的默认值建基线, 以新模型 ID 注册到共享 store。
	/// 不再存在 fire-and-forget 异步写入, 旧模型的表情任务没有机会污染新模型状态。
	/// </summary>
	public void ApplyPrepared(PreparedModel prepared, CubismModel model)
	{
		Dictionary<string, ExpressionEntry> entryMap = new(StringComparer.OrdinalIgnoreCase);
		foreach (ExpressionGroupDefinition group in prepared.ExpressionGroups)
		{
			foreach (ExpressionParameter parameter in group.Parameters)
			{
				float modelDefault = model.GetParameterDefaultValue(parameter.ParameterId);
				if (!entryMap.TryGetValue(parameter.ParameterId, out ExpressionEntry? entry))
				{
					entry = new ExpressionEntry
					{
						Name = parameter.ParameterId,
						ParameterId = parameter.ParameterId,
						Blend = parameter.Blend,
						CurrentValue = ExpressionEntry.GetNeutralValue(parameter.Blend, modelDefault),
						DefaultValue = ExpressionEntry.GetNeutralValue(parameter.Blend, modelDefault),
						ModelDefault = modelDefault,
						TargetValue = parameter.Value,
					};
					entryMap.Add(parameter.ParameterId, entry);
				}
				else if (entry.TargetValue == 0.0f && parameter.Value != 0.0f)
				{
					entry.TargetValue = parameter.Value;
				}
			}
		}

		_store.RegisterExpressions(prepared.ModelId, prepared.ExpressionGroups, entryMap.Values);
	}

	public void Execute(BehaviorContext ctx)
	{
		if (!ctx.ExpressionEnabled) return;

		var model = ctx.Model.Model;
		foreach (ExpressionEntry entry in _store.Expressions.Values)
		{
			if (entry.IsNeutral()) continue;

			float currentFrameValue = model.GetParameterValue(entry.ParameterId);
			model.SetParameterValue(entry.ParameterId, entry.Apply(currentFrameValue));
		}
	}
}
