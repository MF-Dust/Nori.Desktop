using Avalonia.Threading;
using Nori.Core.Agent;
using Nori.Core.Emotion;
using Nori.Core.Logging;
using Nori.Core.Live2D;
using Nori.Core.Security;
using Nori.Core.Voice;
using Nori.Desktop.Windows;

namespace Nori.Desktop.Runtime;

public sealed partial class AppRuntime
{
	private void CancelPetInteractionRequest() => CancelPetInteractionRequest(false);

	/// <summary>取消当前伴侣 AI 请求；聊天抢占时只补发一次本地兜底。</summary>
	private void CancelPetInteractionRequest(bool applyLocalFallback)
	{
		CancellationTokenSource? requestCts;
		PetInteractionTrigger? fallback = null;
		lock (_petInteractionThrottleGate)
		{
			requestCts = _petInteractionCts;
			if (applyLocalFallback
				&& !_activePetInteractionFallbackPosted
				&& _activePetInteractionTrigger is { } trigger)
			{
				_activePetInteractionFallbackPosted = true;
				fallback = trigger;
			}
		}
		try { requestCts?.Cancel(); }
		catch (ObjectDisposedException) { }
		if (fallback is not null) PostPetInteractionFallback(fallback);
	}

	private void OnPetInteractionTriggered(PetInteractionTrigger trigger)
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		if (!IsPetInteractionAiEnabled() || !IsLlmConfigured() || _sessions.Count > 0)
		{
			PostPetInteractionFallback(trigger);
			return;
		}
		if (!_petInteractionGate.Wait(0))
		{
			PostPetInteractionFallback(trigger);
			return;
		}

		DateTimeOffset now = DateTimeOffset.UtcNow;
		lock (_petInteractionThrottleGate)
		{
			if (now - _lastPetInteractionAt < TimeSpan.FromSeconds(3))
			{
				_petInteractionGate.Release();
				PostPetInteractionFallback(trigger);
				return;
			}
			_lastPetInteractionAt = now;
		}

		Task task = RunPetInteractionAsync(trigger);
		TrackTask(task);
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "宠物交互失败后的本地回退不能覆盖原始异常。")]
	private async Task RunPetInteractionAsync(PetInteractionTrigger trigger)
	{
		using CancellationTokenSource requestCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
		lock (_petInteractionThrottleGate)
		{
			_petInteractionCts = requestCts;
			_activePetInteractionTrigger = trigger;
			_activePetInteractionFallbackPosted = false;
		}
		try
		{
			PetInteractionReactionRequest request = new()
			{
				ModelId = trigger.ModelId,
				RegionId = trigger.Hit.Region.Id,
				RegionName = trigger.Hit.Region.Name,
				ModelX = trigger.Hit.ModelX,
				ModelY = trigger.Hit.ModelY,
				RegionX = trigger.Hit.RegionX,
				RegionY = trigger.Hit.RegionY,
				CurrentEmotion = Emotion.CurrentType,
				AvailableMotions = Services.PetRuntime.MotionGroups
					.Select(group => new MotionGroupInfo {Group = group.Group, Names = [.. group.Names]})
					.ToArray(),
				AvailableExpressions = Services.PetRuntime.Expressions.ToArray(),
			};
			PetInteractionReaction reaction = await _petInteractionService.ReactAsync(request, requestCts.Token).ConfigureAwait(false);
			if (requestCts.IsCancellationRequested || !IsCurrentPetInteraction(trigger)) return;
			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (!requestCts.IsCancellationRequested) ApplyPetInteractionReaction(trigger, reaction);
			});
		}
		catch (OperationCanceledException) when (requestCts.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
		{
			// 应用退出、模型切换、隐藏或聊天抢占时取消，不显示错误也不应用旧结果。
		}
		catch (Exception exception)
		{
			try { Services.Logger.Write(LogSource.Backend, "warn", $"伴侣 AI 互动失败: {SensitiveDataRedactor.ExceptionSummary(exception)}"); } catch { }
			PostActivePetInteractionFallback(trigger, requestCts);
		}
		finally
		{
			lock (_petInteractionThrottleGate)
			{
				if (ReferenceEquals(_petInteractionCts, requestCts))
				{
					_petInteractionCts = null;
					_activePetInteractionTrigger = null;
					_activePetInteractionFallbackPosted = false;
				}
			}
			_petInteractionGate.Release();
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "交互表现失败不能阻断后续状态更新。")]
	private void ApplyPetInteractionReaction(PetInteractionTrigger trigger, PetInteractionReaction reaction)
	{
		if (!IsCurrentPetInteraction(trigger)) return;
		if (!string.IsNullOrWhiteSpace(reaction.Emotion) && EmotionTypes.IsValid(reaction.Emotion))
		{
			try { Emotion.SetEmotion(reaction.Emotion); } catch { }
		}
		if (!string.IsNullOrWhiteSpace(reaction.Motion)) Services.PetRuntime.PlayMotionByName(reaction.Motion);
		if (!string.IsNullOrWhiteSpace(reaction.Expression)) Services.PetRuntime.PlayExpression(reaction.Expression);
		if (string.IsNullOrWhiteSpace(reaction.Text)) return;
		Services.Windows.ShowPetSpeech(reaction.Text);
		bool autoTts = ParseBoolFlag(Services.Config.GetStringOr("tts_auto_play", "")) ?? false;
		if (autoTts) StartPetInteractionSpeech(reaction.Text);
	}

	private void PostActivePetInteractionFallback(PetInteractionTrigger trigger, CancellationTokenSource requestCts)
	{
		bool shouldPost = false;
		lock (_petInteractionThrottleGate)
		{
			if (ReferenceEquals(_petInteractionCts, requestCts) && !_activePetInteractionFallbackPosted)
			{
				_activePetInteractionFallbackPosted = true;
				shouldPost = true;
			}
		}
		if (shouldPost) PostPetInteractionFallback(trigger);
	}

	private void PostPetInteractionFallback(PetInteractionTrigger trigger)
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (IsCurrentPetInteraction(trigger)) Services.PetRuntime.ApplyLocalInteraction(trigger.Hit.Region);
		});
	}

	private bool IsCurrentPetInteraction(PetInteractionTrigger trigger) =>
		Services.Windows.IsWindowVisible(WindowLabels.Pet)
		&& Services.PetRuntime.CurrentModelId.Equals(trigger.ModelId, StringComparison.OrdinalIgnoreCase)
		&& Services.PetRuntime.ModelGeneration == trigger.ModelGeneration;

	private bool IsPetInteractionAiEnabled() =>
		!Services.SafeMode
		&& (ParseBoolFlag(Services.Config.GetStringOr(PetInteractionConfig.AiEnabledKey, "")) ?? false);

	private bool IsLlmConfigured() => Services.AiSettings.Read().Chat.IsConfigured;

	private void StartPetInteractionSpeech(string text)
	{
		CancelPetInteractionSpeech();
		CancellationTokenSource speechCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
		lock (_petSpeechGate) _petSpeechCts = speechCts;
		TrackTask(SpeakPetInteractionSafelyAsync(text, speechCts));
	}

	private void CancelPetInteractionPresentation()
	{
		CancelPetInteractionSpeech();
		Dispatcher.UIThread.Post(Services.Windows.ClearPetSpeech);
	}

	private void OnPetModelStateChanged() => InvalidateSnapshot();

	private void CancelPetInteractionSpeech()
	{
		CancellationTokenSource? speechCts;
		lock (_petSpeechGate)
		{
			speechCts = _petSpeechCts;
			_petSpeechCts = null;
		}
		try { speechCts?.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S2486", Justification = "语音播放失败必须降级为静默，不能阻断交互。")]
	private async Task SpeakPetInteractionSafelyAsync(string text, CancellationTokenSource speechCts)
	{
		try
		{
			// 伴侣互动朗读同样带上全局情绪状态，让 TTS 情感与表情联动一致。
			TtsSynthesizeOptions speechOptions = new() {EmotionText = Emotion.CurrentType};
			await Voice.SpeakAsync(text, speechOptions, speechCts.Token);
		}
		catch (OperationCanceledException) when (speechCts.IsCancellationRequested)
		{
			// 隐藏、切换模型、开始聊天或退出时取消，不作为播放失败。
		}
		catch (Exception exception)
		{
			try { Services.Logger.Write(LogSource.Backend, "warn", $"伴侣互动朗读失败: {SensitiveDataRedactor.ExceptionSummary(exception)}"); } catch { }
		}
		finally
		{
			lock (_petSpeechGate)
			{
				if (ReferenceEquals(_petSpeechCts, speechCts)) _petSpeechCts = null;
			}
			speechCts.Dispose();
		}
	}

	private async Task SpeakSafelyAsync(string text)
	{
		try
		{
			await Voice.SpeakAsync(text);
		}
		catch (Exception exception)
		{
			try
			{
				Services.Logger.Write(LogSource.Backend, "warn", $"主动朗读失败: {SensitiveDataRedactor.ExceptionSummary(exception)}");
			}
			catch
			{
				// 日志失败保持静默
			}
		}
	}
}
