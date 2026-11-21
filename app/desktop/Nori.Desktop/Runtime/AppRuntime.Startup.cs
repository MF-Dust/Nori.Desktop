using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Nori.Core.Configuration;
using Nori.Core.Proactive;
using Nori.Core.Voice;
using Nori.Desktop.Bridge;

namespace Nori.Desktop.Runtime;

public sealed partial class AppRuntime
{
	// ===================================================================
	// 启动装配
	// ===================================================================

	/// <summary>启动各子系统并接线事件</summary>
	public void Start()
	{
		Emotion.Initialize();
		if (!_petInteractionSubscribed && Services.PetRuntime is not null)
		{
			Services.PetRuntime.InteractionTriggered += OnPetInteractionTriggered;
			Services.PetRuntime.ModelLoadRequested += CancelPetInteractionRequest;
			Services.PetRuntime.ModelLoadRequested += CancelPetInteractionPresentation;
			Services.PetRuntime.ModelLoadRequested += OnPetModelStateChanged;
			Services.PetRuntime.ModelChanged += OnPetModelStateChanged;
			Services.PetRuntime.ModelLoadFailed += OnPetModelStateChanged;
			_petInteractionSubscribed = true;
		}
		Emotion.ExpressionRequested += expression =>
		{
			try
			{
				Services.PetRuntime?.PlayExpression(expression);
			}
			catch
			{
				/* 表情未匹配时忽略 */
			}
		};

		// 回放持久化的工具禁用清单
		if (Services.Config.Get("tools_disabled") is ConfigValue.Json {Value: JsonNode node})
		{
			try
			{
				List<string>? names = node.Deserialize<List<string>>(BridgeJson.Options);
				if (names is {Count: > 0}) Tools.RestoreDisabled(names);
			}
			catch
			{
				/* 清单损坏时忽略 */
			}
		}

		if (!_focusSubscribed)
		{
			Focus.Changed += OnFocusChanged;
			_focusSubscribed = true;
		}
		ApplyFocusQuiet();
		if (!Services.SafeMode)
		{
			string? recoveredNotice = Focus.ConsumeCompletionNotice(UiLanguage.IsEnglish(Services.Config));
			if (recoveredNotice is not null) Dispatcher.UIThread.Post(() => NotifyFocusComplete(recoveredNotice));
			Proactive.Message += message => Dispatcher.UIThread.Post(() => OnProactiveMessage(message));
			// 情绪一变就扇出到各条表达通道；协调器自己做节流，这里不判。
			Emotion.Changed += state => _ = Expression.ApplyAsync(state, Services.ShutdownToken);

			// 上一次若是崩溃或被强制结束，桌面会停在她改过的样子。原值存在配置库里，
			// 启动时先还原一次；用户仍开着这些通道的话，下一次情绪变化会重新改回去。
			RestoreDesktopState();
			Proactive.Start();

			// 插件贡献动作 → AI 工具 (plugin 分类): 活跃插件变化时防抖刷新
			if (Services.PluginRuntime is not null)
			{
				Services.PluginRuntime.ActivePluginsChanged += SchedulePluginToolsRefresh;
				SchedulePluginToolsRefresh();
			}

			// Knowledge 和 Reflection 都在后台启动；索引或整理失败不能阻塞聊天。
			_reflectionWorker.Start();
			_reflectionWorker.TryEnqueue();
			TrackBackground(InitializeKnowledgeAsync, "Memory.md index");
			TrackBackground(() => Memory.ReembedAllAsync(_lifetimeCts.Token, false), "memory embedding rebuild");
			TrackBackground(RunMemoryMaintenanceAsync, "memory lifecycle");
		}

		// 口型同步: 前端回传的播放音量采样直驱原生伴侣嘴型
		_playback.VolumeSampled += level =>
		{
			try
			{
				Services.PetRuntime?.SetMouthOpen((float)level, true);
			}
			catch
			{
				/* 伴侣未加载时忽略 */
			}
		};
		_playback.PlayingChanged += playing =>
		{
			try
			{
				Services.PetRuntime?.SetMouthOpen(0, playing);
			}
			catch
			{
				/* 伴侣未加载时忽略 */
			}
			try
			{
				// 普通朗读和试听也会进麦克风。实时监听在播放期间暂停，播完再恢复。
				RealtimeVoice.SetPlaybackHold(playing);
			}
			catch
			{
				/* 会话已释放时忽略 */
			}
		};
		Voice.SpeakingChanged += _ => InvalidateSnapshot();

		Voice.VolumeChanged += volume => _playback.SetDeviceVolume(volume);
		_playback.SetDeviceVolume(Voice.GetVolume());

		if (!Services.SafeMode)
		{
			TrackBackground(() => RefreshMcpToolsAsync(), "MCP tools refresh");
		}

		InvalidateSnapshot();
	}

	/// <summary>安全获取系统空闲秒数。平台没有接口或查询失败时返回 null，挂机关怀因此跳过。</summary>
	private static double? GetIdleSecondsSafe()
	{
		try
		{
			if (OperatingSystem.IsWindows()) return SystemIdleTime.GetIdleSeconds();
			if (OperatingSystem.IsLinux()) return Nori.Core.Platform.LinuxIdleTime.GetSeconds();
			return null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private void OnProactiveMessage(ProactiveMessage message)
	{
		bool autoTts = ParseBoolFlag(Services.Config.GetStringOr("tts_auto_play", "")) ?? false;
		Nori.Core.Focus.FocusPresence presence = Focus.Presence;
		if (!Nori.Core.Focus.FocusCompanionPolicy.ShouldDeliver(message.Care, presence)) return;
		bool allowSpeech = Nori.Core.Focus.FocusCompanionPolicy.StillAllowsSpeech(message.AllowSpeech, presence);
		bool allowAnimation = Nori.Core.Focus.FocusCompanionPolicy.StillAllowsAnimation(message.AllowAnimation, presence);
		if (Nori.Core.Focus.FocusCompanionPolicy.ShouldAnimateProactive(allowAnimation))
		{
			try
			{
				Services.PetRuntime?.PlayMotionByName(message.Motion);
				Services.PetRuntime?.PlayExpression(message.Expression);
			}
			catch
			{
				/* 伴侣未加载时忽略 */
			}
		}
		if (Nori.Core.Focus.FocusCompanionPolicy.ShouldShowProactiveText(allowSpeech))
		{
			try { Services.Windows.ShowPetSpeech(message.Text); }
			catch
			{
				/* 伴侣窗口还没建好时忽略 */
			}
		}
		if (Nori.Core.Focus.FocusCompanionPolicy.ShouldSpeakProactive(allowSpeech, autoTts))
			_ = SpeakSafelyAsync(message.Text);
	}

	private void OnFocusChanged()
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		ApplyFocusQuiet();
		InvalidateSnapshot();
		if (Services.SafeMode) return;
		string? notice = Focus.ConsumeCompletionNotice(UiLanguage.IsEnglish(Services.Config));
		if (notice is null) return;
		Dispatcher.UIThread.Post(() =>
		{
			if (Volatile.Read(ref _disposed) == 0) NotifyFocusComplete(notice);
		});
	}

	/// <summary>把当前专注阶段写到已有伴侣运行时。不改用户保存的待机和自主动作开关。</summary>
	internal void ApplyFocusQuiet()
	{
		try
		{
			if (Services.PetRuntime is { } pet) pet.FocusQuiet = Focus.Presence.Active;
		}
		catch
		{
			/* 伴侣未加载时忽略 */
		}
	}

	/// <summary>把专注状态推进快照，并按当前阶段压低或恢复氛围动作。</summary>
	internal void PublishFocus()
	{
		ApplyFocusQuiet();
		InvalidateSnapshot();
	}

	/// <summary>当前专注的只读描述，供快照和设置页倒计时共用。</summary>
	internal Nori.Core.Focus.FocusDisplay DescribeFocus() =>
		Nori.Core.Focus.FocusText.Describe(Focus.View(), UiLanguage.IsEnglish(Services.Config));

	private void NotifyFocusComplete(string text)
	{
		try { Services.Windows.ShowPetSpeech(text); }
		catch
		{
			/* 伴侣窗口还没建好时忽略 */
		}
		bool autoTts = ParseBoolFlag(Services.Config.GetStringOr("tts_auto_play", "")) ?? false;
		if (autoTts) _ = SpeakSafelyAsync(text);
	}
}
