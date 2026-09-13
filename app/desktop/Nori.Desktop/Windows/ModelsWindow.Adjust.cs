using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Nori.Desktop.Windows;

public sealed partial class ModelsWindow
{
	private readonly ContentControl _adjustPane = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
	private Control? _displayControls;
	private Control? _interactionControls;
	private Button? _displayTab;
	private Button? _interactionTab;

	internal async Task OpenAdjustAsync(string modelId)
	{
		if (!Models.Any(model => model.Id == modelId)) throw new InvalidOperationException(T("未知模型", "Unknown model"));
		if (!await FlushPendingSavesAsync()) return;
		long request = ++_adjustRequest;
		Success(T("正在读取模型…", "Loading model…"));
		var meta = await _service.ExecuteAsync("model_get_meta", new { modelId }, _lifetime.Token);
		if (request != _adjustRequest || !IsVisible || _prepared) return;
		_metadata[modelId] = meta; AcceptModelSnapshot(modelId, meta);
		_adjustFor = modelId; _adjustTab = "display";
		_selectedRegionId = null;
		_overlay.CancelGesture(); _overlay.SelectedId = null; _overlay.Editing = true; _overlay.Creating = false;
		_adjustBindings.Clear(); _adjustLocalize.Clear(); _regionBindings.Clear(); _regionLocalize.Clear();
		_buildingAdjust = true;
		try
		{
			var display = Stack(
				Card(() => T("外观与表情", "Appearance & expression"), BuildAppearanceControls(modelId)),
				Card(() => T("渲染与性能", "Rendering & performance"), BuildDisplayControls(modelId)),
				Secondary(() => T("以下行为对所有模型生效", "The following behaviors apply to every model")), BuildBehaviorGroups());
			display.Spacing = 18;
			_displayControls = Scroller(display);
			_interactionControls = Scroller(BuildInteractionControls(modelId));
			Button back = ActionButton(() => T("返回", "Back"), async () => await CloseAdjustAsync(), "ModelsBack");
			Button done = ActionButton(() => T("完成", "Done"), async () => await CloseAdjustAsync(), "ModelsDone");
			_displayTab = ActionButton(() => T("基础显示", "Display"), () => { ChangeAdjustTab("display"); return Task.CompletedTask; }, "ModelsTabDisplay");
			_interactionTab = ActionButton(() => T("互动区域", "Interaction regions"), () => { ChangeAdjustTab("interactions"); return Task.CompletedTask; }, "ModelsTabInteractions");
			_displayTab.Classes.Add("settings-nav"); _interactionTab.Classes.Add("settings-nav");
			var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
			heading.Children.Add(back);
			var title = Local(() => T("调整模型 · ", "Adjust model · ") + ModelName(modelId), 18, true); title.VerticalAlignment = VerticalAlignment.Center;
			Grid.SetColumn(title, 1); heading.Children.Add(title); Grid.SetColumn(done, 2); heading.Children.Add(done);
			_header.MaxWidth = double.PositiveInfinity; _header.Margin = default;
			_header.Children.Clear(); _header.Children.Add(heading); _header.Children.Add(Row(_displayTab, _interactionTab));
			_sidebar.IsVisible = false; _root.ColumnDefinitions[0].Width = new GridLength(0);
			DetachPreviewHost();
			// 舞台不能用固定最小尺寸撑出页脚；改变网格分配不重新挂载 GL 控件。
			_previewHost.MinWidth = _previewHost.MinHeight = _adjustPane.MinHeight = 0;
			var layout = new Grid { Name = "ModelsAdjustLayout", MinHeight = 0 };
			layout.Children.Add(_previewHost); layout.Children.Add(_adjustPane);
			ApplyAdjustLayout(layout, Math.Max(0, ClientSize.Width - _presenter.Margin.Left - _presenter.Margin.Right));
			layout.PropertyChanged += (_, args) =>
			{
				if (args.Property == BoundsProperty && layout.Bounds.Width > 0) ApplyAdjustLayout(layout, layout.Bounds.Width);
			};
			_presenter.Content = layout;
			Bind(() => { if (_interactionTab is not null) _interactionTab.Content = T("互动区域", "Interaction regions") + " · " + InteractionDraft(modelId).Regions.Count; });
			ChangeAdjustTab("display"); ApplyBindings();
		}
		finally { _buildingAdjust = false; }
		await LoadPreviewAsync(modelId);
	}
	internal async Task<bool> CloseAdjustAsync()
	{
		if (_adjustFor is null) return true;
		_overlay.EndGesture();
		if (!await FlushPendingSavesAsync()) return false;
		_adjustRequest++;
		ReleasePreview();
		_adjustFor = null; _selectedRegionId = null;
		_adjustBindings.Clear(); _adjustLocalize.Clear(); _regionBindings.Clear(); _regionLocalize.Clear();
		DetachPreviewHost();
		if (_adjustPane.Parent is Panel panel) panel.Children.Remove(_adjustPane);
		_adjustPane.Content = null; _displayControls = null; _interactionControls = null;
		_header.MaxWidth = 820; _header.Margin = new Thickness(0, 0, 8, 0);
		_header.Children.Clear(); _header.Children.Add(_heading); _header.Children.Add(_description);
		_sidebar.IsVisible = true; _root.ColumnDefinitions[0].Width = new GridLength(200);
		Navigate(_section); await RefreshAsync(); return true;
	}
	private void ApplyAdjustLayout(Grid layout, double availableWidth)
	{
		bool stacked = availableWidth < 800;
		if (layout.RowDefinitions.Count == (stacked ? 2 : 1)) return;
		layout.RowDefinitions = new RowDefinitions(stacked ? "2*,3*" : "*");
		layout.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,*");
		layout.RowSpacing = stacked ? 16 : 0; layout.ColumnSpacing = stacked ? 0 : 16;
		Grid.SetRow(_adjustPane, stacked ? 1 : 0); Grid.SetColumn(_adjustPane, stacked ? 0 : 1);
	}
	private void ChangeAdjustTab(string tab)
	{
		_overlay.EndGesture();
		_adjustTab = tab;
		_overlay.IsVisible = tab == "interactions";
		_adjustPane.Content = tab == "display" ? _displayControls : _interactionControls;
		_displayTab?.Classes.Set("selected", tab == "display");
		_interactionTab?.Classes.Set("selected", tab == "interactions");
		UpdatePreview();
	}
	private void DetachPreviewHost()
	{
		if (_previewHost.Parent is Panel panel) panel.Children.Remove(_previewHost);
		if (_adjustPane.Parent is Panel previous) previous.Children.Remove(_adjustPane);
	}
	private async Task ClearRegionsConfirmedAsync()
	{
		if (await ConfirmClearAsync()) ClearRegions();
	}
	private async Task<bool> ConfirmClearAsync()
	{
		var dialog = Nori.Desktop.Settings.Pages.NativeSettingsDialogs.CreateWindow(T("清空全部区域", "Clear all regions"), new ContentControl());
		dialog.Name = "ModelsClearConfirmation"; dialog.Width = 400; dialog.MinWidth = 320; dialog.MaxWidth = Math.Max(320, Bounds.Width - 32);
		var confirm = new Button { Content = T("清空全部", "Clear all"), MinHeight = 32 };
		confirm.Classes.Add("danger"); confirm.Click += (_, _) => dialog.Close(true);
		var cancel = new Button { Content = T("取消", "Cancel"), MinHeight = 32 }; cancel.Click += (_, _) => dialog.Close(false);
		var body = Stack(Text(T("确定删除该模型的全部互动区域？此操作不可撤销。", "Delete every interaction region for this model? This cannot be undone.")), Row(cancel, confirm)); body.Margin = new Thickness(24);
		((ContentControl)dialog.Content!).Content = body;
		return await dialog.ShowDialog<bool>(this);
	}
}
