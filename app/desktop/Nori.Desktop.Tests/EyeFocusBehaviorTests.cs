using System.Reflection;
using Nori.Desktop.Live2D.Behaviors;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

public sealed class EyeFocusBehaviorTests
{
	[Live2DAssetsFact]
	public void 有效视线跟随时待机扫视不改写眼球()
	{
		using var animation = CreateModel();
		BehaviorContext ctx = CreateContext(animation);
		ctx.EyeTrackingEnabled = true;
		ctx.EyeFocusSourceActive = true;
		animation.Model.SetParameterValue(ctx.ModelParameters.EyeBallXIndex, 0.6f);
		animation.Model.SetParameterValue(ctx.ModelParameters.EyeBallYIndex, -0.4f);

		new EyeFocusBehavior().Execute(ctx);

		Assert.Equal(0.6f, animation.Model.GetParameterValue(ctx.ModelParameters.EyeBallXIndex));
		Assert.Equal(-0.4f, animation.Model.GetParameterValue(ctx.ModelParameters.EyeBallYIndex));
	}

	[Live2DAssetsFact]
	public void 没有指针来源或关闭跟随时保留待机扫视()
	{
		using var animation = CreateModel();
		BehaviorContext ctx = CreateContext(animation);
		foreach (var (tracking, source) in new[] { (true, false), (false, true) })
		{
			ctx.EyeTrackingEnabled = tracking;
			ctx.EyeFocusSourceActive = source;
			animation.Model.SetParameterValue(ctx.ModelParameters.EyeBallXIndex, 0.6f);
			animation.Model.SetParameterValue(ctx.ModelParameters.EyeBallYIndex, -0.4f);
			var behavior = new EyeFocusBehavior();

			behavior.Execute(ctx);

			// 读取本次随机目标来验证合成结果，不依赖某个随机值或真实等待。
			var target = ((float X, float Y))typeof(EyeFocusBehavior)
				.GetField("_focusTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(behavior)!;
			double next = (double)typeof(EyeFocusBehavior)
				.GetField("_nextSaccadeAt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(behavior)!;
			Assert.True(next > ctx.Now);
			Assert.Equal(0.6f + (target.X - 0.6f) * 0.3f,
				animation.Model.GetParameterValue(ctx.ModelParameters.EyeBallXIndex), 5);
			Assert.Equal(-0.4f + (target.Y + 0.4f) * 0.3f,
				animation.Model.GetParameterValue(ctx.ModelParameters.EyeBallYIndex), 5);
		}
	}

	private static AnimatedModel CreateModel() => new(
		File.ReadAllBytes(PreparedModelAssetsTests.FindFixture("nori", "Nori.moc3")),
		ModelDefinition.Parse("""{"FileReferences":{"Moc":"内存"}}"""u8.ToArray()),
		new Dictionary<string, MotionClip>()) { RandomMotion = false };

	private static BehaviorContext CreateContext(AnimatedModel animation)
	{
		var parameters = new ModelParameters();
		parameters.BindModel(animation.Model);
		return new BehaviorContext
		{
			Model = animation,
			ModelParameters = parameters,
			IsIdleMotion = true,
			ForceIdleEyeAnimation = true,
			Now = 100,
		};
	}
}
