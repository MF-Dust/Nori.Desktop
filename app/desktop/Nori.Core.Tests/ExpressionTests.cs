using Nori.Core.Emotion;
using Nori.Core.Expression;

namespace Nori.Core.Tests;

/// <summary>
/// 情绪表达层。
///
/// 这一层错了所有通道一起错 —— 映射只有一处，各通道都从它取参数。所以它值得被钉死。
/// </summary>
public sealed class ExpressionTests
{
	private sealed class RecordingChannel : IExpressionChannel
	{
		public List<ExpressionPalette> Applied { get; } = [];

		public required string Key { get; init; }

		public Intrusiveness Level { get; init; } = Intrusiveness.Local;

		public bool IsAvailable { get; init; } = true;

		public Exception? Throws { get; init; }

		public Task ApplyAsync(ExpressionPalette palette, CancellationToken cancellationToken)
		{
			if (Throws is not null) throw Throws;
			Applied.Add(palette);
			return Task.CompletedTask;
		}
	}

	private static EmotionState Emotion(string type, double intensity) =>
		new() {Type = type, Intensity = intensity, LastUpdated = 0};

	// ---- 映射 ----

	[Fact]
	public void 每种情绪都有映射()
	{
		foreach (string type in EmotionTypes.All)
		{
			ExpressionPalette palette = EmotionExpression.For(Emotion(type, 1));
			Assert.StartsWith("#", palette.Primary, StringComparison.Ordinal);
			Assert.Equal(7, palette.Primary.Length);
			Assert.NotEmpty(palette.Soundscape);
		}
	}

	/// <summary>未知情绪按中性处理，不能抛 —— 模型给出规定之外的值是常态。</summary>
	[Fact]
	public void 未知情绪落回中性()
	{
		Assert.Equal(
			EmotionExpression.For(Emotion(EmotionTypes.Neutral, 1)).Primary,
			EmotionExpression.For(Emotion("狂喜", 1)).Primary);

		Assert.Equal(EmotionExpression.NeutralPrimary, EmotionExpression.For(null).Primary);
	}

	/// <summary>
	/// 强度趋零时向中性收敛。
	///
	/// 否则「她有点开心」和「她很开心」在通道上只差饱和度，而实际差别应该是「几乎看不出来」。
	/// </summary>
	[Fact]
	public void 强度为零时等于中性()
	{
		Assert.Equal(EmotionExpression.NeutralPrimary, EmotionExpression.For(Emotion(EmotionTypes.Angry, 0)).Primary);
	}

	[Fact]
	public void 强度越高越接近该情绪的本色()
	{
		string weak = EmotionExpression.For(Emotion(EmotionTypes.Happy, 0.2)).Primary;
		string strong = EmotionExpression.For(Emotion(EmotionTypes.Happy, 1)).Primary;

		Assert.NotEqual(weak, strong);
		Assert.Equal(strong, EmotionExpression.For(Emotion(EmotionTypes.Happy, 1.5)).Primary);  // 越界夹回
	}

	[Fact]
	public void 强度被夹在零到一之间()
	{
		Assert.Equal(0, EmotionExpression.For(Emotion(EmotionTypes.Sad, -3)).Intensity);
		Assert.Equal(1, EmotionExpression.For(Emotion(EmotionTypes.Sad, 9)).Intensity);
	}

	/// <summary>低强度不换音景：环境音换来换去比不换更烦人。</summary>
	[Fact]
	public void 低强度时音景保持中性()
	{
		Assert.Equal("calm", EmotionExpression.For(Emotion(EmotionTypes.Angry, 0.1)).Soundscape);
		Assert.Equal("tense", EmotionExpression.For(Emotion(EmotionTypes.Angry, 0.9)).Soundscape);
	}

	[Theory]
	[InlineData("#000000", "#FFFFFF", 0.0, "#000000")]
	[InlineData("#000000", "#FFFFFF", 1.0, "#FFFFFF")]
	[InlineData("#000000", "#FFFFFF", 0.5, "#808080")]
	public void 颜色按比例混合(string from, string to, double ratio, string expected)
	{
		Assert.Equal(expected, EmotionExpression.Blend(from, to, ratio));
	}

	// ---- 扇出 ----

	[Fact]
	public async Task 下发到所有已开启且可用的通道()
	{
		RecordingChannel first = new() {Key = "glow"};
		RecordingChannel second = new() {Key = "tray"};
		ExpressionCoordinator coordinator = new([first, second], _ => true);

		await coordinator.ApplyAsync(Emotion(EmotionTypes.Happy, 0.8));

		Assert.Single(first.Applied);
		Assert.Single(second.Applied);
		Assert.Equal(first.Applied[0], second.Applied[0]);
	}

	/// <summary>各通道拿到的是同一组参数，因此表现天然一致 —— 不会光晕是粉的而灯效是蓝的。</summary>
	[Fact]
	public async Task 关掉的通道不下发()
	{
		RecordingChannel on = new() {Key = "glow"};
		RecordingChannel off = new() {Key = "accent"};
		ExpressionCoordinator coordinator = new([on, off], key => key == "glow");

		await coordinator.ApplyAsync(Emotion(EmotionTypes.Happy, 0.8));

		Assert.Single(on.Applied);
		Assert.Empty(off.Applied);
	}

	[Fact]
	public async Task 不可用的通道不下发()
	{
		RecordingChannel channel = new() {Key = "rgb", IsAvailable = false};
		ExpressionCoordinator coordinator = new([channel], _ => true);

		await coordinator.ApplyAsync(Emotion(EmotionTypes.Happy, 0.8));

		Assert.Empty(channel.Applied);
	}

	/// <summary>一条通道坏掉不能连累其余：灯效软件退出不该让她的表情也停住。</summary>
	[Fact]
	public async Task 单条通道抛异常不影响其余()
	{
		List<string> failures = [];
		RecordingChannel broken = new() {Key = "rgb", Throws = new InvalidOperationException("灯效软件没开")};
		RecordingChannel healthy = new() {Key = "glow"};
		ExpressionCoordinator coordinator = new(
			[broken, healthy], _ => true, (key, _) => failures.Add(key));

		await coordinator.ApplyAsync(Emotion(EmotionTypes.Happy, 0.8));

		Assert.Single(healthy.Applied);
		Assert.Equal(["rgb"], failures);
	}

	// ---- 下发节流 ----

	/// <summary>
	/// 强度微动不重下。
	///
	/// 情绪强度是连续量，不设阈值的话每一轮回复都会重下一次；对全局通道那就是每说一句话
	/// 整个桌面闪一下。
	/// </summary>
	[Fact]
	public async Task 强度微动不触发下发()
	{
		RecordingChannel channel = new() {Key = "accent"};
		ExpressionCoordinator coordinator = new([channel], _ => true);

		await coordinator.ApplyAsync(Emotion(EmotionTypes.Happy, 0.80));
		await coordinator.ApplyAsync(Emotion(EmotionTypes.Happy, 0.83));

		Assert.Single(channel.Applied);
	}

	[Fact]
	public async Task 强度跨阈值才触发下发()
	{
		RecordingChannel channel = new() {Key = "accent"};
		ExpressionCoordinator coordinator = new([channel], _ => true);

		await coordinator.ApplyAsync(Emotion(EmotionTypes.Happy, 0.30));
		await coordinator.ApplyAsync(Emotion(EmotionTypes.Happy, 0.90));

		Assert.Equal(2, channel.Applied.Count);
	}

	/// <summary>换情绪但强度相同时也要下发 —— 只比强度会漏掉 sad 0.5 → angry 0.5。</summary>
	[Fact]
	public async Task 换情绪即使强度不变也下发()
	{
		RecordingChannel channel = new() {Key = "accent"};
		ExpressionCoordinator coordinator = new([channel], _ => true);

		await coordinator.ApplyAsync(Emotion(EmotionTypes.Sad, 0.5));
		await coordinator.ApplyAsync(Emotion(EmotionTypes.Angry, 0.5));

		Assert.Equal(2, channel.Applied.Count);
	}

	[Fact]
	public void 可用性按通道报给界面()
	{
		ExpressionCoordinator coordinator = new(
			[new RecordingChannel {Key = "glow"}, new RecordingChannel {Key = "rgb", IsAvailable = false}],
			_ => true);

		Assert.True(coordinator.Availability["glow"]);
		Assert.False(coordinator.Availability["rgb"]);
	}
}
