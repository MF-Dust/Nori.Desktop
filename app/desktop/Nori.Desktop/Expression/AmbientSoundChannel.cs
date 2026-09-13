using Nori.Core.Expression;

namespace Nori.Desktop.Expression;

/// <summary>
/// 环境音随情绪切换音景。
///
/// **这一版只有壳。** 通道、探测、可用性判定都在，但仓库里没有音景素材，因此在用户放进
/// 文件之前它恒为不可用 —— 与「平台不支持」「OpenRGB 没开」走同一套降级路径，界面上能说清
/// 是缺素材而不是坏了。
///
/// 素材放在 `<数据目录>/soundscapes/` 下，按音景名命名（`calm.ogg`、`tense.ogg`……），
/// 音景名由 <see cref="ExpressionPalette.Soundscape"/> 给出。
///
/// 侵入等级 Peripheral：它走的是另一个感官，不抢视觉注意力 —— 你写代码时不会被打断，但
/// 氛围变了感觉得到。缺点是你在听音乐时它就是干扰，所以仍然可以单独关。
/// </summary>
public sealed class AmbientSoundChannel : IExpressionChannel
{
	/// <summary>设置项键名。</summary>
	public const string ChannelKey = "expression_ambient_sound";

	/// <summary>认得出的素材扩展名，按优先级排列。</summary>
	public static readonly IReadOnlyList<string> Extensions = [".ogg", ".mp3", ".wav"];

	private readonly string _directory;
	private readonly Func<string, Task> _play;

	/// <summary>创建通道。</summary>
	/// <param name="directory">音景素材目录。</param>
	/// <param name="play">播放一个素材文件；尚未接上播放链路时传空实现。</param>
	public AmbientSoundChannel(string directory, Func<string, Task>? play = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		_directory = directory;
		_play = play ?? (_ => Task.CompletedTask);
	}

	/// <inheritdoc />
	public string Key => ChannelKey;

	/// <inheritdoc />
	public Intrusiveness Level => Intrusiveness.Peripheral;

	/// <summary>目录里有没有可用素材。一个都没有时整条通道不可用。</summary>
	public bool IsAvailable => Directory.Exists(_directory) && Available().Count > 0;

	/// <summary>当前认得出的音景名，供设置页显示「缺哪些」。</summary>
	public IReadOnlyList<string> Available() =>
		Directory.Exists(_directory)
			? [.. Directory.EnumerateFiles(_directory)
				.Where(file => Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
				.Select(file => Path.GetFileNameWithoutExtension(file))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.Order(StringComparer.Ordinal)]
			: [];

	/// <inheritdoc />
	public Task ApplyAsync(ExpressionPalette palette, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(palette);
		return Resolve(palette.Soundscape) is {} file ? _play(file) : Task.CompletedTask;
	}

	/// <summary>
	/// 找这个音景对应的素材文件；没有就返回 null，**不退回到别的音景**。
	///
	/// 随便挑一个顶上比不放更糟：用户会听到一段与当下情绪不符的声音，而且无从判断是配错了
	/// 还是缺文件。
	/// </summary>
	internal string? Resolve(string soundscape)
	{
		if (string.IsNullOrWhiteSpace(soundscape) || !Directory.Exists(_directory)) return null;

		foreach (string extension in Extensions)
		{
			string candidate = Path.Combine(_directory, soundscape + extension);
			if (File.Exists(candidate)) return candidate;
		}

		return null;
	}
}
