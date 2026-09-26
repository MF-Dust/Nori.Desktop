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

		if (!Services.SafeMode)
		{
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
		};
		Voice.SpeakingChanged += _ => InvalidateSnapshot();

		Voice.VolumeChanged += volume => _playback.SetDeviceVolume(volume);
		_playback.SetDeviceVolume(Voice.GetVolume());

		if (!Services.SafeMode)
		{
			DetectLegacyVoiceConfig();
			TrackBackground(() => RefreshMcpToolsAsync(), "MCP tools refresh");
		}

		InvalidateSnapshot();
	}

	/// <summary>安全获取系统空闲秒数 (非 Windows 返回 null)</summary>
	private static double? GetIdleSecondsSafe()
	{
		if (!OperatingSystem.IsWindows()) return null;
		try
		{
			return SystemIdleTime.GetIdleSeconds();
		}
		catch
		{
			return null;
		}
	}

	private void DetectLegacyVoiceConfig()
	{
		if (!Voice.HasRetiredVoiceConfig()) return;
		string flagged = Services.Config.GetStringOr("voice_notice_pending", "");
		if (flagged.Length > 0) return; // 已提示过或已处理
		Services.Config.Set("voice_notice_pending", new ConfigValue.Text("1"));
	}

	private bool VoiceRetired() => VoiceService.RetiredProviders.Contains(Services.Config.GetStringOr("stt_provider", ""));

	private void OnProactiveMessage(ProactiveMessage message)
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
		bool autoTts = ParseBoolFlag(Services.Config.GetStringOr("tts_auto_play", "")) ?? false;
		if (autoTts)
		{
			_ = SpeakSafelyAsync(message.Text);
		}
	}
}
