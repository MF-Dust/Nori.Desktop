using Nori.Desktop.Live2D.Behaviors;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

public sealed class Live2DBehaviorLogicTests
{
	[Theory]
	[InlineData(ExpressionBlendMode.Add, 0.4f, 0.0f)]
	[InlineData(ExpressionBlendMode.Multiply, 0.4f, 1.0f)]
	[InlineData(ExpressionBlendMode.Overwrite, 0.4f, 0.4f)]
	public void 表情中性值按混合模式计算(ExpressionBlendMode blend, float modelDefault, float expected)
	{
		ExpressionEntry entry = new()
		{
			Name = "ParamTest",
			ParameterId = "ParamTest",
			Blend = blend,
			CurrentValue = ExpressionEntry.GetNeutralValue(blend, modelDefault),
			ModelDefault = modelDefault,
		};

		Assert.Equal(expected, entry.NeutralValue);
		Assert.True(entry.IsNeutral());
	}

	[Theory]
	[InlineData(ExpressionBlendMode.Add, 0.25f, 0.5f, 0.75f)]
	[InlineData(ExpressionBlendMode.Multiply, 2.0f, 0.5f, 1.0f)]
	[InlineData(ExpressionBlendMode.Overwrite, 0.75f, 0.5f, 0.75f)]
	public void 表情混合使用当前帧值(ExpressionBlendMode blend, float expressionValue, float currentFrame, float expected)
	{
		ExpressionEntry entry = new()
		{
			Name = "ParamTest",
			ParameterId = "ParamTest",
			Blend = blend,
			CurrentValue = expressionValue,
			ModelDefault = 0.4f,
		};

		Assert.Equal(expected, entry.Apply(currentFrame));
	}

	[Fact]
	public void 同参数切换表情组时同步切换混合模式()
	{
		ExpressionEntry entry = new()
		{
			Name = "ParamTest",
			ParameterId = "ParamTest",
			Blend = ExpressionBlendMode.Add,
			CurrentValue = 0.0f,
			ModelDefault = 0.4f,
		};
		ExpressionStore store = new();
		store.RegisterExpressions(
			"test",
			[
				new ExpressionGroupDefinition
				{
					Name = "AddGroup",
					Parameters = [new ExpressionParameter
					{
						ParameterId = "ParamTest",
						Blend = ExpressionBlendMode.Add,
						Value = 0.25f,
					}],
				},
				new ExpressionGroupDefinition
				{
					Name = "MultiplyGroup",
					Parameters = [new ExpressionParameter
					{
						ParameterId = "ParamTest",
						Blend = ExpressionBlendMode.Multiply,
						Value = 2.0f,
					}],
				},
			],
			[entry]);

		Assert.True(store.Play("AddGroup"));
		Assert.Equal(ExpressionBlendMode.Add, entry.Blend);
		Assert.Equal(0.75f, entry.Apply(0.5f));

		Assert.True(store.Set("MultiplyGroup", 2.0f));
		Assert.Equal(ExpressionBlendMode.Multiply, entry.Blend);
		Assert.Equal(1.0f, entry.Apply(0.5f));
	}

	[Fact]
	public void 表情重新注册会推进参数索引缓存版本()
	{
		ExpressionStore store = new();
		ExpressionGroupDefinition[] groups =
		[
			new ExpressionGroupDefinition
			{
				Name = "Group",
				Parameters = [new ExpressionParameter
				{
					ParameterId = "ParamA",
					Blend = ExpressionBlendMode.Add,
					Value = 0.25f,
				}],
			},
		];
		ExpressionEntry[] entries =
		[
			new ExpressionEntry
			{
				Name = "ParamA",
				ParameterId = "ParamA",
				Blend = ExpressionBlendMode.Add,
				CurrentValue = 0.0f,
				ModelDefault = 0.0f,
			},
		];

		store.RegisterExpressions("model-a", groups, entries);
		long firstRevision = store.Revision;
		store.RegisterExpressions("model-b", groups, entries);

		Assert.True(store.Revision > firstRevision);
		Assert.Equal("model-b", store.ModelId);
	}

	[Theory]
	[InlineData(0.4f, 0.5f, 0.2f)]
	[InlineData(2.0f, 0.75f, 1.5f)]
	[InlineData(1.5f, 1.0f, 1.5f)]
	[InlineData(1.5f, 2.0f, 1.5f)]
	[InlineData(1.5f, -1.0f, 0.0f)]
	public void 眨眼仅限制系数而不截断当前帧基线(float baseline, float factor, float expected)
	{
		Assert.Equal(expected, AutoBlinkBehavior.ApplyBlinkFactor(baseline, factor), 5);
	}

	[Fact]
	public void 眨眼状态按随机间隔推进且完成后重新等待()
	{
		AutoBlinkBehavior blink = new();
		Assert.Equal(1, blink.UpdateBlink(2.9));
		Assert.Equal(1, blink.UpdateBlink(8));
		Assert.Equal(0.25f, blink.UpdateBlink(0.0375), 5);
		Assert.Equal(0, blink.UpdateBlink(0.0375));
		Assert.InRange(blink.UpdateBlink(0.075), 0.0625f, 0.25f);
		Assert.Equal(1, blink.UpdateBlink(0.3));
		Assert.Equal(1, blink.UpdateBlink(2.9));
		Assert.Equal(1, blink.UpdateBlink(8));
		Assert.Equal(0, blink.UpdateBlink(0.075));
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void 重置或解绑会取消活动眨眼并重新等待完整间隔(bool opening, bool unbound)
	{
		AutoBlinkBehavior blink = new();
		blink.UpdateBlink(8);
		blink.UpdateBlink(opening ? 0.075 : 0.0375);
		if (unbound)
		{
			BehaviorContext ctx = new() { IsIdleMotion = true, AutoBlinkEnabled = true };
			blink.Execute(ctx);
			Assert.False(ctx.Handled);
		}
		else blink.Reset();

		Assert.Equal(1, blink.UpdateBlink(0.016));
		Assert.Equal(1, blink.UpdateBlink(2.9));
		Assert.Equal(1, blink.UpdateBlink(8));
		Assert.Equal(0, blink.UpdateBlink(0.075));
	}

	[Fact]
	public void 解绑后行为不再按参数名重新写入()
	{
		ModelParameters parameters = new();
		parameters.UnbindModel();
		Assert.False(parameters.IsBound);
		Assert.Equal(-1, parameters.MouthOpenIndex);

		BehaviorContext ctx = new()
		{
			ModelParameters = parameters,
			IsIdleMotion = true,
			AutoBlinkEnabled = true,
			ForceIdleEyeAnimation = true,
			BeatSyncEnabled = true,
			IdleAnimationEnabled = true,
			LipSyncEnabled = true,
			ExpressionEnabled = true,
		};
		new AutoBlinkBehavior().Execute(ctx);
		new EyeFocusBehavior().Execute(ctx);
		new BeatSyncBehavior().Execute(ctx);
		LipSyncBehavior lipSync = new();
		lipSync.SetNowSpeaking(true);
		lipSync.SetMouthOpen(1);
		lipSync.Execute(ctx);
		ExpressionBehavior expressions = new(new ExpressionStore());
		expressions.UnbindModel();
		expressions.Execute(ctx);
		Assert.False(ctx.Handled);
	}
}
