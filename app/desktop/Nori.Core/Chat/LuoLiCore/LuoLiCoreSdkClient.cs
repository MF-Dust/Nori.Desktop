using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Nori.Core.Chat.LuoLiCore;

/// <summary>LuoLiCore 接入所需的连接参数。</summary>
public sealed record LuoLiCoreSdkOptions
{
	/// <summary>服务根地址，例如 <c>http://127.0.0.1:3000</c>。路径 <c>/sdk/v1</c> 由本类补。</summary>
	public required string BaseUrl { get; init; }

	/// <summary>`sk-` 开头的来源密钥。与 WebUI 的 cookie 通道互斥，一把密钥对应一个来源。</summary>
	public required string ApiKey { get; init; }

	/// <summary>
	/// 发起人标识。落库前服务端会加 <c>&lt;sourceId&gt;:</c> 前缀，所以这里只要在本来源内唯一。
	///
	/// 桌面端是单用户的，固定一个常量即可；换成随机值会让记忆按人分叉。
	/// </summary>
	public string EndUserId { get; init; } = "desktop";

	/// <summary>模型别名。留空则用服务端会话上的默认值。</summary>
	public string? ModelAlias { get; init; }
}

/// <summary>
/// LuoLiCore <c>/sdk/v1</c> 的最小客户端。
///
/// 只做本集成用得上的三件事：建会话、发消息、重置会话上下文。会话的列举、改名与彻底
/// 删除留给设置页那条路，不在对话链路上。
///
/// **这不是 IChatClient。** 对端一次「发消息」等于它自己跑完一整轮（含它那侧的工具循环），
/// 语义是「把这一轮交出去」，不是「给我一次补全」。硬塞进 IChatClient 会让上层以为自己
/// 还持有工具循环的控制权 —— 那正是两套 agent 打架的起点。上层怎么用见调用方。
/// </summary>
public sealed class LuoLiCoreSdkClient(HttpClient httpClient, LuoLiCoreSdkOptions options)
{
	private readonly HttpClient _httpClient = httpClient;
	private readonly LuoLiCoreSdkOptions _options = options;

	private string Root => _options.BaseUrl.TrimEnd('/') + "/sdk/v1";

	private HttpRequestMessage NewRequest(HttpMethod method, string path)
	{
		HttpRequestMessage request = new(method, Root + path);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
		return request;
	}

	/// <summary>建一个会话。<c>outputFormat</c> 建后不可改，所以在这里定死。</summary>
	public async Task<string> CreateSessionAsync(string? label = null, CancellationToken cancellationToken = default)
	{
		using HttpRequestMessage request = NewRequest(HttpMethod.Post, "/sessions");
		request.Content = JsonContent.Create(
			// plain 而不是 markdown：这条流最终要念出来并驱动口型，
			// 星号和井号会被 TTS 读成字符。
			new SdkCreateSessionInput(_options.ModelAlias, "plain", label),
			options: LuoLiCoreSdkProtocol.Json);

		using HttpResponseMessage response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);

		SdkSession? session = await response.Content.ReadFromJsonAsync<SdkSession>(
			LuoLiCoreSdkProtocol.Json, cancellationToken);
		if (session is null || string.IsNullOrWhiteSpace(session.Id)) throw new ChatException("SDK 建会话成功但没有返回 id");
		return session.Id;
	}

	/// <summary>
	/// 重置会话上下文。
	///
	/// 对端是 git 式的上下文引擎，reset 落一条新提交把工作上下文清空，历史提交仍在 —— 所以
	/// 这是「她不再记得之前聊过什么」，不是「删掉这个会话」。会话 id 与它挂着的长期记忆都保留，
	/// 下一轮接着用同一个会话。
	/// </summary>
	/// <returns>那条重置提交的 id。</returns>
	public async Task<string> ResetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
	{
		using HttpRequestMessage request = NewRequest(HttpMethod.Post, $"/sessions/{Uri.EscapeDataString(sessionId)}/reset");
		using HttpResponseMessage response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
		await ThrowIfFailedAsync(response, cancellationToken);

		SdkResetResult? result = await response.Content.ReadFromJsonAsync<SdkResetResult>(
			LuoLiCoreSdkProtocol.Json, cancellationToken);
		return result?.CommitId ?? string.Empty;
	}

	/// <summary>
	/// 发一条消息并流式读回。
	///
	/// 取消时主动断连 —— 对端把客户端断连映射到轮次取消，所以这里不需要另外调取消接口。
	/// </summary>
	public async IAsyncEnumerable<LuoLiCoreStreamEvent> StreamAsync(
		string sessionId,
		string text,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		using HttpRequestMessage request = NewRequest(HttpMethod.Post, $"/sessions/{Uri.EscapeDataString(sessionId)}/messages/stream");
		request.Content = JsonContent.Create(
			new SdkSendMessageInput(_options.EndUserId, text, _options.ModelAlias),
			options: LuoLiCoreSdkProtocol.Json);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

		// 流在轮次执行期间没有保活帧，用默认超时会把正常的长轮次当成断流掐掉。
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(LuoLiCoreSdkProtocol.StreamReadTimeout);

		using HttpResponseMessage response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

		// 配额拒绝发生在响应头写出之前，所以这一支仍是普通 HTTP 错误。
		// 队列满则不然：它已经是 200 + SSE，要等 error 事件才看得到。
		await ThrowIfFailedAsync(response, timeout.Token);

		await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
		using StreamReader reader = new(stream, Encoding.UTF8);

		await foreach (LuoLiCoreStreamEvent item in LuoLiCoreSseReader.ReadAsync(reader, timeout.Token))
		{
			yield return item;
		}
	}

	private async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		HttpCompletionOption completion,
		CancellationToken cancellationToken)
	{
		try
		{
			return await _httpClient.SendAsync(request, completion, cancellationToken);
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
		{
			throw new ChatException($"连不上 LuoLiCore: {exception.Message}", exception);
		}
	}

	/// <summary>
	/// 失败响应统一走错误信封。
	///
	/// HTTP 错误体与 SSE <c>error</c> 事件同形，所以两条路径解出来是同一个类型；解不出来时
	/// 退回状态码，不把一段 HTML 错误页当成消息抛给上层。
	/// </summary>
	private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.IsSuccessStatusCode) return;

		string body = string.Empty;
		try { body = await response.Content.ReadAsStringAsync(cancellationToken); }
		catch (Exception exception) when (exception is not OperationCanceledException) { /* 读不到就只报状态码 */ }

		SdkError? error = null;
		if (body.Length > 0)
		{
			try { error = JsonSerializer.Deserialize<SdkError>(body, LuoLiCoreSdkProtocol.Json); }
			catch (JsonException) { /* 不是信封 */ }
		}

		string detail = error is not null
			? $"{error.Code}{(string.IsNullOrWhiteSpace(error.Message) ? "" : ": " + error.Message)}"
			: body.Length > 0 ? body[..Math.Min(body.Length, 200)] : "无响应体";
		throw new ChatException($"LuoLiCore 返回 HTTP {(int)response.StatusCode}, {detail}");
	}
}
