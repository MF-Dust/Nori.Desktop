using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Nori.Desktop.Appearance;
using static Nori.Desktop.SnapshotJson;

namespace Nori.Desktop.Windows;

public sealed partial class ModelsWindow
{
	private readonly List<Bitmap> _thumbnails = [];
	private void BuildLibrary()
	{
		var summary = Secondary(() => "");
		Bind(() => summary.Text = T("当前使用：", "Current: ") + (SelectedModel.Length == 0 ? T("无", "None") : ModelName(SelectedModel)) + "   ·   " + T("已安装：", "Installed: ") + Models.Count(model => Installed(_snapshot, model.Id)) + " / " + Models.Length);
		var models = new UniformGrid { Columns = 1, Rows = 0 };
		foreach (var model in Models)
		{
			using Stream stream = AssetLoader.Open(new Uri("avares://Nori.Desktop/Assets/Models/" + model.Image));
			var bitmap = new Bitmap(stream); _thumbnails.Add(bitmap);
			var image = new Image { Source = bitmap, Stretch = Stretch.UniformToFill, Width = 88, Height = 124 };
			var portrait = new Border { Child = image, CornerRadius = new CornerRadius(NoriMetrics.RadiusSm), ClipToBounds = true };
			var status = Text("", NoriMetrics.FontXs);
			Brush(status, TextBlock.ForegroundProperty, "SettingsAccentBrush");
			Button enable = ActionButton(() => T("启用模型", "Use model"), async () =>
			{
				if (!await FlushPendingSavesAsync()) return;
				await MutateAsync("model_select", new { modelId = model.Id }); await RefreshAsync();
			}, "ModelsEnable_" + model.Id);
			Button adjust = ActionButton(() => T("调整", "Adjust"), () => OpenAdjustAsync(model.Id), "ModelsAdjust_" + model.Id);
			Button import = ActionButton(() => T("导入 ZIP", "Import ZIP"), () => ImportAsync("zip"), "ModelsImport_" + model.Id);
			Button download = ActionButton(() => T("在线获取", "Download"), () => DownloadAsync(model.Id), "ModelsDownload_" + model.Id);
			ToolTip.SetTip(download, T("从 static.nori.mom 下载", "Download from static.nori.mom"));
			var labels = Stack(Text(model.Name, NoriMetrics.FontXl, true), status, Row(enable, adjust, download, import)); labels.Spacing = 8; labels.VerticalAlignment = VerticalAlignment.Center;
			var row = new Grid { ColumnDefinitions = new ColumnDefinitions("88,*"), ColumnSpacing = 16 }; row.Children.Add(portrait); Grid.SetColumn(labels, 1); row.Children.Add(labels);
			Border card = Card(null, row); card.Margin = new Thickness(0, 0, 8, 10); card.Name = "ModelsCard_" + model.Id; models.Children.Add(card);
			Bind(() =>
			{
				bool installed = Installed(_snapshot, model.Id), current = SelectedModel == model.Id;
				status.Text = (installed ? T("已安装", "Installed") : T("未安装", "Not installed")) + (current ? T(" · 当前使用", " · Current") : "");
				enable.IsVisible = adjust.IsVisible = installed; import.IsVisible = download.IsVisible = !installed;
				enable.IsEnabled = installed && !current; adjust.IsEnabled = installed && !_importing; import.IsEnabled = !_importing;
				download.IsEnabled = !_importing && !B(P(_snapshot, "app"), "safeMode");
				enable.Content = current ? T("已启用", "Enabled") : T("启用模型", "Use model");
				Brush(card, Border.BorderBrushProperty, current ? "SettingsAccentBrush" : "SettingsBorderBrush");
			});
		}
		Button zip = ActionButton(() => T("导入 ZIP 文件", "Import ZIP file"), () => ImportAsync("zip"), "ModelsImportZip");
		Button folder = ActionButton(() => T("导入文件夹", "Import folder"), () => ImportAsync("folder"), "ModelsImportFolder");
		Bind(() => { zip.IsEnabled = folder.IsEnabled = !_importing; });
		var body = Stack(summary, Row(zip, folder), models);
		var scroll = Scroller(body);
		scroll.PropertyChanged += (_, args) => { if (args.Property == ScrollViewer.ViewportProperty) models.Columns = scroll.Viewport.Width >= 740 ? 2 : 1; };
		_library = scroll;
	}
	/// <summary>从 static.nori.mom 获取内置模型，与本地导入共用互斥状态。</summary>
	internal async Task DownloadAsync(string modelId)
	{
		if (_importing) return;
		_importing = true; ApplyBindings(); Success(T("正在从 static.nori.mom 下载 ", "Downloading from static.nori.mom: ") + ModelName(modelId) + "…");
		try
		{
			await MutateAsync("model_download_remote", new { modelId });
			Success(T("下载完成：", "Downloaded: ") + ModelName(modelId));
			await RefreshAsync();
		}
		finally { _importing = false; ApplyBindings(); }
	}
	internal async Task ImportAsync(string sourceKind)
	{
		if (_importing) return;
		_importing = true; ApplyBindings(); Success(T("请选择本地模型…", "Choose a local model…"));
		try
		{
			var result = await MutateAsync("model_import_local", new { sourceKind, resourceType = "live2d" });
			string[] imported = Strings(result);
			Success(imported.Length > 0 ? T("导入成功：", "Imported: ") + string.Join(", ", imported) : T("已取消导入", "Import cancelled"));
			await RefreshAsync();
		}
		finally { _importing = false; ApplyBindings(); }
	}
}
