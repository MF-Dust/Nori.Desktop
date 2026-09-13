using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

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
			var portrait = new Border { Child = image, CornerRadius = new CornerRadius(7), ClipToBounds = true };
			var status = Text("", 12);
			Brush(status, TextBlock.ForegroundProperty, "SettingsAccentBrush");
			Button enable = ActionButton(() => T("启用模型", "Use model"), async () =>
			{
				if (!await FlushPendingSavesAsync()) return;
				await MutateAsync("model_select", new { modelId = model.Id }); await RefreshAsync();
			}, "ModelsEnable_" + model.Id);
			Button adjust = ActionButton(() => T("调整", "Adjust"), () => OpenAdjustAsync(model.Id), "ModelsAdjust_" + model.Id);
			Button import = ActionButton(() => T("导入 ZIP", "Import ZIP"), () => ImportAsync("zip"), "ModelsImport_" + model.Id);
			var labels = Stack(Text(model.Name, 17, true), status, Row(enable, adjust, import)); labels.Spacing = 8; labels.VerticalAlignment = VerticalAlignment.Center;
			var row = new Grid { ColumnDefinitions = new ColumnDefinitions("88,*"), ColumnSpacing = 16 }; row.Children.Add(portrait); Grid.SetColumn(labels, 1); row.Children.Add(labels);
			Border card = Card(null, row); card.Margin = new Thickness(0, 0, 8, 10); card.Name = "ModelsCard_" + model.Id; models.Children.Add(card);
			Bind(() =>
			{
				bool installed = Installed(_snapshot, model.Id), current = SelectedModel == model.Id;
				status.Text = (installed ? T("已安装", "Installed") : T("未安装", "Not installed")) + (current ? T(" · 当前使用", " · Current") : "");
				enable.IsVisible = adjust.IsVisible = installed; import.IsVisible = !installed;
				enable.IsEnabled = installed && !current; adjust.IsEnabled = installed && !_importing; import.IsEnabled = !_importing;
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
