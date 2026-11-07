using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Desktop.Appearance;
using Nori.Startup;

namespace Nori.Desktop.Settings;

/// <summary>图片路径字段：预览、选择与清除。解码不在界面线程上进行。</summary>
internal sealed class ImageFileEditor : Grid, ISettingsFieldEditor
{
	private const double PreviewWidth = 160;
	private const double PreviewHeight = 100;
	private const int DecodeWidth = 480;

	private readonly SettingsFieldViewModel _field;
	private readonly SettingsBrushPalette _palette;
	private readonly Image _image;
	private readonly TextBlock _placeholder;
	private readonly SelectableTextBlock _path;
	private readonly Button _choose;
	private readonly Button _clear;
	private CancellationTokenSource? _loadCts;
	private Bitmap? _bitmap;
	private string _activePath = "\0";
	private bool _attached;
	private bool _failed;
	private int _generation;

	/// <summary>创建图片路径编辑器。</summary>
	public ImageFileEditor(SettingsFieldViewModel field)
	{
		_field = field;
		_palette = new SettingsBrushPalette(this);
		ColumnDefinitions = new ColumnDefinitions("Auto,*");
		ColumnSpacing = NoriMetrics.Spacing3;
		HorizontalAlignment = HorizontalAlignment.Stretch;
		_image = new Image
		{
			Stretch = Stretch.UniformToFill,
			IsHitTestVisible = false,
			IsVisible = false,
		};
		_placeholder = new TextBlock
		{
			FontSize = NoriMetrics.FontXs,
			Foreground = _palette["SettingsSecondaryBrush"],
			TextWrapping = TextWrapping.Wrap,
			TextAlignment = TextAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(NoriMetrics.Spacing2),
			IsHitTestVisible = false,
		};
		Grid layers = new() { Children = { _image, _placeholder } };
		Border preview = new()
		{
			Width = PreviewWidth,
			Height = PreviewHeight,
			CornerRadius = new CornerRadius(NoriMetrics.RadiusMd),
			Background = NoriThemeTokens.Brush("bg-deep"),
			ClipToBounds = true,
			Child = layers,
		};
		_path = new SelectableTextBlock
		{
			FontSize = NoriMetrics.FontXs,
			Foreground = _palette["SettingsSecondaryBrush"],
			TextTrimming = TextTrimming.CharacterEllipsis,
			TextWrapping = TextWrapping.NoWrap,
			HorizontalAlignment = HorizontalAlignment.Stretch,
		};
		_choose = new Button { MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Left };
		_clear = new Button { MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Left };
		_clear.Click += (_, _) => _field.Text = "";
		StackPanel actions = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = NoriMetrics.Spacing2,
			Children = { _choose, _clear },
		};
		StackPanel side = new()
		{
			Spacing = NoriMetrics.Spacing2,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			Children = { _path, actions },
		};
		Children.Add(preview);
		SetColumn(side, 1);
		Children.Add(side);
		_choose.Command = _field.Command;
		ApplyLanguage();
		UpdatePath(_field.Text);
		if (string.IsNullOrWhiteSpace(_field.Text)) ShowEmpty();
		else _placeholder.IsVisible = false;
	}

	/// <inheritdoc />
	public void Refresh(string? propertyName)
	{
		switch (propertyName)
		{
			case nameof(SettingsFieldViewModel.Text):
				if (_attached) Load(_field.Text);
				else _activePath = "\0";
				break;
			case nameof(SettingsFieldViewModel.Label):
				ApplyLanguage();
				break;
			case nameof(SettingsFieldViewModel.Command):
				_choose.Command = _field.Command;
				break;
			case nameof(SettingsFieldViewModel.IsReadOnly):
				UpdatePath(_field.Text);
				break;
			case null:
				ApplyLanguage();
				_choose.Command = _field.Command;
				if (!_attached) _activePath = "\0";
				else
				{
					_activePath = "\0";
					Load(_field.Text);
				}
				break;
		}
	}

	/// <inheritdoc />
	protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		_attached = true;
		_activePath = "\0";
		Load(_field.Text);
	}

	/// <inheritdoc />
	protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		_attached = false;
		_generation++;
		_activePath = "\0";
		_loadCts?.Cancel();
		_loadCts?.Dispose();
		_loadCts = null;
		ApplyBitmap(null);
		base.OnDetachedFromVisualTree(e);
	}

	private void Load(string? path)
	{
		path ??= string.Empty;
		if (string.Equals(_activePath, path, StringComparison.Ordinal)) return;
		_activePath = path;
		int generation = ++_generation;
		_loadCts?.Cancel();
		_loadCts?.Dispose();
		_loadCts = null;
		UpdatePath(path);
		// 空路径是留白，不能拿去走图片校验，否则会被显示成预览失败。
		if (string.IsNullOrWhiteSpace(path))
		{
			ShowEmpty();
			return;
		}
		CancellationTokenSource cts = new();
		_loadCts = cts;
		ApplyBitmap(null);
		_placeholder.IsVisible = false;
		_ = DecodeAsync(path, generation, cts.Token);
	}

	private async Task DecodeAsync(string path, int generation, CancellationToken token)
	{
		Bitmap? bitmap = null;
		bool failed = false;
		try
		{
			bitmap = await Task.Run(() => TryDecode(path, token), token).ConfigureAwait(false);
			failed = bitmap is null;
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			failed = true;
		}
		if (token.IsCancellationRequested)
		{
			bitmap?.Dispose();
			return;
		}
		Bitmap? decoded = bitmap;
		try
		{
			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (generation != _generation || !_attached)
				{
					decoded?.Dispose();
					decoded = null;
					return;
				}
				if (failed || decoded is null)
				{
					decoded?.Dispose();
					decoded = null;
					ShowFailed();
					return;
				}
				ShowReady(decoded);
				decoded = null;
			});
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			decoded?.Dispose();
		}
	}

	private static Bitmap? TryDecode(string path, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		if (!SplashOptions.IsLocalAbsolutePath(path) || !SplashOptions.HasImageExtension(path) || !File.Exists(path))
			return null;
		if (new FileInfo(path).Length > SplashOptions.MaxBackgroundBytes)
			return null;
		token.ThrowIfCancellationRequested();
		using FileStream stream = File.OpenRead(path);
		return Bitmap.DecodeToWidth(stream, DecodeWidth);
	}

	private void ShowEmpty()
	{
		_failed = false;
		ApplyBitmap(null);
		_placeholder.IsVisible = true;
		_placeholder.Text = EmptyText();
	}

	private void ShowFailed()
	{
		_failed = true;
		ApplyBitmap(null);
		_placeholder.IsVisible = true;
		_placeholder.Text = FailedText();
	}

	private void ShowReady(Bitmap bitmap)
	{
		_failed = false;
		ApplyBitmap(bitmap);
		_placeholder.IsVisible = false;
	}

	private void ApplyBitmap(Bitmap? bitmap)
	{
		Bitmap? previous = _bitmap;
		_bitmap = bitmap;
		_image.Source = bitmap;
		_image.IsVisible = bitmap is not null;
		if (previous is not null && !ReferenceEquals(previous, bitmap)) previous.Dispose();
	}

	private void UpdatePath(string path)
	{
		_path.Text = path;
		ToolTip.SetTip(_path, path.Length == 0 ? null : path);
		_choose.IsEnabled = !_field.IsReadOnly;
		_clear.IsEnabled = !_field.IsReadOnly && path.Length > 0;
	}

	private void ApplyLanguage()
	{
		bool english = English();
		string choose = english ? "Choose image…" : "选择图片…";
		string clear = english ? "Clear" : "清除";
		_choose.Content = choose;
		_clear.Content = clear;
		AutomationProperties.SetName(_choose, choose);
		AutomationProperties.SetName(_clear, clear);
		if (_bitmap is null) _placeholder.Text = _failed ? FailedText() : EmptyText();
	}

	private string EmptyText() => English()
		? "Not set — the artwork area stays blank"
		: "未设置，作品区保持留白";

	private string FailedText() => English()
		? "This image can't be previewed"
		: "无法预览这张图片";

	private bool English() => UiLanguage.IsEnglish(_field.Language);
}
