using Nori.Desktop.Live2D.Behaviors;

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

	[Fact]
	public void 眨眼只将动画系数乘到捕获基线()
	{
		Assert.Equal(0.2f, AutoBlinkBehavior.ApplyBlinkFactor(0.4f, 0.5f), 5);
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
