using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Nori.Core.Live2D;
using Nori.Desktop.Memory;

namespace Nori.Desktop.Windows;

public sealed partial class ModelsWindow
{
	internal static readonly string[] BehaviorKeys = ["clickInteraction", "clickThrough", "aiInteraction", "autoBlink", "eyeTracking", "idleEyeAnimation", "idleAnimation", "expressionEnabled", "lipSync", "beatSync"];
	private readonly ContentControl _selectedDisplay = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
	private string _displayFor = "";
	private bool _selectedDisplayHasMetadata;
	private readonly List<Action> _selectedBindings = [];
	private readonly List<Action> _selectedLocalize = [];
	private static string BehaviorKey(string key) => "behavior:" + key;
	private static string DisplayKey(string id, string field) => id + ":" + field;
	private MemorySettingDraft Draft(string key, object value, string command, Func<object, object> args)
	{
		if (_drafts.TryGetValue(key, out var existing)) return existing;
		var draft = new MemorySettingDraft(value, async next =>
		{
			_revision++;
			try { await _service.ExecuteAsync(command, args(next), _lifetime.Token); }
			finally { _revision++; QueueRefresh(); }
		}, () => { ApplyBindings(); ShowSaveState(); UpdatePreview(); });
		_drafts.Add(key, draft); return draft;
	}
	private MemorySettingDraft BehaviorDraft(string field)
	{
		bool initial = P(_snapshot, "behaviors").ValueKind == JsonValueKind.Object ? B(P(_snapshot, "behaviors"), field) : field is not ("clickThrough" or "aiInteraction" or "beatSync");
		return Draft(BehaviorKey(field), initial, "model_set_behavior", value => new Dictionary<string, object> { [field] = value });
	}
	private MemorySettingDraft DisplayDraft(string id, string field, object initial) => Draft(DisplayKey(id, field), initial, "model_set_display", value => new Dictionary<string, object> { ["modelId"] = id, [field] = value });
	private void Bind(Action binding) => (_buildingAdjust ? _adjustBindings : _bindings).Add(binding);
	private void ShowSaveState()
	{
		MemorySettingDraft? error = _drafts.Values.FirstOrDefault(draft => draft.Error.Length > 0);
		if (error is not null) { ShowError(new InvalidOperationException(T("保存失败，草稿已保留。", "Save failed. Your draft is retained. ") + error.Error)); return; }
		if (_drafts.Values.Any(draft => draft.Saving)) _status.Text = T("正在保存…", "Saving…");
		else if (_drafts.Values.Any(draft => draft.Dirty)) _status.Text = T("待保存…", "Unsaved changes…");
		else _status.Text = T("已自动保存", "Saved automatically");
		Brush(_status, TextBlock.ForegroundProperty, "SettingsSecondaryBrush");
	}
	private void AcceptModelSnapshot(string id, JsonElement meta)
	{
		foreach (string field in new[] { "scale", "renderScale", "maxFps", "shadow", "expressions", "interactions" })
		{
			if (!_drafts.TryGetValue(DisplayKey(id, field), out var draft)) continue;
			object value = field switch
			{
				"shadow" => B(meta, field),
				"expressions" => ActiveExpressions(id, meta),
				"interactions" => ReadInteractions(meta),
				_ => N(meta, field, field == "renderScale" ? 2 : field == "scale" ? 1 : 0),
			};
			draft.AcceptSnapshot(value);
		}
	}
	private string[] ActiveExpressions(string id, JsonElement meta) => P(meta, "selectedExpressions").ValueKind == JsonValueKind.Array ? Strings(P(meta, "selectedExpressions")) : id == SelectedModel ? Strings(P(P(_snapshot, "models"), "expressions")) : [];
	private static PetInteractionConfig ReadInteractions(JsonElement meta) => P(meta, "interactions").ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize<PetInteractionConfig>(P(meta, "interactions"), PetInteractionJson.Options) ?? PetInteractionConfig.Empty : PetInteractionConfig.Empty;
	private Control BuildBehaviorPage() => Stack(BuildBehaviorGroups(), _selectedDisplay);
	private Control BuildBehaviorGroups()
	{
		var groups = Stack(
			Card(() => T("触碰与回应", "Touch & response"), BuildBehaviorControls(["clickInteraction", "clickThrough", "aiInteraction"])),
			Card(() => T("目光与动作", "Gaze & movement"), BuildBehaviorControls(["autoBlink", "eyeTracking", "idleEyeAnimation", "idleAnimation", "expressionEnabled"])),
			Card(() => T("声音与律动", "Voice & rhythm"), BuildBehaviorControls(["lipSync", "beatSync"])));
		groups.Spacing = 18;
		return groups;
	}
	private Control BuildBehaviorControls(string[] fields)
	{
		var body = Stack(); body.Spacing = 0;
		foreach (string field in fields)
		{
			MemorySettingDraft draft = BehaviorDraft(field);
			var toggle = new ToggleSwitch { Name = "ModelsBehavior_" + field, OnContent = "", OffContent = "" };
			toggle.IsCheckedChanged += async (_, _) => { if (!_applying) { _revision++; draft.Set(toggle.IsChecked == true); await draft.FlushAsync(); } };
			Bind(() =>
			{
				toggle.IsChecked = (bool)draft.Value && (field != "aiInteraction" || AiAvailable);
				toggle.IsEnabled = field switch { "clickThrough" => B(P(_snapshot, "platform"), "supportsHitThrough"), "aiInteraction" => AiAvailable, _ => true };
			});
			body.Children.Add(SettingLine(() => BehaviorLabel(field), () => BehaviorHint(field), toggle));
		}
		return body;
	}
	private string BehaviorLabel(string field) => field switch
	{
		"clickInteraction" => T("点击互动", "Click interaction"), "clickThrough" => T("鼠标穿透", "Click-through"), "aiInteraction" => T("AI 互动", "AI interaction"),
		"autoBlink" => T("自动眨眼", "Auto blink"), "eyeTracking" => T("视线跟随", "Eye tracking"), "idleEyeAnimation" => T("待机眼部动画", "Idle eye animation"),
		"idleAnimation" => T("待机动画", "Idle animation"), "expressionEnabled" => T("表情行为", "Expression behavior"), "lipSync" => T("口型同步", "Lip sync"), _ => T("节拍同步", "Beat sync"),
	};
	private string BehaviorHint(string field) => field switch
	{
		"clickInteraction" => T("点击模型时播放动作与表情。", "Play actions and expressions when the model is clicked."),
		"clickThrough" => B(P(_snapshot, "platform"), "supportsHitThrough") ? T("鼠标可穿过桌宠窗口，不影响下层应用。", "Let pointer input pass through the desktop pet.") : T("当前桌面环境不支持鼠标穿透。", "Click-through is unavailable in this desktop session."),
		"aiInteraction" => AiAvailable ? T("AI 按区域回应，会消耗 Token 并可能产生 API 费用；离线或请求失败时播放本地兜底动作。", "AI reacts to touch and consumes tokens, which may incur API costs. Local actions are used when offline or on failure.") : B(P(_snapshot, "app"), "safeMode") ? T("安全模式下不启用 AI 互动。", "AI interaction is disabled in safe mode.") : T("请先在设置中配置 AI 服务。", "Configure an AI provider in Settings first."),
		"autoBlink" => T("自然地自动眨眼。", "Blink naturally at intervals."),
		"eyeTracking" => T("视线跟随鼠标位置。", "Follow the pointer with Nori’s gaze."),
		"idleEyeAnimation" => T("待机时随机转动眼睛与头部。", "Random eye saccades and head movement while idle."),
		"idleAnimation" => T("静候时保持轻柔的待机动作。", "Play gentle movements while waiting."),
		"expressionEnabled" => T("启用 exp3 表情解析与叠加。", "Enable exp3 expression parsing and stacking."),
		"lipSync" => T("说话时根据音量同步口型。", "Animate the mouth with speech volume."),
		_ => T("随节拍律动，需要外部节拍音源。", "Dance to beats; an external beat source is required."),
	};
	private void UpdateSelectedDisplay()
	{
		string id = SelectedModel;
		bool available = id.Length > 0 && Installed(_snapshot, id) && _metadata.ContainsKey(id);
		if (_displayFor == id && _selectedDisplayHasMetadata == available && _selectedDisplay.Content is not null) return;
		foreach (Action bind in _selectedBindings) _bindings.Remove(bind); _selectedBindings.Clear();
		foreach (Action local in _selectedLocalize) _localize.Remove(local); _selectedLocalize.Clear();
		_displayFor = id; _selectedDisplayHasMetadata = available;
		int bindingStart = _bindings.Count, localStart = _localize.Count;
		_selectedDisplay.Content = available
			? Card(() => T("当前模型显示 · ", "Current model display · ") + ModelName(id), BuildDisplayControls(id))
			: Card(null, Secondary(() => T("导入并启用模型后可调整渲染参数。", "Import and enable a model to adjust rendering.")));
		_selectedBindings.AddRange(_bindings.Skip(bindingStart)); _selectedLocalize.AddRange(_localize.Skip(localStart));
	}
	private Control BuildAppearanceControls(string id)
	{
		JsonElement meta = _metadata.GetValueOrDefault(id);
		return Stack(
			SliderField(id, "scale", () => T("模型大小", "Model size"), 0.5, 4, 0.05, N(meta, "scale", 1), value => DisplayNumber(value * 100, "0") + "%"),
			BuildExpressions(id, meta));
	}
	private Control BuildDisplayControls(string id)
	{
		JsonElement meta = _metadata.GetValueOrDefault(id);
		var body = Stack();
		MemorySettingDraft shadow = DisplayDraft(id, "shadow", B(meta, "shadow"));
		var toggle = new ToggleSwitch { Name = "ModelsDisplay_" + id + "_shadow", OnContent = "", OffContent = "" };
		toggle.IsCheckedChanged += async (_, _) => { if (!_applying) { _revision++; shadow.Set(toggle.IsChecked == true); await shadow.FlushAsync(); } };
		Bind(() => toggle.IsChecked = (bool)shadow.Value);
		body.Children.Add(SettingLine(() => T("模型阴影", "Model shadow"), () => T("为该模型启用柔和阴影。", "Enable a soft shadow for this model."), toggle));
		body.Children.Add(SliderField(id, "renderScale", () => T("渲染倍率", "Render scale"), 0.5, 4, 0.25, N(meta, "renderScale", 2), value => DisplayNumber(value, "0.00") + "×"));
		body.Children.Add(Secondary(() => T("倍率越高越清晰，也会使用更多显存。", "Higher scales improve clarity but use more graphics memory.")));
		MemorySettingDraft fps = DisplayDraft(id, "maxFps", N(meta, "maxFps"));
		var selector = new ComboBox { Name = "ModelsDisplay_" + id + "_maxFps", HorizontalAlignment = HorizontalAlignment.Stretch };
		Localize(() => selector.ItemsSource = new[] { T("不限制", "Unlimited"), "30 FPS", "60 FPS" });
		selector.SelectionChanged += async (_, _) => { if (!_applying && selector.SelectedIndex >= 0) { _revision++; fps.Set((double)(selector.SelectedIndex == 0 ? 0 : selector.SelectedIndex == 1 ? 30 : 60)); await fps.FlushAsync(); } };
		Bind(() => selector.SelectedIndex = Convert.ToDouble(fps.Value) switch { 30 => 1, 60 => 2, _ => 0 });
		body.Children.Add(Field(() => T("最大帧率", "Maximum frame rate"), selector));
		return body;
	}
	private Control SliderField(string id, string field, Func<string> label, double min, double max, double step, double initial, Func<double, string> format)
	{
		MemorySettingDraft draft = DisplayDraft(id, field, initial);
		var slider = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, TickFrequency = step, IsSnapToTickEnabled = true, Name = "ModelsDisplay_" + id + "_" + field };
		Localize(() => Avalonia.Automation.AutomationProperties.SetName(slider, label()));
		var value = Text("", 13, true); value.MinWidth = 54; value.VerticalAlignment = VerticalAlignment.Center;
		Brush(value, TextBlock.ForegroundProperty, "SettingsAccentBrush");
		slider.PropertyChanged += (_, args) => { if (args.Property == RangeBase.ValueProperty && !_applying) { _revision++; draft.Set(slider.Value); } };
		Bind(() => { slider.Value = Convert.ToDouble(draft.Value); value.Text = format(Convert.ToDouble(draft.Value)); });
		var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
		row.Children.Add(slider); Grid.SetColumn(value, 1); row.Children.Add(value); return Field(label, row);
	}
	private Control BuildExpressions(string id, JsonElement meta)
	{
		MemorySettingDraft draft = DisplayDraft(id, "expressions", ActiveExpressions(id, meta));
		var choices = new WrapPanel();
		foreach (string expression in new[] { "" }.Concat(Strings(P(meta, "expressions"))))
		{
			var button = new ToggleButton { Name = "ModelsExpression_" + expression, Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 6, 6), MinHeight = 32 };
			button.Classes.Add("settings-choice");
			Localize(() => button.Content = expression.Length == 0 ? T("无", "None") : ExpressionLabel(expression));
			button.Click += (_, _) =>
			{
				if (_applying) return;
				_revision++;
				draft.Set(expression.Length == 0 || ((string[])draft.Value).Contains(expression) ? Array.Empty<string>() : new[] { expression });
			};
			Bind(() => button.IsChecked = expression.Length == 0 ? ((string[])draft.Value).Length == 0 : ((string[])draft.Value).Contains(expression));
			choices.Children.Add(button);
		}
		var body = Stack(Local(() => T("表情", "Expression"), 13, true), new ScrollViewer { Content = choices, MaxHeight = 160, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
		if (Strings(P(meta, "expressions")).Length == 0) body.Children.Add(Secondary(() => T("该模型没有可用表情。", "This model has no expressions.")));
		body.Children.Add(Secondary(() => T("表情为单选，再次点击可取消。", "Select one expression; click it again to clear it.")));
		return body;
	}
	private string ExpressionLabel(string name) => name switch
	{
		"Default" => T("默认", "Default"), "KiraKira" => T("星星眼", "Sparkle Eyes"), "Dizzy" => T("眩晕", "Dizzy"), "Angry" => T("生气", "Angry"),
		"Shy" => T("害羞", "Shy"), "Dark" => T("黑化", "Dark"), "Speechless" => T("无语", "Speechless"), "Smile" => T("微笑", "Smile"),
		"Tears" => T("流泪", "Tears"), "Troubled" => T("困扰", "Troubled"), "Doubt" => T("疑惑", "Doubt"), "Disgust" => T("嫌弃", "Disgust"),
		"Serious" => T("认真", "Serious"), "Happy" => T("开心", "Happy"), "Surprised" => T("惊讶", "Surprised"), "Sleep" => T("睡觉", "Sleep"),
		"Chibi" => T("Q版", "Chibi"), "Shojo" => T("少女", "Maiden"), "TailOFF" => T("去掉尾巴", "No Tail"), "LongHairOFF" => T("短发", "Short Hair"),
		"Finale_Default" => T("终章·默认", "Finale Default"), "Finale_EyeClosed_Smile" => T("终章·闭眼微笑", "Finale Closed Eye Smile"),
		"Finale_EyeClosed" => T("终章·闭眼", "Finale Closed Eye"), "Finale_Farewell" => T("终章·告别", "Finale Farewell"),
		"Finale_Sad_Smile" => T("终章·悲伤微笑", "Finale Sad Smile"), "Finale_Sad" => T("终章·悲伤", "Finale Sad"), "Finale_Smile" => T("终章·微笑", "Finale Smile"), _ => name,
	};
}
