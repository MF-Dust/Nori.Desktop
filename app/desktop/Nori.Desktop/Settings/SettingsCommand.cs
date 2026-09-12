using System.Windows.Input;

namespace Nori.Desktop.Settings;

/// <summary>设置窗口使用的轻量命令。</summary>
public sealed class SettingsCommand : ICommand
{
	private readonly Action<object?> _execute;
	private readonly Predicate<object?>? _canExecute;

	/// <summary>创建命令。</summary>
	public SettingsCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
	{
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
		_canExecute = canExecute;
	}

	/// <inheritdoc />
	public event EventHandler? CanExecuteChanged;

	/// <inheritdoc />
	public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

	/// <inheritdoc />
	public void Execute(object? parameter) => _execute(parameter);

	/// <summary>通知命令可执行状态变化。</summary>
	public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
