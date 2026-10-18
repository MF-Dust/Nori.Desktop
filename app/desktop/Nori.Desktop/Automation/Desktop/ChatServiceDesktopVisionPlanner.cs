using Nori.Core.Chat;
using Nori.Core.Configuration;

namespace Nori.Desktop.Automation.Desktop;

/// <summary>
/// 使用当前聊天 Provider 的统一多模态契约执行视觉规划。
/// 不创建第二套 Provider adapter，也不把规划请求写入聊天历史。
/// </summary>
public sealed class ChatServiceDesktopVisionPlanner : IDesktopVisionPlanner
{
	private readonly ChatService _chat;
	private readonly AiSettingsStore _settings;

	/// <summary>创建当前聊天 Provider 规划器。</summary>
	public ChatServiceDesktopVisionPlanner(ChatService chat, AiSettingsStore settings)
	{
		ArgumentNullException.ThrowIfNull(chat);
		ArgumentNullException.ThrowIfNull(settings);
		_chat = chat;
		_settings = settings;
	}

	/// <inheritdoc />
	public Task<string> PlanAsync(IReadOnlyList<ChatMessageInput> messages, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(messages);
		AiChatSettings chat = _settings.Read().Chat;
		if (!chat.IsConfigured)
		{
			throw new InvalidOperationException("当前聊天 Provider 未配置，无法执行桌面视觉自动化");
		}

		return _chat.CompleteAsync(
			chat.ProviderId,
			chat.BaseUrl,
			chat.ApiKey,
			chat.Model,
			messages,
			static _ => { },
			persist: false,
			cancellationToken: cancellationToken);
	}
}
