using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Nori.Core.Live2D;
using Nori.Desktop.Memory;

namespace Nori.Desktop.Windows;

public sealed partial class ModelsWindow
{
	private string? _selectedRegionId;
	private ComboBox? _regionList;
	private ContentControl? _regionEditor;
	private string _regionListSignature = "";
	private readonly List<Action> _regionBindings = [];
	private readonly List<Action> _regionLocalize = [];
	private MemorySettingDraft RegionsDraft(string modelId) => Draft(DisplayKey(modelId, "interactions"), ReadInteractions(_metadata.GetValueOrDefault(modelId)), "model_set_interactions", value => new { modelId, interactions = JsonSerializer.SerializeToElement(value, PetInteractionJson.Options) });
	private PetInteractionConfig InteractionDraft(string modelId) => (PetInteractionConfig)RegionsDraft(modelId).Value;
	private PetInteractionRegion? SelectedRegion => _adjustFor is { } id ? InteractionDraft(id).Regions.FirstOrDefault(region => region.Id == _selectedRegionId) : null;
	private Control BuildInteractionControls(string modelId)
	{
		_ = RegionsDraft(modelId);
		var edit = ActionButton(() => T("编辑", "Edit"), () => { _overlay.Editing = true; ApplyBindings(); UpdatePreview(); return Task.CompletedTask; }, "ModelsRegionEdit");
		var test = ActionButton(() => T("测试", "Test"), () => { _overlay.EndGesture(); _overlay.Editing = false; _overlay.Creating = false; ApplyBindings(); UpdatePreview(); return Task.CompletedTask; }, "ModelsRegionTest");
		edit.Classes.Add("settings-nav"); test.Classes.Add("settings-nav");
		Button add = ActionButton(() => T("新建区域", "New region"), () => { AddRegion(); return Task.CompletedTask; }, "ModelsRegionAdd");
		Button draw = ActionButton(() => _overlay.Creating ? T("取消绘制", "Cancel draw") : T("绘制区域", "Draw region"), () => { _overlay.EndGesture(); _overlay.Editing = true; _overlay.Creating = !_overlay.Creating; _overlay.Focus(); ApplyBindings(); UpdatePreview(); return Task.CompletedTask; }, "ModelsRegionDraw");
		Button clear = ActionButton(() => T("清空全部", "Clear all"), async () =>
		{
			Task operation = ClearRegionsConfirmedAsync(); _operations.Add(operation);
			try { await operation; } finally { _operations.Remove(operation); }
		}, "ModelsRegionClear", true);
		_regionList = new ComboBox { Name = "ModelsRegionList", HorizontalAlignment = HorizontalAlignment.Stretch };
		_regionListSignature = "";
		_regionList.SelectionChanged += (_, _) => { if (!_applying) SelectRegion((_regionList.SelectedItem as ComboBoxItem)?.Tag as string); };
		_regionEditor = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
		TextBlock modeHint = Secondary(() => "");
		Bind(() =>
		{
			PetInteractionConfig config = InteractionDraft(modelId);
			_overlay.Regions = config.Regions; _overlay.SelectedId = _selectedRegionId;
			edit.Classes.Set("selected", _overlay.Editing); test.Classes.Set("selected", !_overlay.Editing);
			modeHint.Text = _overlay.Editing ? T("在预览中拖拽空白处创建，拖动区域移动，使用八个手柄缩放。", "Drag empty space to create, drag a region to move, or use its eight resize handles.") : T("测试仅播放本地动作与表情，不发送 AI 请求，不产生 API 费用。", "Tests play local motions and expressions only. No AI requests or API costs.");
			draw.Content = _overlay.Creating ? T("取消绘制", "Cancel draw") : T("绘制区域", "Draw region");
			add.IsEnabled = draw.IsEnabled = config.Regions.Count < PetInteractionConfig.MaxRegions;
			clear.IsEnabled = config.Regions.Count > 0;
			string signature = _language + string.Join("|", config.Regions.Select(region => region.Id + region.Name + region.ReactionMode));
			if (_regionListSignature != signature)
			{
				_regionListSignature = signature;
				_regionList.ItemsSource = config.Regions.Select((region, index) => new ComboBoxItem { Tag = region.Id, Content = $"{index + 1}. {region.Name} · " + (region.ReactionMode == PetInteractionReactionMode.Ai ? "AI" : T("本地", "Local")) }).ToArray();
			}
			_regionList.SelectedItem = _regionList.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == _selectedRegionId);
		});
		BuildRegionEditor();
		return Card(() => T("自定义互动区域", "Custom interaction regions"), Row(edit, test), modeHint, Row(add, draw, clear), Field(() => T("选择区域", "Select region"), _regionList), _regionEditor);
	}
	internal void AddRegion(PetInteractionRect? rect = null)
	{
		if (_adjustFor is not { } id) return;
		PetInteractionConfig config = InteractionDraft(id);
		if (config.Regions.Count >= PetInteractionConfig.MaxRegions) { ShowError(new InvalidOperationException(T("最多可以创建 32 个区域。", "You can create up to 32 regions."))); return; }
		var region = new PetInteractionRegion { Id = "region_" + Guid.NewGuid().ToString("N"), Name = T("新互动区域 ", "New region ") + (config.Regions.Count + 1), Rect = rect ?? new PetInteractionRect { X = 0.25, Y = 0.25, Width = 0.5, Height = 0.5 } };
		SetRegions(new PetInteractionConfig { Regions = [.. config.Regions, region] }); SelectRegion(region.Id);
	}
	internal void DeleteRegion(string id)
	{
		if (_adjustFor is not { } modelId) return;
		SetRegions(new PetInteractionConfig { Regions = InteractionDraft(modelId).Regions.Where(region => region.Id != id).ToList() });
		if (_selectedRegionId == id) SelectRegion(null);
	}
	internal void ClearRegions() { SetRegions(PetInteractionConfig.Empty); SelectRegion(null); }
	private void SetRegions(PetInteractionConfig config)
	{
		if (_adjustFor is not { } id) return;
		_revision++; RegionsDraft(id).Set(config);
	}
	private void UpdateRegion(string id, Func<PetInteractionRegion, PetInteractionRegion> update)
	{
		if (_adjustFor is not { } modelId) return;
		SetRegions(new PetInteractionConfig { Regions = InteractionDraft(modelId).Regions.Select(region => region.Id == id ? update(region) : region).ToList() });
	}
	internal void SelectRegion(string? id)
	{
		if (_selectedRegionId == id && _regionEditor?.Content is not null) return;
		_selectedRegionId = id; _overlay.SelectedId = id; BuildRegionEditor(); ApplyBindings();
	}
	private void BuildRegionEditor()
	{
		if (_regionEditor is null) return;
		foreach (Action action in _regionBindings) _adjustBindings.Remove(action); _regionBindings.Clear();
		foreach (Action action in _regionLocalize) _adjustLocalize.Remove(action); _regionLocalize.Clear();
		bool previous = _buildingAdjust; _buildingAdjust = true;
		int bindingStart = _adjustBindings.Count, localStart = _adjustLocalize.Count;
		try
		{
			if (SelectedRegion is not { } region || _adjustFor is not { } modelId)
			{
				_regionEditor.Content = Secondary(() => _adjustFor is { } current && InteractionDraft(current).Regions.Count > 0 ? T("选择一个区域以编辑其名称和响应。", "Select a region to edit its name and response.") : T("暂无互动区域。新建区域或在预览上框选。", "No regions yet. Add one or draw on the preview."));
				return;
			}
			string id = region.Id;
			var name = new TextBox { Name = "ModelsRegionName", MaxLength = 24, Text = region.Name, HorizontalAlignment = HorizontalAlignment.Stretch };
			// TextChanged 会延后派发；直接跟踪属性，确保紧接着关闭时最后一次输入也进入草稿。
			name.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty && !_applying) UpdateRegion(id, item => item with { Name = name.Text ?? "" }); };
			Bind(() => { if (!name.IsKeyboardFocusWithin && SelectedRegion is { } selected) name.Text = selected.Name; });
			var local = new ToggleButton { Name = "ModelsReactionLocal", MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
			var ai = new ToggleButton { Name = "ModelsReactionAi", MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
			local.Classes.Add("settings-choice"); ai.Classes.Add("settings-choice");
			Localize(() => { local.Content = T("本地反应", "Local reaction"); ai.Content = T("AI 反应", "AI response"); });
			local.Click += (_, _) => { if (!_applying) UpdateRegion(id, item => item with { ReactionMode = PetInteractionReactionMode.Local }); };
			ai.Click += (_, _) => { if (!_applying && AiAvailable) UpdateRegion(id, item => item with { ReactionMode = PetInteractionReactionMode.Ai }); };
			var reactions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
			reactions.Children.Add(local); Grid.SetColumn(ai, 1); reactions.Children.Add(ai);
			TextBlock fallback = Secondary(() => T("AI 请求会消耗 Token 并可能产生费用。离线、未配置或请求失败时使用下方本地动作与表情兜底。", "AI requests consume tokens and may incur costs. The local actions below are the fallback when offline, unconfigured or on failure."));
			TextBlock disabled = Secondary(() => T("AI 尚未配置或处于安全模式，无法启用 AI 反应。", "Configure AI and leave safe mode to enable AI responses."));
			Bind(() => { local.IsChecked = SelectedRegion?.ReactionMode == PetInteractionReactionMode.Local; ai.IsChecked = SelectedRegion?.ReactionMode == PetInteractionReactionMode.Ai; ai.IsEnabled = AiAvailable; fallback.IsVisible = ai.IsChecked == true; disabled.IsVisible = !AiAvailable; });
			Button delete = ActionButton(() => T("删除此区域", "Delete region"), () => { DeleteRegion(id); return Task.CompletedTask; }, "ModelsRegionDelete", true);
			Button focus = ActionButton(() => T("聚焦预览编辑", "Focus preview editor"), () => { _overlay.Focus(); return Task.CompletedTask; }, "ModelsRegionFocus");
			_regionEditor.Content = Stack(Field(() => T("区域名称", "Region name"), name), Field(() => T("反应模式", "Reaction mode"), reactions), disabled, fallback, BuildActionBinding(modelId, id, true), BuildActionBinding(modelId, id, false), Secondary(() => T("方向键移动 1%，Shift + 方向键移动 5%；Delete 删除，Esc 取消选中。仅预览获得焦点时生效。", "Arrow keys move 1%, Shift + arrows move 5%; Delete removes, Esc deselects. Shortcuts apply only when the preview is focused.")), Row(focus, delete));
		}
		finally { _regionBindings.AddRange(_adjustBindings.Skip(bindingStart)); _regionLocalize.AddRange(_adjustLocalize.Skip(localStart)); _buildingAdjust = previous; }
	}
	private Control BuildActionBinding(string modelId, string regionId, bool motion)
	{
		var mode = new ComboBox { Name = motion ? "ModelsMotionMode" : "ModelsExpressionMode", HorizontalAlignment = HorizontalAlignment.Stretch };
		var group = new ComboBox { Name = "ModelsMotionGroup", HorizontalAlignment = HorizontalAlignment.Stretch };
		var names = new ComboBox { Name = motion ? "ModelsMotionName" : "ModelsExpressionName", HorizontalAlignment = HorizontalAlignment.Stretch };
		var warning = Text("", 12); Brush(warning, TextBlock.ForegroundProperty, "SettingsErrorBrush");
		JsonElement meta = _metadata.GetValueOrDefault(modelId);
		MotionGroupInfo[] motions = Items(P(meta, "motions")).Select(item => new MotionGroupInfo { Group = S(item, "group"), Names = Strings(P(item, "names")).ToList() }).ToArray();
		string[] expressions = Strings(P(meta, "expressions"));
		bool available = motion ? motions.Any(item => item.Names.Count > 0) : expressions.Length > 0;
		Localize(() =>
		{
			mode.ItemsSource = new[] { new ComboBoxItem { Content = T("无", "None") }, new ComboBoxItem { Content = T("随机", "Random") }, new ComboBoxItem { Content = T("指定", "Selected"), IsEnabled = available } };
			names.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((value, _) => Text(motion ? value ?? "" : ExpressionLabel(value ?? "")));
		});
		TextBlock empty = Secondary(() => motion ? T("该模型没有可用动作。", "This model has no motions.") : T("该模型没有可用表情。", "This model has no expressions."));
		empty.Name = motion ? "ModelsMotionEmpty" : "ModelsExpressionEmpty"; empty.IsVisible = !available;
		group.ItemsSource = motions.Select(item => item.Group).ToArray();
		PetInteractionAction Current() => (motion ? SelectedRegion?.Motion : SelectedRegion?.Expression) ?? PetInteractionAction.None;
		void Save(PetInteractionAction action) => UpdateRegion(regionId, region => motion ? region with { Motion = action } : region with { Expression = action });
		mode.SelectionChanged += (_, _) =>
		{
			if (_applying || mode.SelectedIndex < 0) return;
			Save(mode.SelectedIndex switch
			{
				0 => PetInteractionAction.None, 1 => PetInteractionAction.Random,
				_ => motion ? new PetInteractionAction { Mode = PetInteractionActionMode.Selected, Group = motions.FirstOrDefault()?.Group, Name = motions.FirstOrDefault()?.Names.FirstOrDefault() } : new PetInteractionAction { Mode = PetInteractionActionMode.Selected, Name = expressions.FirstOrDefault() },
			});
		};
		group.SelectionChanged += (_, _) => { if (!_applying && group.SelectedItem is string selected) Save(Current() with { Group = selected, Name = motions.FirstOrDefault(item => item.Group == selected)?.Names.FirstOrDefault() }); };
		names.SelectionChanged += (_, _) => { if (!_applying && names.SelectedItem is string selected) Save(Current() with { Name = selected }); };
		Control groupField = Field(() => T("动作分组", "Motion group"), group);
		Control nameField = Field(() => motion ? T("动作名称", "Motion name") : T("表情名称", "Expression name"), names);
		string? currentGroup = null;
		Bind(() =>
		{
			PetInteractionAction action = Current(); mode.SelectedIndex = (int)action.Mode;
			groupField.IsVisible = motion && action.Mode == PetInteractionActionMode.Selected; nameField.IsVisible = action.Mode == PetInteractionActionMode.Selected;
			if (!motion) names.ItemsSource = expressions;
			else if (currentGroup != action.Group) { currentGroup = action.Group; names.ItemsSource = motions.FirstOrDefault(item => item.Group == currentGroup)?.Names ?? []; }
			group.SelectedItem = action.Group; names.SelectedItem = action.Name;
			bool groupValid = !motion || motions.Any(item => item.Group.Equals(action.Group, StringComparison.OrdinalIgnoreCase));
			bool nameValid = motion ? motions.Any(item => item.Group.Equals(action.Group, StringComparison.OrdinalIgnoreCase) && item.Names.Contains(action.Name ?? "", StringComparer.OrdinalIgnoreCase)) : expressions.Contains(action.Name ?? "", StringComparer.OrdinalIgnoreCase);
			warning.IsVisible = action.Mode == PetInteractionActionMode.Selected && (!groupValid || !nameValid);
			warning.Text = !groupValid ? T("绑定的动作组不存在，请重新选择。", "The bound motion group is missing. Choose another.") : motion ? T("绑定的动作文件不存在，请重新选择。", "The bound motion file is missing. Choose another.") : T("绑定的表情文件不存在，请重新选择。", "The bound expression is missing. Choose another.");
		});
		return Stack(Field(() => motion ? T("动作响应", "Motion response") : T("表情响应", "Expression response"), mode), empty, groupField, nameField, warning);
	}
}
