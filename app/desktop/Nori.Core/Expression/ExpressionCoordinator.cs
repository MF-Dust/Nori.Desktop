using Nori.Core.Emotion;

namespace Nori.Core.Expression;

/// <summary>
/// 一条表达通道：把 <see cref="ExpressionPalette"/> 变成自己那种形式。
///
/// 通道只管「怎么表现」，不管「happy 是什么颜色」—— 那一处在 <see cref="EmotionExpression"/>。
/// </summary>
public interface IExpressionChannel
{
	/// <summary>设置项键名，也是这条通道的开关键。</summary>
	string Key { get; }

	/// <summary>侵入程度，决定默认开关与变化速度。</summary>
	Intrusiveness Level { get; }

	/// <summary>
	/// 这条通道当前能不能用。
	///
	/// 与「开没开」分开：用户打开了开关却没反应时，界面要能说出是「你没开」还是「开不了」。
	/// </summary>
	bool IsAvailable { get; }

	/// <summary>应用一组表达参数。实现须自行吞掉自身故障，一条通道坏掉不应影响其余通道。</summary>
	Task ApplyAsync(ExpressionPalette palette, CancellationToken cancellationToken);
}

/// <summary>
/// 把情绪扇出到各条表达通道。
///
/// 三条职责：只在**跨档**时才下发（避免每句话都改一次系统颜色）、按开关与可用性过滤、
/// 单条通道抛异常不影响其余通道。
/// </summary>
public sealed class ExpressionCoordinator
{
	/// <summary>
	/// 触发下发的最小强度变化。
	///
	/// 情绪强度是连续量，不设阈值的话每一轮回复都会重下一次；对全局通道（系统强调色、壁纸）
	/// 那就是每说一句话整个桌面闪一下。
	/// </summary>
	public const double MinimumIntensityChange = 0.15;

	private readonly IReadOnlyList<IExpressionChannel> _channels;
	private readonly Func<string, bool> _isEnabled;
	private readonly Action<string, Exception>? _onFailure;
	private readonly Lock _gate = new();
	private string? _lastEmotion;
	private double _lastIntensity;

	/// <summary>创建协调器。</summary>
	/// <param name="channels">全部通道。</param>
	/// <param name="isEnabled">按通道键判断用户开没开。</param>
	/// <param name="onFailure">单条通道失败时的回调，用于记日志。</param>
	public ExpressionCoordinator(
		IReadOnlyList<IExpressionChannel> channels,
		Func<string, bool> isEnabled,
		Action<string, Exception>? onFailure = null)
	{
		ArgumentNullException.ThrowIfNull(channels);
		ArgumentNullException.ThrowIfNull(isEnabled);
		_channels = channels;
		_isEnabled = isEnabled;
		_onFailure = onFailure;
	}

	/// <summary>各通道的当前可用性，供设置页显示。</summary>
	public IReadOnlyDictionary<string, bool> Availability =>
		_channels.ToDictionary(channel => channel.Key, channel => channel.IsAvailable, StringComparer.Ordinal);

	/// <summary>
	/// 按当前情绪下发。变化不足阈值时直接返回，不下发也不记录。
	/// </summary>
	public async Task ApplyAsync(EmotionState? emotion, CancellationToken cancellationToken = default)
	{
		string type = emotion?.Type ?? EmotionTypes.Neutral;
		double intensity = Math.Clamp(emotion?.Intensity ?? 0, 0, 1);

		lock (_gate)
		{
			if (!ShouldApply(type, intensity)) return;
			_lastEmotion = type;
			_lastIntensity = intensity;
		}

		ExpressionPalette palette = EmotionExpression.For(emotion);

		foreach (IExpressionChannel channel in _channels)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!channel.IsAvailable || !_isEnabled(channel.Key)) continue;

			try
			{
				await channel.ApplyAsync(palette, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// 一条通道坏掉不能连累其余：灯效软件退出不该让她的表情也停住。
				_onFailure?.Invoke(channel.Key, exception);
			}
		}
	}

	/// <summary>
	/// 值得下发吗：换了情绪，或强度变化超过阈值。
	///
	/// 判据取映射**之前**的情绪与强度，不取渲染后的颜色 —— 颜色本身就是强度算出来的，
	/// 0.80 与 0.83 混出的十六进制不同，拿它当判据节流就永远不生效。
	///
	/// 两项都要：只比强度会漏掉换情绪但强度相同的情形（sad 0.5 → angry 0.5）；
	/// 只比情绪会漏掉同一情绪内的强弱变化。
	/// </summary>
	private bool ShouldApply(string emotion, double intensity) =>
		_lastEmotion is null
		|| !string.Equals(_lastEmotion, emotion, StringComparison.Ordinal)
		|| Math.Abs(_lastIntensity - intensity) >= MinimumIntensityChange;
}
