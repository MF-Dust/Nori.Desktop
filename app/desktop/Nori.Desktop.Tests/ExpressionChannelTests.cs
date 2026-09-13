using Avalonia.Controls;
using Avalonia.Media;
using Nori.Core.Emotion;
using Nori.Core.Expression;
using Nori.Desktop.Expression;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Tests;

/// <summary>
/// 桌面侧的表达通道。
///
/// 这两条都建立在现成的界面元素上：托盘图标和语音气泡的描边。选它们而不是给宠物本体加光晕，
/// 是因为本体是 OpenGL 表面，在上面做发光要动合成与着色器 —— 代价差着量级。
/// </summary>
public sealed class ExpressionChannelTests
{
	private static ExpressionPalette Palette(string emotion = EmotionTypes.Happy, double intensity = 0.8) =>
		EmotionExpression.For(new EmotionState {Type = emotion, Intensity = intensity, LastUpdated = 0});

	private static void RunInline(Action action) => action();

	// ---- 颜色解析 ----

	[Fact]
	public void 解析六位十六进制颜色()
	{
		Color color = ExpressionColors.Parse("#4FC3D9", 180);

		Assert.Equal(0x4F, color.R);
		Assert.Equal(0xC3, color.G);
		Assert.Equal(0xD9, color.B);
		Assert.Equal(180, color.A);
	}

	/// <summary>
	/// 格式不对时给中性灰而不是抛。
	///
	/// 一条通道因为颜色串格式崩掉，会连累协调器去记一条看不懂的错误，而用户看到的是
	/// 「她的表情不动了」。
	/// </summary>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("#12345")]
	[InlineData("#GGGGGG")]
	[InlineData("not a color")]
	public void 非法颜色落回中性灰(string input)
	{
		Color color = ExpressionColors.Parse(input);

		Assert.Equal(0x8F, color.R);
		Assert.Equal(0xA3, color.G);
		Assert.Equal(0xB0, color.B);
	}

	[Fact]
	public void 转成Windows的BGR字节序()
	{
		// Windows 的注册表与 DWM 都按 0x00BBGGRR 存颜色，写反了整个桌面会变成补色。
		Assert.Equal(0x00DAC3B4u, ExpressionColors.ToWindowsBgr(Color.FromRgb(0xB4, 0xC3, 0xDA)));
	}

	// ---- 语音气泡 ----

	[Fact]
	public void 气泡不存在时通道不可用()
	{
		SpeechBorderChannel channel = new(() => null, RunInline);

		Assert.False(channel.IsAvailable);
		Assert.Equal(Intrusiveness.Local, channel.Level);
	}

	[Fact]
	public async Task 气泡不存在时下发不抛()
	{
		await new SpeechBorderChannel(() => null, RunInline).ApplyAsync(Palette(), CancellationToken.None);
	}

	// ---- 托盘 ----

	[Fact]
	public void 托盘不存在时通道不可用()
	{
		TrayIconChannel channel = new(() => null, RunInline);

		Assert.False(channel.IsAvailable);
		Assert.Equal(Intrusiveness.Local, channel.Level);
	}

	[Fact]
	public async Task 托盘不存在时下发不抛()
	{
		await new TrayIconChannel(() => null, RunInline).ApplyAsync(Palette(), CancellationToken.None);
	}

	/// <summary>
	/// 不同情绪取不同的渐变色。
	///
	/// 只测颜色不测绘制：绘制要 Avalonia 的渲染后端，在无头测试里起不来。而「什么情绪画成
	/// 什么颜色」是纯数据判断，抽出来之后无条件可测。
	/// </summary>
	[Fact]
	public void 不同情绪取不同的渐变色()
	{
		Assert.NotEqual(
			TrayIconChannel.Gradient(Palette(EmotionTypes.Happy)),
			TrayIconChannel.Gradient(Palette(EmotionTypes.Sad)));
	}

	[Fact]
	public void 同一情绪同一强度取相同的渐变色()
	{
		Assert.Equal(
			TrayIconChannel.Gradient(Palette(EmotionTypes.Fond, 0.6)),
			TrayIconChannel.Gradient(Palette(EmotionTypes.Fond, 0.6)));
	}

	/// <summary>强度趋零时向中性收敛，图标也跟着 —— 这条穿透了映射层与通道层。</summary>
	[Fact]
	public void 强度为零时图标是中性色()
	{
		(Color from, _) = TrayIconChannel.Gradient(Palette(EmotionTypes.Angry, 0));

		Assert.Equal(ExpressionColors.Parse(EmotionExpression.NeutralPrimary), from);
	}
}
