using System.Globalization;
using Avalonia.Media;
using Nori.Core.Expression;

namespace Nori.Desktop.Expression;

/// <summary>
/// 系统强调色随情绪变化。
///
/// 这是对「住在电脑里」最对题的一条通道：她的情绪改变**整个操作系统的颜色**，你看任何窗口
/// 都能感觉到，而不是她在屏幕角落里显示情绪。
///
/// 也是侵入性最强的一条，因此：默认关、改之前备份原值、关掉时还原。
/// </summary>
public sealed class AccentColorChannel : IExpressionChannel
{
	/// <summary>设置项键名。</summary>
	public const string ChannelKey = "expression_accent_color";

	private readonly IDesktopAppearance _appearance;
	private readonly DesktopStateBackup _backup;

	/// <summary>创建通道。</summary>
	public AccentColorChannel(IDesktopAppearance appearance, DesktopStateBackup backup)
	{
		ArgumentNullException.ThrowIfNull(appearance);
		ArgumentNullException.ThrowIfNull(backup);
		_appearance = appearance;
		_backup = backup;
	}

	/// <inheritdoc />
	public string Key => ChannelKey;

	/// <inheritdoc />
	public Intrusiveness Level => Intrusiveness.Global;

	/// <inheritdoc />
	public bool IsAvailable => _appearance.IsAvailable;

	/// <inheritdoc />
	public Task ApplyAsync(ExpressionPalette palette, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(palette);
		if (!_appearance.IsAvailable) return Task.CompletedTask;

		// 改之前先记原值。Remember 自身不覆盖，所以第二次调用不会把她改过的值当成原值存下来。
		_backup.Remember(ChannelKey, _appearance.GetAccentColor()?.ToString(CultureInfo.InvariantCulture));
		_appearance.SetAccentColor(ExpressionColors.Parse(palette.Primary));
		return Task.CompletedTask;
	}

	/// <summary>
	/// 还原用户原来的强调色。关掉这条通道、退出应用、以及启动时发现上次没还原干净都要调。
	/// </summary>
	public void Restore()
	{
		if (_backup.Original(ChannelKey) is not {} saved) return;
		if (!uint.TryParse(saved, CultureInfo.InvariantCulture, out uint stored))
		{
			// 备份值坏了：清掉它，否则每次启动都会拿它去试一遍。
			_backup.Forget(ChannelKey);
			return;
		}

		// AccentColor 存的是 AABBGGRR，还原时要按同一个字节序拆回来。
		_appearance.SetAccentColor(Color.FromRgb(
			(byte)(stored & 0xFF),
			(byte)((stored >> 8) & 0xFF),
			(byte)((stored >> 16) & 0xFF)));
		_backup.Forget(ChannelKey);
	}
}
