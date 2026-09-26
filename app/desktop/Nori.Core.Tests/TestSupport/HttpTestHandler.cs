namespace Nori.Core.Tests.TestSupport;

/// <summary>可记录最近一次请求并委托响应的 HTTP 测试替身。</summary>
internal sealed class HttpTestHandler : HttpMessageHandler
{
	private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

	public HttpTestHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
		_responder = request => Task.FromResult(responder(request));

	public HttpTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
		_responder = request => responder(request, CancellationToken.None);

	public Uri? LastUri { get; private set; }

	public string? AuthorizationScheme { get; private set; }

	public string? AuthorizationParameter { get; private set; }

	public string? LastBody { get; private set; }

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		LastUri = request.RequestUri;
		AuthorizationScheme = request.Headers.Authorization?.Scheme;
		AuthorizationParameter = request.Headers.Authorization?.Parameter;
		LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
		return await _responder(request).ConfigureAwait(false);
	}
}
