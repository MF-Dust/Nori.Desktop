using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Nori.Core.Configuration;
using Nori.Core.Data;

namespace Nori.Core.Chat;

/// <summary>
/// 聊天消息中的图片部分。
/// 图片只在请求生命周期内由调用方和适配器持有, 不参与聊天历史持久化。
/// </summary>
public sealed record ChatImagePart
{
	/// <summary>单张图片大小上限 (4 MiB)</summary>
	public const int MaxBytes = 4 * 1024 * 1024;

	/// <summary>不可变图片字节</summary>
	public ImmutableArray<byte> Bytes { get; }

	/// <summary>规范化后的 MIME 类型</summary>
	public string MimeType { get; }

	/// <summary>
	/// 创建图片部分。构造时复制字节, 因此调用方之后修改原数组不会影响请求内容。
	/// </summary>
	public ChatImagePart(byte[] bytes, string mimeType)
	{
		if (bytes is null || bytes.Length == 0) throw new ChatException("图片不能为空");
		if (bytes.Length > MaxBytes) throw new ChatException("单张图片不能超过 4 MiB");

		Bytes = ImmutableArray.CreateRange(bytes);
		MimeType = NormalizeMimeType(mimeType);
	}

	private static string NormalizeMimeType(string mimeType)
	{
		if (string.IsNullOrWhiteSpace(mimeType)) throw new ChatException("图片 MIME 类型不能为空");
		return mimeType.Trim().ToLowerInvariant() switch
		{
			"image/png" => "image/png",
			"image/jpeg" => "image/jpeg",
			"image/webp" => "image/webp",
			_ => throw new ChatException("不支持的图片 MIME 类型"),
		};
	}
}

/// <summary>
/// 聊天消息 (输入)
/// 前端: {role: "user" | "assistant", content: "..."}
/// </summary>
public sealed record ChatMessageInput
{
	/// <summary>消息图片总大小上限 (8 MiB)</summary>
	public const int MaxTotalImageBytes = 8 * 1024 * 1024;

	private IReadOnlyList<ChatImagePart> _imageParts = ImmutableArray<ChatImagePart>.Empty;

	/// <summary>无参构造, 保持现有对象初始化调用兼容</summary>
	public ChatMessageInput()
	{
	}

	/// <summary>保留旧的角色与文本构造方式, 并可选附加图片部分</summary>
	[System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
	public ChatMessageInput(string role, string content, IReadOnlyList<ChatImagePart>? imageParts = null)
	{
		Role = role;
		Content = content;
		ImageParts = imageParts ?? ImmutableArray<ChatImagePart>.Empty;
	}

	/// <summary>角色: user / assistant</summary>
	public required string Role { get; init; }

	/// <summary>消息内容</summary>
	public required string Content { get; init; }

	/// <summary>可选的不可变图片部分</summary>
	public IReadOnlyList<ChatImagePart> ImageParts
	{
		get => _imageParts;
		init => _imageParts = NormalizeImageParts(value);
	}

	private static IReadOnlyList<ChatImagePart> NormalizeImageParts(IReadOnlyList<ChatImagePart>? imageParts)
	{
		if (imageParts is null || imageParts.Count == 0) return ImmutableArray<ChatImagePart>.Empty;

		ImmutableArray<ChatImagePart>.Builder normalized = ImmutableArray.CreateBuilder<ChatImagePart>(imageParts.Count);
		long totalBytes = 0;
		foreach (ChatImagePart? imagePart in imageParts)
		{
			if (imagePart is null) throw new ChatException("图片不能为空");
			totalBytes += imagePart.Bytes.Length;
			if (totalBytes > MaxTotalImageBytes) throw new ChatException("图片总大小不能超过 8 MiB");
			normalized.Add(imagePart);
		}
		return normalized.MoveToImmutable();
	}

	/// <summary>校验一次请求中的图片总大小, 防止多条消息绕过总上限。</summary>
	internal static void ValidateImageLimits(IReadOnlyList<ChatMessageInput> messages)
	{
		long totalBytes = 0;
		foreach (ChatMessageInput message in messages)
		{
			foreach (ChatImagePart imagePart in message.ImageParts)
			{
				totalBytes += imagePart.Bytes.Length;
				if (totalBytes > MaxTotalImageBytes) throw new ChatException("图片总大小不能超过 8 MiB");
			}
		}
	}
}

/// <summary>
/// 聊天消息 (存储 / 输出)
/// 前端: {id, role, content, createdAt}
/// </summary>
public sealed record ChatMessage
{
	/// <summary>自增 id (即时间顺序)</summary>
	public required long Id { get; init; }

	/// <summary>角色</summary>
	public required string Role { get; init; }

	/// <summary>内容</summary>
	public required string Content { get; init; }

	/// <summary>创建时间 (RFC3339)</summary>
	public required string CreatedAt { get; init; }
}

/// <summary>
/// 聊天服务
///
/// 对应 Rust 版 chat.rs. 系统提示词以嵌入资源形式编译进程序集,
/// 与原来的 include_str! 一样 —— 改了 nori-system-prompt.md 必须重新构建才生效.
/// </summary>
public sealed class ChatService(HttpClient httpClient, NoriDatabase database, ConfigStore config)
{
	/// <summary>配置键: LLM 协议类型</summary>
	public const string KeyLlmProvider = AiSettingsStore.KeyLlmProvider;

	/// <summary>
	/// 聊天请求超时 (秒): 防止接口挂起导致后台任务永久阻塞。
	/// public: App 组装 HttpClient 时超时要大于这个值, 否则 HttpClient 会先一步捨断长回复。
	/// </summary>
	public const int TimeoutSeconds = 120;

	/// <summary>嵌入资源名</summary>
	private const string PromptResource = "Nori.Core.Chat.nori-system-prompt.md";

	private static readonly Lazy<string> SystemPrompt = new(LoadSystemPrompt);

	private readonly HttpClient _httpClient = httpClient;
	private readonly NoriDatabase _database = database;
	private readonly ConfigStore _config = config;
	private readonly AiSettingsStore _aiSettings = new(config);

	/// <summary>
	/// 获取完整聊天历史 (按时间正序, 永不清除)
	/// </summary>
	public IReadOnlyList<ChatMessage> GetHistory() => _database.Locked(connection =>
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT id, role, content, created_at FROM chat_messages ORDER BY id ASC";
		using SqliteDataReader reader = command.ExecuteReader();
		List<ChatMessage> messages = [];
		while (reader.Read())
		{
			messages.Add(new ChatMessage
			{
				Id = reader.GetInt64(0),
				Role = reader.GetString(1),
				Content = reader.GetString(2),
				CreatedAt = reader.GetString(3),
			});
		}
		return (IReadOnlyList<ChatMessage>)messages;
	});

	/// <summary>
	/// 分页读取聊天历史 (返回按时间正序)
	///
	/// chat_messages 随使用无限增长, 界面加载必须带 limit, 否则每次打开都全量拉取.
	/// beforeId <= 0 表示从最新一条开始; limit <= 0 视为不限制 (兼容旧的全量读取).
	/// </summary>
	public IReadOnlyList<ChatMessage> GetHistory(int limit, long beforeId, bool excludeToolFeedback = false) => _database.Locked(connection =>
	{
		string sql = "SELECT id, role, content, created_at FROM chat_messages WHERE 1 = 1";
		if (beforeId > 0) sql += " AND id < $before";
		// 先筛选再分页，避免一页旧工具反馈使界面误判已经没有更早消息。
		if (excludeToolFeedback) sql += " AND NOT (role = 'user' AND content LIKE $feedbackPrefix)";
		// 倒序取最新的 limit 条, 读完后反转回时间正序
		sql += " ORDER BY id DESC";
		if (limit > 0) sql += " LIMIT $limit";

		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;
		if (beforeId > 0) command.Parameters.AddWithValue("$before", beforeId);
		if (excludeToolFeedback) command.Parameters.AddWithValue("$feedbackPrefix", "【系统工具执行反馈 -%");
		if (limit > 0) command.Parameters.AddWithValue("$limit", limit);
		using SqliteDataReader reader = command.ExecuteReader();
		List<ChatMessage> messages = [];
		while (reader.Read())
		{
			messages.Add(new ChatMessage
			{
				Id = reader.GetInt64(0),
				Role = reader.GetString(1),
				Content = reader.GetString(2),
				CreatedAt = reader.GetString(3),
			});
		}
		messages.Reverse();
		return (IReadOnlyList<ChatMessage>)messages;
	});

	/// <summary>
	/// 发起一次对话
	///
	/// 返回剥离动作标记后的回复文本; 动作名通过 onMotion 回调交给调用方广播
	/// </summary>
	public async Task<string> CompleteAsync(
		string? providerStr,
		string baseUrl,
		string apiKey,
		string model,
		IReadOnlyList<ChatMessageInput> messages,
		Action<string> onMotion,
		bool persist = true,
		CancellationToken cancellationToken = default)
	{
		baseUrl = baseUrl.TrimEnd('/');
		if (baseUrl.Length == 0) throw new ChatException("Base URL 不能为空");
		if (apiKey.Length == 0) throw new ChatException("API Key 不能为空");
		if (model.Length == 0) throw new ChatException("模型不能为空");
		if (messages.Count == 0) throw new ChatException("消息不能为空");
		ChatMessageInput.ValidateImageLimits(messages);

		// 若未指定 providerStr，优先读配置
		if (string.IsNullOrWhiteSpace(providerStr))
		{
			providerStr = _aiSettings.Read().Chat.Provider.AsString();
		}

		LlmProvider provider = LlmProviderExtensions.ParseProvider(providerStr);
		ILlmAdapter adapter = LlmClient.CreateAdapter(provider, _httpClient);

		// 系统提示词 = 人格 + 当前模型动作列表附录
		string modelId = _config.GetStringOr(ConfigStore.KeySelectedModel, "");
		string systemContent = SystemPrompt.Value + MotionMarkers.BuildHint(_config, modelId);

		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

		string raw = await adapter.CompleteAsync(baseUrl, apiKey, model, systemContent, messages, timeout.Token);

		// 解析动作标记: 剥离标记并广播给伴侣窗口播放
		(string content, IReadOnlyList<string> motions) = MotionMarkers.Extract(raw);
		foreach (string motion in motions) onMotion(motion);

		// 写入历史: 仅保存最后一条输入与回复, 避免重复落库
		if (persist)
		{
			if (messages.Count > 0)
			{
				SaveMessage(messages[^1].Role, messages[^1].Content);
			}
			SaveMessage("assistant", content);
		}
		return content;
	}

	/// <summary>
	/// 发起一次流式对话
	///
	/// 逐 chunk 触发 onChunk 回调, 并在结束时返回剥离动作标记后的完整回复文本
	/// </summary>
	public async Task<string> StreamAsync(
		string? providerStr,
		string baseUrl,
		string apiKey,
		string model,
		IReadOnlyList<ChatMessageInput> messages,
		Action<string> onChunk,
		Action<string> onMotion,
		Action<LlmUsageInfo>? onUsage = null,
		bool persist = true,
		CancellationToken cancellationToken = default)
	{
		baseUrl = baseUrl.TrimEnd('/');
		if (baseUrl.Length == 0) throw new ChatException("Base URL 不能为空");
		if (apiKey.Length == 0) throw new ChatException("API Key 不能为空");
		if (model.Length == 0) throw new ChatException("模型不能为空");
		if (messages.Count == 0) throw new ChatException("消息不能为空");
		ChatMessageInput.ValidateImageLimits(messages);

		if (string.IsNullOrWhiteSpace(providerStr))
		{
			providerStr = _aiSettings.Read().Chat.Provider.AsString();
		}

		LlmProvider provider = LlmProviderExtensions.ParseProvider(providerStr);
		ILlmAdapter adapter = LlmClient.CreateAdapter(provider, _httpClient);

		string modelId = _config.GetStringOr(ConfigStore.KeySelectedModel, "");
		string systemContent = SystemPrompt.Value + MotionMarkers.BuildHint(_config, modelId);

		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

		string raw = await adapter.StreamAsync(baseUrl, apiKey, model, systemContent, messages, onChunk, onUsage, timeout.Token);

		(string content, IReadOnlyList<string> motions) = MotionMarkers.Extract(raw);
		foreach (string motion in motions) onMotion(motion);

		if (persist)
		{
			if (messages.Count > 0)
			{
				SaveMessage(messages[^1].Role, messages[^1].Content);
			}
			SaveMessage("assistant", content);
		}
		return content;
	}

	/// <summary>
	/// 兼容老接口 (从配置或默认协议发起对话)
	/// </summary>
	public Task<string> CompleteAsync(
		string baseUrl,
		string apiKey,
		string model,
		IReadOnlyList<ChatMessageInput> messages,
		Action<string> onMotion,
		CancellationToken cancellationToken = default)
	{
		return CompleteAsync(null, baseUrl, apiKey, model, messages, onMotion, cancellationToken: cancellationToken);
	}

	/// <summary>
	/// 保存一条聊天消息并返回持久化结果
	/// </summary>
	public ChatMessage SaveMessage(string role, string content) => _database.Locked(connection =>
	{
		using SqliteCommand command = connection.CreateCommand();
		string createdAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
		command.CommandText = "INSERT INTO chat_messages (role, content, created_at) VALUES ($role, $content, $createdAt); SELECT last_insert_rowid();";
		command.Parameters.AddWithValue("$role", role);
		command.Parameters.AddWithValue("$content", content);
		command.Parameters.AddWithValue("$createdAt", createdAt);
		long id = (long)(command.ExecuteScalar() ?? throw new InvalidOperationException("保存聊天消息失败"));
		return new ChatMessage {Id = id, Role = role, Content = content, CreatedAt = createdAt};
	});

	/// <summary>
	/// 清空全部聊天历史
	/// </summary>
	public void ClearHistory() => _database.Locked(connection =>
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "DELETE FROM chat_messages;";
		command.ExecuteNonQuery();
	});

	/// <summary>
	/// 从嵌入资源读取系统提示词
	/// </summary>
	private static string LoadSystemPrompt()
	{
		using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PromptResource)
			?? throw new InvalidOperationException($"找不到嵌入资源: {PromptResource}");
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}
}

/// <summary>
/// 聊天相关错误, 消息直接展示给用户
/// </summary>
public sealed class ChatException(string message, Exception? inner = null) : Exception(message, inner);
