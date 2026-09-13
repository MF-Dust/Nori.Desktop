using Nori.Core.Chat;
using Nori.Core.Configuration;
using Nori.Core.Vision;

namespace Nori.Desktop.Vision;

/// <summary>
/// 用当前聊天 Provider 的多模态能力分析截图。
///
/// 与 <c>ChatServiceDesktopVisionPlanner</c> 同一条路：不另起一套 Provider adapter，也不把
/// 请求写进聊天历史（<c>persist: false</c>）。两者的区别在契约 —— 那个要的是严格 JSON 的
/// 动作计划，这个要的是给用户看的自然语言回答。
/// </summary>
public sealed class ChatVisionAnalyzer : IVisionAnalyzer
{
	private readonly ChatService _chat;
	private readonly AiSettingsStore _settings;

	/// <summary>创建截图分析器。</summary>
	public ChatVisionAnalyzer(ChatService chat, AiSettingsStore settings)
	{
		ArgumentNullException.ThrowIfNull(chat);
		ArgumentNullException.ThrowIfNull(settings);
		_chat = chat;
		_settings = settings;
	}

	/// <inheritdoc />
	public bool IsConfigured => _settings.Read().Chat.IsConfigured;

	/// <inheritdoc />
	public Task<string> AnalyzeAsync(string question, CapturedScreen screen, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(question);
		ArgumentNullException.ThrowIfNull(screen);

		AiChatSettings chat = _settings.Read().Chat;
		if (!chat.IsConfigured) throw new InvalidOperationException("当前聊天 Provider 未配置，无法查看屏幕");

		// 单轮、不带历史：这次调用只为回答关于这张图的问题，带上对话历史既涨成本也会让模型
		// 跑题去接续聊天。模型不支持图片时会在此处报错，错误原文交回给调用方。
		ChatMessageInput message = new(
			"user",
			question,
			[new ChatImagePart(screen.Bytes, screen.MimeType)]);

		return _chat.CompleteAsync(
			chat.Provider.AsString(),
			chat.BaseUrl,
			chat.ApiKey,
			chat.Model,
			[message],
			static _ => { },
			persist: false,
			cancellationToken: cancellationToken);
	}
}
