using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Nori.Core.Live2D;
using Nori.Desktop.Bridge;
using Nori.Desktop.Memory;
using Nori.Desktop.Models;
using Nori.Desktop.Settings;

namespace Nori.Desktop.Windows;

public sealed partial class ModelsWindow
{
	private readonly Border _previewHost = new()
	{
		Name = "ModelsPreviewHost",
		CornerRadius = new CornerRadius(10),
		BorderThickness = new Thickness(1),
		ClipToBounds = true,
		HorizontalAlignment = HorizontalAlignment.Stretch,
		VerticalAlignment = VerticalAlignment.Stretch,
	};
	private readonly Grid _previewLayer = new() {ClipToBounds = true};
	private readonly ModelRegionOverlay _overlay = new() {IsVisible = false};
	private readonly TextBlock _previewStatus = new()
	{
		HorizontalAlignment = HorizontalAlignment.Center,
		VerticalAlignment = VerticalAlignment.Center,
		TextAlignment = Avalonia.Media.TextAlignment.Center,
		TextWrapping = Avalonia.Media.TextWrapping.Wrap,
		Margin = new Thickness(18),
		IsHitTestVisible = false,
	};
	private ModelPreviewControl? _preview;
	private CancellationTokenSource? _previewLoadCancellation;
	private long _previewRequest;
	private string? _previewExpression;

	private void InitializePreview(AppServices services)
	{
		_preview = new ModelPreviewControl(services) {Name = "ModelsPreview"};
		_preview.ModelReady += OnPreviewReady;
		_preview.ModelLoadFailed += OnPreviewLoadFailed;
		_preview.ViewportChanged += OnPreviewViewportChanged;

		_overlay.SelectionChanged += SelectRegion;
		_overlay.RectChanged += (id, rect) => UpdateRegion(id, region => region with {Rect = rect});
		_overlay.RegionCreated += AddRegion;
		_overlay.RegionDeleted += DeleteRegion;
		_overlay.RegionTested += region => _preview?.TestRegion(region);
		_overlay.BackgroundTested += point => _preview?.TapAt(point);
		_overlay.CreationFinished += ApplyBindings;
		AddHandler(Button.ClickEvent, (_, args) =>
		{
			if (_applying || _adjustFor is not { } modelId || args.Source is not Button button) return;
			// 显式选择“无”即使草稿未变也要清除测试表情；普通快照不能打断本地试播。
			if (button.Name?.StartsWith("ModelsExpression_", StringComparison.Ordinal) == true)
				UpdatePreviewExpression(modelId, force: true);
			else if (button.Name == "ModelsRetry" && _preview is {IsReady: false, ErrorMessage: not null})
				_ = LoadPreviewAsync(modelId);
		});

		_previewLayer.Children.Add(_preview);
		_previewLayer.Children.Add(_overlay);
		_previewLayer.Children.Add(_previewStatus);
		_previewHost.Child = _previewLayer;
		_previewHost.Background = SettingsBrushes.Resolve(this, "SettingsCardBrush");
		_previewHost.BorderBrush = SettingsBrushes.Resolve(this, "SettingsBorderBrush");
		_previewStatus.Foreground = SettingsBrushes.Resolve(this, "SettingsSecondaryBrush");
		_overlay.Accent = SettingsBrushes.Resolve(this, "SettingsAccentBrush");
		_overlay.Surface = SettingsBrushes.Resolve(this, "SettingsCardBrush");
		_overlay.LabelBrush = SettingsBrushes.Resolve(this, "SettingsPrimaryBrush");
	}

	private Task LoadPreviewAsync(string modelId)
	{
		if (_preview is null || _prepared || !IsVisible) return Task.CompletedTask;

		CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
		CancellationTokenSource? previous = _previewLoadCancellation;
		_previewLoadCancellation = cancellation;
		long request = ++_previewRequest;
		try { previous?.Cancel(); }
		catch (ObjectDisposedException) { }

		_previewExpression = null;
		_previewStatus.Text = T("正在准备原生模型预览…", "Preparing native model preview…");
		_previewStatus.IsVisible = true;
		_ = ObservePreviewLoadAsync(modelId, request, cancellation);
		return Task.CompletedTask;
	}

	private async Task ObservePreviewLoadAsync(
		string modelId,
		long request,
		CancellationTokenSource cancellation)
	{
		try
		{
			await _preview!.LoadModelAsync(modelId, cancellation.Token);
		}
		catch (OperationCanceledException)
		{
			if (request == _previewRequest && _adjustFor == modelId && IsVisible)
			{
				_previewStatus.IsVisible = false;
				ShowSaveState();
			}
		}
		catch (Exception exception)
		{
			if (request == _previewRequest && _adjustFor == modelId && IsVisible) ShowError(exception);
		}
		finally
		{
			if (ReferenceEquals(_previewLoadCancellation, cancellation)) _previewLoadCancellation = null;
			cancellation.Dispose();
		}
	}

	private void OnPreviewReady(object? sender, ModelPreviewReadyEventArgs args)
	{
		if (_adjustFor != args.ModelId || !IsVisible) return;
		_previewExpression = null;
		_previewStatus.IsVisible = false;
		ShowSaveState();
		UpdatePreview();
	}

	private void OnPreviewLoadFailed(object? sender, ModelPreviewErrorEventArgs args)
	{
		if (_adjustFor != args.ModelId || !IsVisible) return;
		_previewStatus.Text = args.Message;
		_previewStatus.IsVisible = true;
		UpdatePreviewViewport();
	}

	private void OnPreviewViewportChanged(object? sender, EventArgs args) => UpdatePreviewViewport();

	private void UpdatePreview()
	{
		if (_preview is null || _adjustFor is not { } modelId) return;
		JsonElement meta = _metadata.GetValueOrDefault(modelId);
		_preview.PreviewScale = (float)DraftNumber(modelId, "scale", N(meta, "scale", 1));
		_preview.RenderScale = (float)DraftNumber(modelId, "renderScale", N(meta, "renderScale", 2));
		_preview.ShadowEnabled = DraftBoolean(modelId, "shadow", B(meta, "shadow"));
		_preview.MaxFps = (int)DraftNumber(modelId, "maxFps", N(meta, "maxFps"));
		_preview.QualityMode = Live2DRenderSettings.ParseQualityMode(S(meta, "qualityMode", "adaptive"))
			?? Live2DQualityMode.Adaptive;
		// 独立运行时继承全局视觉行为与活动草稿，不接入穿透、AI 或音频回调。
		foreach (string key in BehaviorKeys)
		{
			bool enabled = _drafts.TryGetValue(BehaviorKey(key), out MemorySettingDraft? draft)
				? Convert.ToBoolean(draft.Value)
				: B(P(_snapshot, "behaviors"), key);
			_preview.ApplyLocalBehavior(key, enabled);
		}
		if (_drafts.TryGetValue(DisplayKey(modelId, "interactions"), out MemorySettingDraft? interactions))
			_overlay.Regions = ((PetInteractionConfig)interactions.Value).Regions;
		UpdatePreviewViewport();
		UpdatePreviewExpression(modelId);
	}

	private double DraftNumber(string modelId, string field, double fallback) =>
		_drafts.TryGetValue(DisplayKey(modelId, field), out MemorySettingDraft? draft)
			? Convert.ToDouble(draft.Value)
			: fallback;

	private bool DraftBoolean(string modelId, string field, bool fallback) =>
		_drafts.TryGetValue(DisplayKey(modelId, field), out MemorySettingDraft? draft)
			? Convert.ToBoolean(draft.Value)
			: fallback;

	private void UpdatePreviewViewport()
	{
		_overlay.Viewport = _preview is not null && _preview.TryGetModelViewport(out Rect viewport)
			? viewport
			: default;
	}

	private void UpdatePreviewExpression(string modelId, bool force = false)
	{
		if (_preview is null || !_preview.IsReady) return;
		string expression = _drafts.TryGetValue(DisplayKey(modelId, "expressions"), out MemorySettingDraft? draft)
			? ((string[])draft.Value).FirstOrDefault() ?? ""
			: ActiveExpressions(modelId, _metadata.GetValueOrDefault(modelId)).FirstOrDefault() ?? "";
		if (!force && _previewExpression == expression) return;
		_previewExpression = expression;
		_preview.StopExpression();
		if (expression.Length > 0) _preview.PlayExpression(expression);
	}

	private void ReleasePreview()
	{
		_previewRequest++;
		CancellationTokenSource? cancellation = _previewLoadCancellation;
		_previewLoadCancellation = null;
		try { cancellation?.Cancel(); }
		catch (ObjectDisposedException) { }
		_previewExpression = null;
		_overlay.Viewport = default;
		_previewStatus.IsVisible = false;
		_preview?.ClearModel();
		ShowSaveState();
	}

	private void SetPreviewActive(bool active)
	{
		if (_preview is null) return;
		if (!active)
		{
			_previewRequest++;
			try { _previewLoadCancellation?.Cancel(); }
			catch (ObjectDisposedException) { }
			_preview.Pause();
			return;
		}

		_preview.Resume();
		if (_adjustFor is { } modelId && (!_preview.IsReady || _preview.LoadedModelId != modelId))
			_ = LoadPreviewAsync(modelId);
	}

	private void DisposePreview()
	{
		_previewRequest++;
		try { _previewLoadCancellation?.Cancel(); }
		catch (ObjectDisposedException) { }
		_previewLoadCancellation = null;
		if (_preview is null) return;
		_preview.ModelReady -= OnPreviewReady;
		_preview.ModelLoadFailed -= OnPreviewLoadFailed;
		_preview.ViewportChanged -= OnPreviewViewportChanged;
		_preview.Dispose();
		_preview = null;
	}
}
