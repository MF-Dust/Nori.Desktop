using Avalonia.Media;
using Nori.Core.Expression;
using Nori.Desktop.Live2D;

namespace Nori.Desktop.Expression;

/// <summary>
/// 语音气泡的描边随情绪变色。
///
/// 选这个位置而不是给宠物本体加光晕：本体是 OpenGL 表面，在它上面做发光要动合成与着色器，
/// 而气泡是一个现成的 <see cref="Avalonia.Controls.Border"/>，改的是一个属性。同样是「她身上
/// 的局部表达」，代价差着量级。
///
/// 侵入等级 Local：只出现在她说话时，不影响你看别的东西。
/// </summary>
public sealed class SpeechBorderChannel : IExpressionChannel
{
	/// <summary>设置项键名。</summary>
	public const string ChannelKey = "expression_speech_border";

	/// <summary>描边不透明度。跟原来写死的那版保持一致，只换色相。</summary>
	public const byte BorderAlpha = 180;

	private readonly Func<PetSpeechOverlay?> _overlay;
	private readonly Action<Action> _onUi;

	/// <summary>创建通道。</summary>
	/// <param name="overlay">取当前的语音气泡；伴侣视窗没开时为 null。</param>
	/// <param name="onUi">把操作调度到 UI 线程。</param>
	public SpeechBorderChannel(Func<PetSpeechOverlay?> overlay, Action<Action> onUi)
	{
		ArgumentNullException.ThrowIfNull(overlay);
		ArgumentNullException.ThrowIfNull(onUi);
		_overlay = overlay;
		_onUi = onUi;
	}

	/// <inheritdoc />
	public string Key => ChannelKey;

	/// <inheritdoc />
	public Intrusiveness Level => Intrusiveness.Local;

	/// <inheritdoc />
	public bool IsAvailable => _overlay() is not null;

	/// <inheritdoc />
	public Task ApplyAsync(ExpressionPalette palette, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(palette);
		if (_overlay() is not {} overlay) return Task.CompletedTask;

		Color color = ExpressionColors.Parse(palette.Accent, BorderAlpha);
		_onUi(() => overlay.BorderBrush = new SolidColorBrush(color));
		return Task.CompletedTask;
	}
}
