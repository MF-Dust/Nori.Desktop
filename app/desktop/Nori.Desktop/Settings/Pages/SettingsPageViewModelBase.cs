using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Nori.Desktop.Settings.Pages;

/// <summary>原生设置页 ViewModel 的共同基础，统一宿主调用、忙碌状态和错误通知。</summary>
public abstract class SettingsPageViewModelBase : INotifyPropertyChanged
{
	private bool _isBusy;
	private string _errorMessage = string.Empty;

	/// <summary>创建设置页 ViewModel。</summary>
	protected SettingsPageViewModelBase(SettingsService service)
	{
		Service = service ?? throw new ArgumentNullException(nameof(service));
	}

	/// <summary>当前设置窗口的宿主服务。</summary>
	protected SettingsService Service { get; }

	/// <summary>页面是否正在执行一个写入或网络操作。</summary>
	public bool IsBusy
	{
		get => _isBusy;
		protected set => SetProperty(ref _isBusy, value);
	}

	/// <summary>本页最近一次操作的可读错误。</summary>
	public string ErrorMessage
	{
		get => _errorMessage;
		private set => SetProperty(ref _errorMessage, value);
	}

	/// <summary>属性变化通知。</summary>
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>通知页面刷新数据。</summary>
	public event Action? Changed;

	/// <summary>刷新页面数据。</summary>
	public abstract Task RefreshAsync(CancellationToken cancellationToken = default);

	/// <summary>在页面关闭前等待尚未完成的操作。</summary>
	public virtual Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

	/// <summary>清除页面错误。</summary>
	public void ClearError() => ErrorMessage = string.Empty;

	/// <summary>记录页面错误并通知视图。</summary>
	public void ReportError(Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		ErrorMessage = exception.Message;
		NotifyChanged(nameof(ErrorMessage));
	}

	/// <summary>设置属性并通知页面。</summary>
	protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return false;
		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		return true;
	}

	/// <summary>通知集合或派生状态已经发生变化。</summary>
	protected void NotifyChanged([CallerMemberName] string? propertyName = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		Changed?.Invoke();
	}

	/// <summary>执行宿主命令并维护忙碌状态。</summary>
	protected async Task<JsonElement> ExecuteAsync(string command, object? args = null, CancellationToken cancellationToken = default)
	{
		IsBusy = true;
		try
		{
			return await Service.ExecuteAsync(command, args, cancellationToken).ConfigureAwait(true);
		}
		finally
		{
			IsBusy = false;
		}
	}
}
