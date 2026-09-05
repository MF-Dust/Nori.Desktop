namespace Nori.Desktop.Live2D.Behaviors;

/// <summary>
/// 空闲动画禁用行为
///
/// 对应前端 plugins/idle-disable.ts
/// </summary>
public sealed class IdleDisableBehavior : IBehaviorPlugin
{
	public void Execute(BehaviorContext ctx)
	{
		if (!ctx.IdleAnimationEnabled && ctx.IsIdleMotion)
		{
			ctx.Model.StopAllMotions();
		}
	}
}
