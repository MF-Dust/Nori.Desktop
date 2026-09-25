using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Nori.Core.Logging;
namespace Nori.PluginRuntime;

/// <summary>
/// 动态插件窗口管理器
///
/// 独立于 WindowDefinition.All 与 WindowManager 的四个固定主窗口,
/// 专职负责插件 Web 视图窗口的生命周期调度、标签命名空间维护与安全标识校验.
/// </summary>
internal sealed partial class PluginWindowHost : IAsyncDisposable
{
	private readonly ConcurrentDictionary<string, PluginWebViewWindow> _windows = new(StringComparer.Ordinal);
	private readonly FileLogger? _logger;
	private readonly string _webViewDataRoot;
	private readonly Func<string, string, Uri>? _assetUriFactory;
	private int _disposed;

	[GeneratedRegex(@"^[a-zA-Z0-9_\-\.]{1,64}$", RegexOptions.Compiled)]
	private static partial Regex SafeIdPattern();

	public PluginWindowHost(
		FileLogger? logger = null,
		string? webViewDataRoot = null,
		Func<string, string, Uri>? assetUriFactory = null)
	{
		_logger = logger;
		_webViewDataRoot = Path.GetFullPath(webViewDataRoot ?? Path.Combine(AppContext.BaseDirectory, "webview_plugins"));
		_assetUriFactory = assetUriFactory;
		Directory.CreateDirectory(_webViewDataRoot);
	}

	/// <summary>
	/// 校验插件 ID 或窗口 ID 是否符合安全命名规范 (防路径遍历与非法字符注入)
	/// </summary>
	public static bool IsValidPluginId(string? id) => PluginManifestReader.IsValidPluginId(id);

	public static bool IsValidId(string? id)
	{
		if (string.IsNullOrWhiteSpace(id)) return false;
		if (id.Length > 64) return false;
		if (id.Contains('/') || id.Contains('\\') || id.Contains(':') || id.Contains("..")) return false;
		return SafeIdPattern().IsMatch(id);
	}

	/// <summary>
	/// 断言 ID 有效性，不合法时抛出领域异常
	/// </summary>
	public static void ValidateId(string id, string paramName)
	{
		if (!IsValidId(id))
			throw new ArgumentException($"标识符 '{id}' 不合法: 仅允许 1-64 位字母、数字、下划线、短横线与点，且不得包含路径或冒号字符。", paramName);
	}

	/// <summary>校验插件 ID。manifest 负责更严格的规范 ID 校验，窗口标签允许最多 128 位。</summary>
	public static void ValidatePluginId(string id, string paramName)
	{
		if (!IsValidPluginId(id)) throw new ArgumentException($"插件标识符 '{id}' 不合法。", paramName);
	}

	/// <summary>
	/// 生成标准的插件窗口全局标签
	/// </summary>
	public static string BuildLabel(string pluginId, string windowId)
	{
		ValidatePluginId(pluginId, nameof(pluginId));
		ValidateId(windowId, nameof(windowId));
		return $"plugin:{pluginId}:{windowId}";
	}

	/// <summary>
	/// 从窗口全局标签解析所属插件 ID 与窗口 ID
	/// </summary>
	public static bool TryParseLabel(
		string? label,
		[NotNullWhen(true)] out string? pluginId,
		[NotNullWhen(true)] out string? windowId)
	{
		pluginId = null;
		windowId = null;

		if (string.IsNullOrWhiteSpace(label)) return false;
		if (!label.StartsWith("plugin:", StringComparison.Ordinal)) return false;

		string[] parts = label.Split(':');
		if (parts.Length != 3) return false;

		if (!IsValidPluginId(parts[1]) || !IsValidId(parts[2])) return false;
		pluginId = parts[1];
		windowId = parts[2];
		return true;
	}

	/// <summary>
	/// 创建并登记一个插件 Web 视图窗口 (在 UI 线程执行实例化)
	/// </summary>
	public async Task<IPluginWebViewWindow> CreateWindowAsync(
		PluginDescriptorSummary descriptor,
		PluginWebViewOptions options,
		CancellationToken revocationToken = default,
		CancellationToken cancellationToken = default)
	{
		if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PluginWindowHost));
		ArgumentNullException.ThrowIfNull(descriptor);
		ArgumentNullException.ThrowIfNull(options);

		ValidatePluginId(descriptor.Id, nameof(descriptor.Id));
		PluginWindowOptionsValidator.Validate(options);
		Uri entryPoint = ResolveEntryPoint(descriptor.Id, options.EntryPoint);
		Uri webRoot = new(entryPoint, "./");

		string label = BuildLabel(descriptor.Id, options.Id);

		if (_windows.TryGetValue(label, out PluginWebViewWindow? existing))
		{
			_logger?.Write(LogSource.Backend, "info", $"插件窗口 '{label}' 已存在，重新激活");
			await existing.ShowAsync(cancellationToken).ConfigureAwait(false);
			return existing;
		}

		cancellationToken.ThrowIfCancellationRequested();

		PluginWebViewWindow window = await Dispatcher.UIThread.InvokeAsync(() =>
		{
			return new PluginWebViewWindow(
				descriptor,
				options,
				revocationToken: revocationToken,
				webViewDataRoot: _webViewDataRoot,
				logger: _logger,
				entryPoint: entryPoint,
				navigationPolicy: request => IsAllowedNavigation(request, webRoot));
		});

		window.WindowClosed += OnWindowClosed;

		_windows[label] = window;
		_logger?.Write(LogSource.Backend, "info", $"已创建并注册插件窗口: {label}");

		return window;
	}

	private Uri ResolveEntryPoint(string pluginId, string entryPoint)
	{
		if (_assetUriFactory is null)
			throw new PluginException(PluginErrorCodes.BridgeDenied, "插件资源服务未提供绝对入口");

		Uri sample = _assetUriFactory(pluginId, "web/index.html");
		if (!sample.IsAbsoluteUri)
			throw new PluginException(PluginErrorCodes.BridgeDenied, "插件资源服务必须提供绝对入口");

		Uri webRoot = new(sample, "./");
		Uri candidate;
		if (Uri.TryCreate(entryPoint, UriKind.Absolute, out Uri? absolute)) candidate = absolute;
		else
		{
			string relative = entryPoint.TrimStart('/');
			if (relative.StartsWith("web/", StringComparison.OrdinalIgnoreCase)) relative = relative[4..];
			candidate = new Uri(webRoot, relative);
		}
		if (!IsAllowedNavigation(candidate, webRoot))
			throw new PluginException(PluginErrorCodes.BridgeDenied, "插件窗口入口超出当前插件的 web 根目录");
		return candidate;
	}

	private static bool IsAllowedNavigation(Uri request, Uri webRoot)
	{
		if (!request.IsAbsoluteUri
			|| (request.Scheme != Uri.UriSchemeHttp && request.Scheme != Uri.UriSchemeHttps)
			|| !request.IsLoopback
			|| request.UserInfo.Length > 0
			|| request.Fragment.Length > 0
			|| !string.Equals(request.Scheme, webRoot.Scheme, StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(request.IdnHost, webRoot.IdnHost, StringComparison.OrdinalIgnoreCase)
			|| request.Port != webRoot.Port)
		{
			return false;
		}
		string rootPath = webRoot.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
			? webRoot.AbsolutePath
			: webRoot.AbsolutePath + "/";
		return request.AbsolutePath.StartsWith(rootPath, StringComparison.Ordinal);
	}

	private IReadOnlyList<PluginWebViewWindow> GetWindowsForPlugin(string pluginId) =>
		_windows.Values.Where(window => string.Equals(window.PluginId, pluginId, StringComparison.Ordinal)).ToArray();

	/// <summary>
	/// 关闭指定插件名下的全部窗口 (例如插件卸载或上下文租约撤销)
	/// </summary>
	public async Task CloseAllWindowsForPluginAsync(string pluginId, CancellationToken cancellationToken = default)
	{
		IReadOnlyList<PluginWebViewWindow> pluginWindows = GetWindowsForPlugin(pluginId);
		foreach (PluginWebViewWindow window in pluginWindows)
		{
			try
			{
				await window.CloseAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger?.Write(LogSource.Backend, "warn", $"关闭插件窗口 [{window.Label}] 发生异常: {ex.GetType().Name}");
			}
			finally
			{
				_windows.TryRemove(window.Label, out _);
			}
		}
	}

	/// <summary>
	/// 关闭并清空所有存活的插件窗口
	/// </summary>
	public async Task CloseAllAsync(CancellationToken cancellationToken = default)
	{
		PluginWebViewWindow[] windows = _windows.Values.ToArray();
		_windows.Clear();

		foreach (PluginWebViewWindow window in windows)
		{
			try
			{
				window.WindowClosed -= OnWindowClosed;
				await window.CloseAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger?.Write(LogSource.Backend, "warn", $"关闭插件窗口 [{window.Label}] 发生异常: {ex.GetType().Name}");
			}
		}
	}

	private void OnWindowClosed(PluginWebViewWindow window)
	{
		window.WindowClosed -= OnWindowClosed;
		if (_windows.TryRemove(window.Label, out _))
		{
			_logger?.Write(LogSource.Backend, "info", $"插件窗口已注销: {window.Label}");
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		await CloseAllAsync().ConfigureAwait(false);
	}
}
