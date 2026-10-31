using System.Net.Http;
using System.Text.Json;
using Nori.Core.Live2D;

namespace Nori.Core.Resources;

/// <summary>远程模型下载进度. <see cref="CompletedFiles"/> 不含 model3.json 本身.</summary>
public readonly record struct RemoteModelProgress(string ModelId, int CompletedFiles, int TotalFiles, long Bytes);

/// <summary>
/// 从 Nori 静态资源站 (默认 <see cref="DefaultOrigin"/>) 获取内置 Live2D 模型.
///
/// 只下载 <see cref="SupportedModelIds"/> 中的固定模型, 文件清单完全由远端 model3.json 的
/// FileReferences 决定, 每个引用先经 <see cref="Model3ReferenceValidator"/> 的路径校验再请求.
/// 文件先落到资源根目录下的同卷暂存目录, 随后交给 <see cref="ResourceManager.Import(ResourceType, string, CancellationToken)"/>
/// 完成完整校验与原子交换; 任一失败都不改动已安装模型.
/// </summary>
public sealed class RemoteModelInstaller
{
	/// <summary>Nori 静态资源站.</summary>
	public const string DefaultOrigin = "https://static.nori.mom";

	/// <summary>单个文件上限.</summary>
	internal const long MaxFileBytes = 64L * 1024 * 1024;

	/// <summary>单个模型总大小上限.</summary>
	internal const long MaxTotalBytes = 256L * 1024 * 1024;

	/// <summary>model3.json 上限.</summary>
	internal const int MaxManifestBytes = 1024 * 1024;

	/// <summary>单个模型引用文件数上限.</summary>
	internal const int MaxFiles = 512;

	private const int Parallelism = 4;

	private static readonly IReadOnlyDictionary<string, (string Directory, string Model3)> Layout =
		new Dictionary<string, (string, string)>(StringComparer.Ordinal)
		{
			[SupportedModelIds.ArgNori] = ("ARGNori_web", "ARGNori.model3.json"),
			[SupportedModelIds.Nori] = ("Nori_web", "Nori.model3.json"),
		};

	private readonly HttpClient _http;
	private readonly ResourceManager _resources;
	private readonly Uri _origin;
	private readonly Func<bool> _networkEnabled;
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <param name="http">公网 HTTP 客户端.</param>
	/// <param name="resources">本地资源管理.</param>
	/// <param name="networkEnabled">是否允许联网; 安全模式下返回 false.</param>
	/// <param name="origin">静态资源站 origin, 只接受无路径、查询与凭据的 HTTPS origin.</param>
	public RemoteModelInstaller(HttpClient http, ResourceManager resources, Func<bool>? networkEnabled = null, string origin = DefaultOrigin)
	{
		_http = http ?? throw new ArgumentNullException(nameof(http));
		_resources = resources ?? throw new ArgumentNullException(nameof(resources));
		_networkEnabled = networkEnabled ?? (() => true);
		_origin = ParseOrigin(origin);
	}

	/// <summary>静态资源站 origin.</summary>
	public string Origin => _origin.GetLeftPart(UriPartial.Authority);

	/// <summary>
	/// 下载并安装指定模型, 已安装时覆盖为远端版本. 同一时间只运行一个安装任务.
	/// </summary>
	/// <returns>安装后的模型 ID.</returns>
	public async Task<string> InstallAsync(
		string modelId,
		IProgress<RemoteModelProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		string id = SupportedModelIds.Normalize(modelId)
			?? throw new ResourceException($"不支持的 Live2D 模型 ID: {modelId}");
		if (!_networkEnabled()) throw new ResourceException("安全模式已禁用联网, 无法在线获取模型");

		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		string? root = null;
		try
		{
			root = await Task.Run(_resources.CreateDownloadRoot, cancellationToken).ConfigureAwait(false);
			string modelDir = Path.Combine(root, id);
			Directory.CreateDirectory(modelDir);
			await DownloadAsync(id, modelDir, progress, cancellationToken).ConfigureAwait(false);
			IReadOnlyList<string> imported = await Task.Run(
				() => _resources.Import(ResourceType.Live2D, modelDir, cancellationToken),
				cancellationToken).ConfigureAwait(false);
			if (!imported.Contains(id, StringComparer.Ordinal))
				throw new ResourceException($"远端模型与预期不符: {id}");
			return id;
		}
		catch (HttpRequestException exception)
		{
			throw new ResourceException($"下载模型失败: {exception.Message}", exception);
		}
		catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			throw new ResourceException("下载模型超时, 请检查网络后重试", exception);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new ResourceException($"保存模型失败: {exception.Message}", exception);
		}
		finally
		{
			if (root is not null) ResourceManager.DeleteScratchRoot(root);
			_gate.Release();
		}
	}

	private async Task DownloadAsync(
		string id,
		string modelDir,
		IProgress<RemoteModelProgress>? progress,
		CancellationToken cancellationToken)
	{
		(string remoteDir, string model3Name) = Layout[id];
		string model3Path = Path.Combine(modelDir, model3Name);
		byte[] manifest = await GetBytesAsync(FileUri(remoteDir, model3Name), MaxManifestBytes, cancellationToken).ConfigureAwait(false);
		await File.WriteAllBytesAsync(model3Path, manifest, cancellationToken).ConfigureAwait(false);

		IReadOnlyList<string> references = ReadReferences(manifest);
		if (references.Count > MaxFiles) throw new ResourceException($"远端模型文件过多: {references.Count}");

		// 先全部过一遍路径校验, 再发请求: 恶意清单不应触发任何额外下载.
		List<(string Reference, string Target)> files = [.. references.Select(reference =>
			(reference, Model3ReferenceValidator.ResolveReferencePath(model3Path, reference, requireFile: false)))];

		long total = manifest.Length;
		int completed = 0;
		progress?.Report(new RemoteModelProgress(id, 0, files.Count, total));
		await Parallel.ForEachAsync(
			files,
			new ParallelOptions {MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken},
			async (file, token) =>
			{
				long written = await DownloadFileAsync(FileUri(remoteDir, file.Reference), file.Target, token).ConfigureAwait(false);
				long bytes = Interlocked.Add(ref total, written);
				if (bytes > MaxTotalBytes) throw new ResourceException("远端模型总大小超出限制");
				progress?.Report(new RemoteModelProgress(id, Interlocked.Increment(ref completed), files.Count, bytes));
			}).ConfigureAwait(false);
	}

	/// <summary>收集 model3.json 中需要下载的全部引用, 按出现顺序去重. Sound 不由 SDK 读取, 不下载.</summary>
	internal static IReadOnlyList<string> ReadReferences(byte[] manifest)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(manifest);
		}
		catch (JsonException exception)
		{
			throw new ResourceException("远端 model3.json 格式无效", exception);
		}

		using (document)
		{
			if (document.RootElement.ValueKind != JsonValueKind.Object
				|| !document.RootElement.TryGetProperty("FileReferences", out JsonElement refs)
				|| refs.ValueKind != JsonValueKind.Object)
				throw new ResourceException("远端 model3.json 缺少有效的 FileReferences 对象");

			List<string> result = [];
			HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
			void Add(JsonElement value)
			{
				if (value.ValueKind == JsonValueKind.String && value.GetString() is {Length: > 0} path
					&& seen.Add(path.Replace('\\', '/')))
					result.Add(path.Replace('\\', '/'));
			}

			foreach (string name in new[] {"Moc", "Physics", "Pose", "DisplayInfo", "UserData"})
				if (refs.TryGetProperty(name, out JsonElement value)) Add(value);
			if (refs.TryGetProperty("Textures", out JsonElement textures) && textures.ValueKind == JsonValueKind.Array)
				foreach (JsonElement texture in textures.EnumerateArray()) Add(texture);
			if (refs.TryGetProperty("Expressions", out JsonElement expressions) && expressions.ValueKind == JsonValueKind.Array)
				foreach (JsonElement expression in expressions.EnumerateArray())
					if (expression.ValueKind == JsonValueKind.Object && expression.TryGetProperty("File", out JsonElement file)) Add(file);
			if (refs.TryGetProperty("Motions", out JsonElement motions) && motions.ValueKind == JsonValueKind.Object)
				foreach (JsonProperty group in motions.EnumerateObject())
					if (group.Value.ValueKind == JsonValueKind.Array)
						foreach (JsonElement motion in group.Value.EnumerateArray())
							if (motion.ValueKind == JsonValueKind.Object && motion.TryGetProperty("File", out JsonElement file)) Add(file);
			return result;
		}
	}

	private Uri FileUri(string remoteDir, string relative) => new(_origin,
		"/" + remoteDir + "/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString)));

	private async Task<byte[]> GetBytesAsync(Uri uri, int limit, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendAsync(uri, limit, cancellationToken).ConfigureAwait(false);
		await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using MemoryStream buffer = new();
		await CopyLimitedAsync(stream, buffer, limit, uri, cancellationToken).ConfigureAwait(false);
		return buffer.ToArray();
	}

	private async Task<long> DownloadFileAsync(Uri uri, string target, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendAsync(uri, MaxFileBytes, cancellationToken).ConfigureAwait(false);
		Directory.CreateDirectory(Path.GetDirectoryName(target)!);
		await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		await using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
		return await CopyLimitedAsync(input, output, MaxFileBytes, uri, cancellationToken).ConfigureAwait(false);
	}

	private async Task<HttpResponseMessage> SendAsync(Uri uri, long limit, CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new(HttpMethod.Get, uri);
		request.Headers.UserAgent.ParseAdd(Chat.ChatClientFactory.UserAgent);
		HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		try
		{
			if (!response.IsSuccessStatusCode)
				throw new ResourceException($"下载模型文件失败 ({(int)response.StatusCode}): {uri.AbsolutePath}");
			if (response.Content.Headers.ContentLength > limit)
				throw new ResourceException($"模型文件超出大小限制: {uri.AbsolutePath}");
			return response;
		}
		catch
		{
			response.Dispose();
			throw;
		}
	}

	private static async Task<long> CopyLimitedAsync(Stream input, Stream output, long limit, Uri uri, CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[64 * 1024];
		long total = 0;
		while (true)
		{
			int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			if (read == 0) return total;
			total += read;
			if (total > limit) throw new ResourceException($"模型文件超出大小限制: {uri.AbsolutePath}");
			await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
		}
	}

	private static Uri ParseOrigin(string origin)
	{
		if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
			|| uri.Scheme != Uri.UriSchemeHttps
			|| uri.UserInfo.Length > 0
			|| uri.AbsolutePath != "/"
			|| uri.Query.Length > 0
			|| uri.Fragment.Length > 0)
			throw new ArgumentException("静态资源站必须是不含路径、查询与凭据的 HTTPS origin", nameof(origin));
		return uri;
	}
}
