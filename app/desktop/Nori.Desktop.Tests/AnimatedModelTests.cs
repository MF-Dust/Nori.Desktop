using System.Numerics;
using System.Text;
using System.Text.Json;
using Nori.Desktop.Live2D.Behaviors;
using Nori.Live2D;

namespace Nori.Desktop.Tests;

public sealed class AnimatedModelTests
{
	private static byte[] Moc() => File.ReadAllBytes(PreparedModelAssetsTests.FindFixture("nori", "Nori.moc3"));
	private static ModelDefinition Definition(string groups = "{}") => ModelDefinition.Parse(Encoding.UTF8.GetBytes(
		"{\"FileReferences\":{\"Moc\":\"不存在.moc3\",\"Motions\":" + groups + "}}"));
	private static MotionClip Clip() => MotionClip.Parse("""
		{"Version":3,"Meta":{"Duration":1,"Loop":false,"FadeInTime":0,"FadeOutTime":0},
		"Curves":[{"Target":"Parameter","Id":"ParamAngleX","Segments":[0,10,0,1,10]}],
		"UserData":[{"Time":0.5,"Value":"事件"}]}
		"""u8.ToArray());

	[Live2DAssetsFact]
	public void 动作快照在行为之前保存且最终回调在呼吸姿势之后()
	{
		PoseDefinition pose = PoseDefinition.Parse("""{"Groups":[[{"Id":"TestPose"}]]}"""u8.ToArray());
		using var animation = new AnimatedModel(Moc(), Definition("""{"Idle":[{"File":"idle.motion3.json"}]}"""),
			new Dictionary<string, MotionClip> { ["Idle_0"] = Clip() }, pose: pose) { RandomMotion = false };
		NativeModel model = animation.Model;
		int angle = model.GetParameterIndex("ParamAngleX"), breath = model.GetParameterIndex("ParamBreath");
		List<string> order = [];
		animation.BeforeEffects = () =>
		{
			order.Add("行为");
			Assert.Equal(10, model.GetParameterValue(angle));
			model.AddParameterValue(angle, 2);
			model.SetParameterValue(breath, 0);
		};
		animation.AfterEffects = () =>
		{
			order.Add("最终");
			Assert.Equal(12, model.GetParameterValue(angle));
			Assert.Equal(0.25f, model.GetParameterValue(breath));
			Assert.Equal(1, model.GetPartOpacity(model.GetPartIndex("TestPose")));
			model.AddParameterValue(angle, 3);
		};
		Assert.NotNull(animation.StartMotion("Idle", 0, MotionPriority.Force));
		animation.Update(0);
		Assert.Equal(15, model.GetParameterValue(angle));
		animation.StopAllMotions();
		animation.Update(0);
		Assert.Equal(15, model.GetParameterValue(angle));
		Assert.Equal(new[] { "行为", "最终", "行为", "最终" }, order);
	}

	[Live2DAssetsFact]
	public void 关闭待机在动作更新前恢复闭眼与快照且不保存上一帧最终效果()
	{
		MotionClip clip = EyeClip("""
			{"Target":"Model","Id":"EyeBlink","Segments":[0,0]},
			{"Target":"Parameter","Id":"ParamEyeLOpen","Segments":[0,1]}
			""");
		ModelDefinition definition = ModelDefinition.Parse("""
			{"FileReferences":{"Moc":"内存","Motions":{"Idle":[{"File":"内存"}]}},
			"Groups":[{"Target":"Parameter","Name":"EyeBlink","Ids":["ParamEyeLOpen","ParamEyeROpen"]}]}
			"""u8.ToArray());
		using var animation = new AnimatedModel(Moc(), definition,
			new Dictionary<string, MotionClip> { ["Idle_0"] = clip }) { RandomMotion = false };
		NativeModel model = animation.Model;
		int left = model.GetParameterIndex("ParamEyeLOpen"), right = model.GetParameterIndex("ParamEyeROpen");
		int angle = model.GetParameterIndex("ParamAngleX"), mouth = model.GetParameterIndex("ParamMouthOpenY");
		int events = 0;
		animation.MotionEvent += _ => events++;
		animation.AfterEffects = () =>
		{
			model.SetParameterValue(angle, 24);
			model.SetParameterValue(mouth, 0.8f);
		};
		MotionPlayback playback = Assert.IsType<MotionPlayback>(animation.StartMotion("Idle", 0, MotionPriority.Force));
		animation.Update(0);
		Assert.Equal(0, model.GetParameterValue(left));
		Assert.Equal(0, model.GetParameterValue(right));
		Assert.Equal(24, model.GetParameterValue(angle));

		animation.StopIdleMotion();
		Assert.True(playback.IsFinished);
		Assert.True(animation.IsMotionFinished);
		Assert.Null(animation.CurrentMotionGroup);
		animation.AfterEffects = null;
		animation.Update(0);
		Assert.Equal(model.GetParameterDefaultValue(left), model.GetParameterValue(left));
		Assert.Equal(model.GetParameterDefaultValue(right), model.GetParameterValue(right));
		Assert.Equal(10, model.GetParameterValue(angle));
		Assert.Equal(model.GetParameterDefaultValue(mouth), model.GetParameterValue(mouth));
		animation.BeforeEffects = () =>
		{
			Assert.Equal(10, model.GetParameterValue(angle));
			Assert.Equal(model.GetParameterDefaultValue(left), model.GetParameterValue(left));
			Assert.Equal(model.GetParameterDefaultValue(right), model.GetParameterValue(right));
		};
		animation.Update(0.5f);
		Assert.Equal(0, playback.Elapsed);
		Assert.Equal(0, events);
	}

	[Live2DAssetsFact]
	public void 禁用待机只恢复写过的眼开合且不破坏故意闭眼表情()
	{
		using var animation = new AnimatedModel(Moc(), Definition("""{"Idle":[{"File":"内存"}]}"""),
			new Dictionary<string, MotionClip> { ["Idle_0"] = EyeClip("""{"Target":"Parameter","Id":"ParamEyeLOpen","Segments":[0,0]}""") })
			{ RandomMotion = false };
		NativeModel model = animation.Model;
		int left = model.GetParameterIndex("ParamEyeLOpen"), right = model.GetParameterIndex("ParamEyeROpen");
		int angle = model.GetParameterIndex("ParamAngleX");
		model.SetParameterValue(right, 0.6f);
		model.SaveParameters();
		ExpressionStore store = new();
		store.RegisterExpressions("test", [],
		[
			new ExpressionEntry
			{
				Name = "ParamEyeLOpen", ParameterId = "ParamEyeLOpen", Blend = ExpressionBlendMode.Overwrite,
				CurrentValue = 0, ModelDefault = model.GetParameterDefaultValue(left),
			},
		]);
		ExpressionBehavior expressions = new(store);
		expressions.BindModel(model);
		BehaviorContext ctx = new() { Model = animation, IsIdleMotion = true, IdleAnimationEnabled = false };
		animation.AfterEffects = () => expressions.Execute(ctx);
		Assert.NotNull(animation.StartMotion("Idle", 0, MotionPriority.Force));
		animation.Update(0);
		Assert.Equal(0, model.GetParameterValue(left));

		new IdleDisableBehavior().Execute(ctx);
		animation.BeforeEffects = () =>
		{
			Assert.Equal(model.GetParameterDefaultValue(left), model.GetParameterValue(left));
			Assert.Equal(0.6f, model.GetParameterValue(right), 5);
			Assert.Equal(10, model.GetParameterValue(angle));
		};
		animation.Update(0);
		Assert.True(animation.IsMotionFinished);
		Assert.Equal(0, model.GetParameterValue(left));
		Assert.Equal(0.6f, model.GetParameterValue(right), 5);
		ctx.ExpressionEnabled = false;
		animation.Update(0);
		Assert.Equal(model.GetParameterDefaultValue(left), model.GetParameterValue(left));
	}

	[Live2DAssetsFact]
	public void 关闭待机不打断显式非Idle动作且通用停止仍保留当前眼值()
	{
		MotionClip clip = EyeClip("""
			{"Target":"Parameter","Id":"ParamEyeLOpen","Segments":[0,0]},
			{"Target":"Parameter","Id":"ParamEyeROpen","Segments":[0,0]}
			""");
		using var animation = new AnimatedModel(Moc(), Definition("""{"Idle":[{"File":"内存"}],"TapBody":[{"File":"内存"}]}"""),
			new Dictionary<string, MotionClip> { ["Idle_0"] = clip, ["TapBody_0"] = clip }) { RandomMotion = false };
		NativeModel model = animation.Model;
		int left = model.GetParameterIndex("ParamEyeLOpen"), right = model.GetParameterIndex("ParamEyeROpen");
		Assert.NotNull(animation.StartMotion("Idle", 0, MotionPriority.Force));
		animation.Update(0);
		MotionPlayback playback = Assert.IsType<MotionPlayback>(animation.StartMotion("TapBody", 0, MotionPriority.Force));
		animation.Update(0);
		animation.StopIdleMotion();
		new IdleDisableBehavior().Execute(new BehaviorContext { Model = animation, IsIdleMotion = false, IdleAnimationEnabled = false });
		Assert.False(animation.IsMotionFinished);
		Assert.False(playback.IsFinished);
		Assert.Equal("TapBody", animation.CurrentMotionGroup);
		Assert.Equal(0, model.GetParameterValue(left));
		Assert.Equal(0, model.GetParameterValue(right));
		animation.Update(0.5f);
		Assert.Equal(0.5, playback.Elapsed);
		Assert.False(playback.IsFinished);
		animation.StopAllMotions();
		model.LoadParameters();
		Assert.Equal(0, model.GetParameterValue(left));
		Assert.Equal(0, model.GetParameterValue(right));
	}

	[Live2DAssetsFact]
	public void 动作组精确匹配优先且大小写回退保留真实组名()
	{
		var clips = new Dictionary<string, MotionClip> { ["Idle_0"] = Clip(), ["idle_0"] = Clip() };
		using var animation = new AnimatedModel(Moc(), Definition("""{"Idle":[{"File":"a"}],"idle":[{"File":"b"}],"Empty":[]}"""), clips);
		Assert.NotNull(animation.StartMotion("idle", 0, MotionPriority.Force));
		Assert.Equal("idle", animation.CurrentMotionGroup);
		Assert.Null(animation.StartMotion("Idle", 0, MotionPriority.Idle));
		Assert.Equal("idle", animation.CurrentMotionGroup);
		animation.StopAllMotions();
		Assert.Null(animation.CurrentMotionGroup);
		Assert.NotNull(animation.StartMotion("IDLE", 0, MotionPriority.Force));
		Assert.Equal("Idle", animation.CurrentMotionGroup);
		Assert.Null(animation.StartMotion("Idle", -1, MotionPriority.Force));
		Assert.Null(animation.StartMotion("Idle", 1, MotionPriority.Force));
		Assert.Null(animation.StartRandomMotion("Empty", MotionPriority.Force));
		Assert.Null(animation.StartMotion("missing", 0, MotionPriority.Force));
	}

	[Live2DAssetsFact]
	public void 自动待机下一帧起播且完成回调可重入播放()
	{
		using var animation = new AnimatedModel(Moc(), Definition("""{"Idle":[{"File":"idle"}]}"""),
			new Dictionary<string, MotionClip> { ["Idle_0"] = Clip() });
		animation.Update(0.1f);
		Assert.False(animation.IsMotionFinished);
		Assert.Equal("Idle", animation.CurrentMotionGroup);
		animation.RandomMotion = false;
		int events = 0;
		animation.MotionEvent += value => { Assert.Equal("事件", value); events++; };
		MotionPlayback? replay = null;
		animation.StartMotion("Idle", 0, MotionPriority.Force,
			() => replay = animation.StartMotion("Idle", 0, MotionPriority.Force));
		animation.Update(0);
		animation.Update(0.5f);
		Assert.Equal(1, events);
		animation.Update(0.6f);
		Assert.NotNull(replay);
		Assert.False(animation.IsMotionFinished);
	}

	[Live2DAssetsFact]
	public void 两个装配实例共享定义与动作但不共享播放和释放()
	{
		ModelDefinition definition = Definition("""{"Idle":[{"File":"idle"}]}""");
		var clips = new Dictionary<string, MotionClip> { ["Idle_0"] = Clip() };
		var first = new AnimatedModel(Moc(), definition, clips) { RandomMotion = false };
		using var second = new AnimatedModel(Moc(), definition, clips) { RandomMotion = false };
		first.StartMotion("Idle", 0, MotionPriority.Force);
		Assert.True(second.IsMotionFinished);
		first.Update(0);
		first.Dispose();
		first.Dispose();
		Assert.True(first.Model.IsDisposed);
		Assert.False(second.Model.IsDisposed);
		second.Update(0);
		Assert.Throws<ObjectDisposedException>(() => first.Update(0));
		Assert.Throws<ObjectDisposedException>(() => first.StartMotion("Idle", 0, MotionPriority.Force));
		foreach (float value in new[] { -1, float.NaN, float.PositiveInfinity })
			Assert.Throws<ArgumentOutOfRangeException>(() => second.Update(value));
	}

	private static MotionClip EyeClip(string eyes) => MotionClip.Parse(Encoding.UTF8.GetBytes($$"""
		{"Version":3,"Meta":{"Duration":1,"FadeInTime":0,"FadeOutTime":0},"Curves":[
		{{eyes}}, {"Target":"Parameter","Id":"ParamAngleX","Segments":[0,10]}],
		"UserData":[{"Time":0.5,"Value":"事件"}]}
		"""));

	[Fact]
	public void 缺失动作在创建原生模型之前拒绝而不回退读取文件()
	{
		var error = Assert.Throws<InvalidOperationException>(() => new AnimatedModel([], Definition("""{"Idle":[{"File":"不存在"}]}"""),
			new Dictionary<string, MotionClip>()));
		Assert.Contains("Idle_0", error.Message);
	}

	[Live2DAssetsFact]
	public unsafe void 命名命中区域使用当前网格模型坐标且透明时不命中()
	{
		byte[] moc = Moc();
		using var reference = new NativeModel(moc);
		string id = reference.DrawableIds[0];
		ModelDefinition definition = ModelDefinition.Parse(JsonSerializer.SerializeToUtf8Bytes(new
		{
			FileReferences = new { Moc = "内存" }, HitAreas = new[] { new { Id = id, Name = "Head" } },
		}));
		using var animation = new AnimatedModel(moc, definition, new Dictionary<string, MotionClip>()) { RandomMotion = false };
		animation.Update(0);
		NativeModel model = animation.Model;
		Vector2* vertices = model.GetDrawableVertexPositions(0);
		Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
		for (int i = 0; i < model.GetDrawableVertexCount(0); i++)
		{
			min = Vector2.Min(min, vertices[i]);
			max = Vector2.Max(max, vertices[i]);
		}
		Vector2 center = (min + max) / 2;
		Assert.True(animation.HitTest("head", center));
		Assert.False(animation.HitTest("missing", center));
		Assert.False(animation.HitTest("Head", max + Vector2.One));
		Assert.False(animation.HitTest("Head", new(float.NaN, 0)));
		model.Opacity = 0.5f;
		Assert.False(animation.HitTest("Head", center));
		GC.KeepAlive(model);
	}
}
