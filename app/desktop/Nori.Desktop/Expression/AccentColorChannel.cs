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
	private readonly Func<bool>? _canApply;
	private readonly string _backupKey;
	private readonly Lock _gate = new();

	/// <summary>创建通道；写入前再次检查开关，避免排队的颜色覆盖还原结果。</summary>
	public AccentColorChannel(IDesktopAppearance appearance, DesktopStateBackup backup, Func<bool>? canApply = null)
	{
		_appearance = appearance;
		_backup = backup;
		_canApply = canApply;
		_backupKey = appearance.BackupScope is { } scope ? $"{ChannelKey}_{scope}" : ChannelKey;
	}

	/// <inheritdoc />
	public string Key => ChannelKey;

	/// <inheritdoc />
	public Intrusiveness Level => Intrusiveness.Global;

	/// <inheritdoc />
	public bool IsAvailable => _appearance.IsAvailable;

	/// <inheritdoc />
	public Task ApplyAsync(ExpressionPalette palette, CancellationToken cancellationToken) => Task.Run(() =>
	{
		lock (_gate)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!_appearance.IsAvailable || _canApply?.Invoke() == false) return;

			// 没有可还原的原值就不改系统设置；备份、写入与还原不能交错。
			if (_appearance.CaptureBackup() is not { } original) return;
			_backup.Remember(_backupKey, original);
			_appearance.SetAccentColor(ExpressionColors.Parse(palette.Primary));
		}
	}, cancellationToken);

	/// <summary>
	/// 还原用户原来的强调色。关掉这条通道、退出应用、以及启动时发现上次没还原干净都要调。
	/// </summary>
	public void Restore()
	{
		lock (_gate)
		{
			if (!_appearance.IsAvailable || _backup.Original(_backupKey) is not { } saved) return;
			_appearance.RestoreBackup(saved);
			_backup.Forget(_backupKey);
		}
	}
}
